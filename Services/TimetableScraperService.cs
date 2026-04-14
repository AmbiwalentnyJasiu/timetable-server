using ExcelDataReader;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using TimeTableWebAPI.Data;

namespace TimeTableWebAPI.Services;

public class TimetableScraperService(
  IHttpClientFactory httpClientFactory,
  ILogger<TimetableScraperService> logger,
  IServiceProvider serviceProvider ) : BackgroundService
{
  private const string TargetUrl = "https://it.pk.edu.pl/studenci/na-studiach/rozklady-zajec/";
  private string? _lastKnownLink;

  protected override async Task ExecuteAsync( CancellationToken stoppingToken )
  {
    logger.LogInformation( "TimetableScraperService is starting." );

    // Register provider for ExcelDataReader
    System.Text.Encoding.RegisterProvider( System.Text.CodePagesEncodingProvider.Instance );

    while ( !stoppingToken.IsCancellationRequested )
    {
      try
      {
        await ScrapeTimetableAsync( stoppingToken );
      }
      catch ( Exception ex )
      {
        logger.LogError( ex, "An error occurred while scraping the timetable." );
      }

      // Wait for 1 hour before next scrape
      await Task.Delay( TimeSpan.FromHours( 1 ), stoppingToken );
    }
  }

  private async Task ScrapeTimetableAsync( CancellationToken stoppingToken )
  {
    logger.LogInformation( "Scraping timetable from {Url}", TargetUrl );

    using var client = httpClientFactory.CreateClient();
    var response = await client.GetStringAsync( TargetUrl, stoppingToken );

    var doc = new HtmlDocument();
    doc.LoadHtml( response );

    var linkNode = doc.DocumentNode.SelectSingleNode( "//h2[contains(text(), 'STUDIA NIESTACJONARNE')]/following::h4[contains(., 'Informatyka')]/following::a[contains(@href, '.xls')]" )
                   ?? doc.DocumentNode.SelectSingleNode( "//a[contains(@href, 'NIESTACJONARNE') and contains(@href, 'INFORMATYKA') and contains(@href, '.xls')]" );

    if ( linkNode != null )
    {
      var href = linkNode.GetAttributeValue( "href", "" );
      if ( !string.IsNullOrEmpty( href ) )
      {
        if ( href != _lastKnownLink )
        {
          logger.LogInformation( "New timetable link found: {Link}", href );
          _lastKnownLink = href;
          await ProcessTimetableAsync( href, stoppingToken );
        }
        else
        {
          logger.LogInformation( "Timetable link has not changed." );
        }
      }
    }
    else
    {
      logger.LogWarning( "Could not find the timetable link on the page." );
    }
  }

  private string FormatDate( object? dateValue )
  {
      if ( dateValue == null ) return "";
      
      if ( dateValue is DateTime dt )
      {
          return dt.ToString( "dd.MM.yyyy" );
      }

      var dateStr = dateValue.ToString() ?? "";
      if ( DateTime.TryParse( dateStr, out var parsedDate ) )
      {
          return parsedDate.ToString( "dd.MM.yyyy" );
      }

      return dateStr;
  }

  private async Task ProcessTimetableAsync( string url, CancellationToken stoppingToken )
  {
    try
    {
      using var client = httpClientFactory.CreateClient();
      var data = await client.GetByteArrayAsync( url, stoppingToken );
      using var stream = new MemoryStream( data );

      using var reader = ExcelReaderFactory.CreateReader( stream );
      var result = reader.AsDataSet();

      var entries = new List<TimetableEntry>();
      var groupNames = new[] { "CY1", "CY2", "CY3", "CY4", "DS1", "DS2" };

      foreach ( System.Data.DataTable table in result.Tables )
      {
        int rowIdx = 7; // Start from row index 7
        while ( rowIdx < table.Rows.Count )
        {
          // Process 4 blocks
          for ( int block = 0; block < 4 && rowIdx < table.Rows.Count; block++ )
          {
            var row = table.Rows[rowIdx];
            if ( row.ItemArray.Length < 25 )
            {
              rowIdx += 3;
              continue;
            }

            // Extract date (Heuristic: usually in column 1 (index 0) or nearby)
            // It might be merged over multiple rows, so we search up or keep it.
            object? dateObj = row[0];
            if ( dateObj == null || string.IsNullOrWhiteSpace( dateObj.ToString() ) )
            {
                // Try looking back up for merged cell value
                for (int d = rowIdx; d >= 7; d--)
                {
                    var dRow = table.Rows[d];
                    dateObj = dRow[0];
                    if (dateObj != null && !string.IsNullOrWhiteSpace(dateObj.ToString())) break;
                }
            }

            string date = FormatDate( dateObj );

            string time = row[17]?.ToString() ?? ""; // Column index 17

            for ( int colIdx = 19; colIdx <= 24; colIdx++ ) // Columns index 19-24
            {
              string cellContent = row[colIdx]?.ToString() ?? "";
              string groupName = groupNames[colIdx - 19];

              // Check if this cell is already filled (by a shared "ćwiczenia" or "lecture" from a previous column)
              if ( entries.Any( e => e.Date == date && e.Time == time && e.Group == groupName ) )
              {
                  // If we already filled this, it's either from a shared ćwiczenia or shared lecture
                  // If it's a shared lecture, we can continue to use it for next groups if the rules allow it.
                  continue;
              }

              if ( !string.IsNullOrWhiteSpace( cellContent ) )
              {
                var entry = ParseCellContent( cellContent );
                if ( entry.Subject.Contains( "BRAK ZAJĘĆ", StringComparison.OrdinalIgnoreCase ) )
                {
                    continue; // Skip blocks marked as "No classes"
                }

                entry.Date = date;
                entry.Time = time;
                entry.Group = groupName;
                entries.Add( entry );

                bool isLecture = entry.Type.Contains( "wyk", StringComparison.OrdinalIgnoreCase ) || 
                                 entry.Type.Contains( "Lecture", StringComparison.OrdinalIgnoreCase );
                bool isExercises = entry.Type.Contains( "ćwicz", StringComparison.OrdinalIgnoreCase ) ||
                                   entry.Type.Contains( "Exercise", StringComparison.OrdinalIgnoreCase );
                bool isBezpiecz = entry.Subject.Contains( "bezpiecz", StringComparison.OrdinalIgnoreCase );

                if ( isLecture )
                {
                  // Rule: "bezpiecz" lectures are shared only by CY groups, don't stretch them to DS1 and DS2
                  // DS1 is group names index 4, groupNames is CY1, CY2, CY3, CY4, DS1, DS2
                  // So we stop sharing if we reach DS1 (colIdx 23)
                  int endIdx = isBezpiecz ? 22 : 24; // colIdx 19-22 are CY1-CY4
                  
                  for ( int nextCol = colIdx + 1; nextCol <= endIdx; nextCol++ )
                  {
                    string nextCellContent = row[nextCol]?.ToString() ?? "";
                    if ( string.IsNullOrWhiteSpace( nextCellContent ) )
                    {
                      var sharedEntry = new TimetableEntry
                      {
                        Date = entry.Date,
                        Time = entry.Time,
                        Subject = entry.Subject,
                        Lecturer = entry.Lecturer,
                        Room = entry.Room,
                        Type = entry.Type,
                        Group = groupNames[nextCol - 19]
                      };
                      entries.Add( sharedEntry );
                    }
                    else
                    {
                        break;
                    }
                  }
                }
                else if ( isExercises )
                {
                  // "ćwiczenia" is shared by groups CY1, CY2 and CY3 or CY4, DS1 and DS2
                  // colIdx 19-21 (CY1-CY3) or 22-24 (CY4-DS2)
                  int endIdx = colIdx <= 21 ? 21 : 24;
                  for ( int nextCol = colIdx + 1; nextCol <= endIdx; nextCol++ )
                  {
                    string nextCellContent = row[nextCol]?.ToString() ?? "";
                    if ( string.IsNullOrWhiteSpace( nextCellContent ) )
                    {
                      var sharedEntry = new TimetableEntry
                      {
                        Date = entry.Date,
                        Time = entry.Time,
                        Subject = entry.Subject,
                        Lecturer = entry.Lecturer,
                        Room = entry.Room,
                        Type = entry.Type,
                        Group = groupNames[nextCol - 19]
                      };
                      entries.Add( sharedEntry );
                    }
                    else
                    {
                        // Stop if we find something in the next cell
                        break;
                    }
                  }
                }
              }
            }

            rowIdx += 3; // Each block is 4 rows wide
          }

          rowIdx += 1; // Skip the singular irrelevant row after 4 blocks
        }
      }

      if ( entries.Any() )
      {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // For simplicity, clear old data and insert new.
        // In a real app, you might want to merge or use versioning.
        await db.Database.EnsureCreatedAsync( stoppingToken );
        db.TimetableEntries.RemoveRange( db.TimetableEntries );
        await db.TimetableEntries.AddRangeAsync( entries, stoppingToken );
        await db.SaveChangesAsync( stoppingToken );

        logger.LogInformation( "Successfully processed and saved {Count} timetable entries.", entries.Count );
      }
    }
    catch ( Exception ex )
    {
      logger.LogError( ex, "Failed to process timetable from {Url}", url );
    }
  }

  private TimetableEntry ParseCellContent( string content )
  {
    // The user says: "All the info (type of class, subject, room, lecturer) about singular block... is in one cell."
    
    // Check for "BRAK ZAJĘĆ" early
    if ( content.Contains( "BRAK ZAJĘĆ", StringComparison.OrdinalIgnoreCase ) )
    {
        return new TimetableEntry { Subject = "BRAK ZAJĘĆ" };
    }

    // Handle "ZDALNIE" and "wykład/wyklad" - it might be joined by only one space (e.g., "Lecturer ZDALNIE")
    // Let's replace these with a clear separator so they split correctly
    content = System.Text.RegularExpressions.Regex.Replace( content, @"(?i)\s*ZDALNIE\s*", "\nZDALNIE\n" );
    content = System.Text.RegularExpressions.Regex.Replace( content, @"(?i)\s*(?<!s\.\s*|sala\s*)(wyk\w*ad)\b\s*", "\n$1\n" );

    // Usually these are separated by newlines, or sometimes multiple spaces (2 or more).
    
    // Sometimes there's stray time like "16:45-18:15" at the start of cell content.
    // Let's remove any such time pattern if it appears at the beginning of a line.
    var lines = System.Text.RegularExpressions.Regex.Split( content, @"[\n\r]|\s{2,}" )
                       .Select( l => l.Trim() )
                       .Select( l => System.Text.RegularExpressions.Regex.Replace( l, @"^\d{1,2}:\d{2}-\d{1,2}:\d{2}\s*", "" ) )
                       .Where( l => !string.IsNullOrWhiteSpace( l ) )
                       .ToArray();

    var entry = new TimetableEntry();

    if ( lines.Length > 0 ) entry.Subject = lines[0];
    if ( lines.Length > 1 ) entry.Type = lines[1];
    
    // Sometimes Room and Lecturer order might vary or be on the same line.
    // Heuristic: Room usually starts with a letter and numbers (e.g. A-1, L-10) or "sala"
    // Lecturer is usually a name.
    
    for ( int i = 2; i < lines.Length; i++ )
    {
        string line = lines[i];
        if ( line.Equals( "ZDALNIE", StringComparison.OrdinalIgnoreCase ) )
        {
            entry.Room = "ZDALNIE";
        }
        else if ( string.IsNullOrEmpty( entry.Room ) && line.Any( char.IsDigit ) )
        {
            entry.Room = line;
        }
        else if ( string.IsNullOrEmpty( entry.Lecturer ) )
        {
            entry.Lecturer = line;
        }
        else if ( string.IsNullOrEmpty( entry.Room ) )
        {
            entry.Room = line;
        }
    }

    return entry;
  }
}
