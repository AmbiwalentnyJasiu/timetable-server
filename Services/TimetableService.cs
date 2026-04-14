using Microsoft.EntityFrameworkCore;
using TimeTableWebAPI.Data;

namespace TimeTableWebAPI.Services;

public static class TimetableConstants
{
    public static readonly string[] AllowedGroups = ["CY1", "CY2", "CY3", "CY4", "DS1", "DS2"];
}

public interface ITimetableService
{
    Task<List<TimetableEntry>> GetAllEntriesAsync();
    Task<List<TimetableEntry>> GetEntriesByGroupAsync( string group );
    Task<List<TimetableEntry>> GetEntriesByGroupAndDateAsync( string group, string date );
    Task<List<TimetableEntry>> GetEntriesForWeekendAsync( string group, DateTime date );
}

public class TimetableService( AppDbContext db ) : ITimetableService
{
    public async Task<List<TimetableEntry>> GetAllEntriesAsync()
    {
        return await db.TimetableEntries.ToListAsync();
    }

    public async Task<List<TimetableEntry>> GetEntriesByGroupAsync( string group )
    {
        if ( !TimetableConstants.AllowedGroups.Contains( group ) )
        {
            return [];
        }

        return await db.TimetableEntries
            .Where( e => e.Group == group )
            .ToListAsync();
    }

    public async Task<List<TimetableEntry>> GetEntriesByGroupAndDateAsync( string group, string date )
    {
        if ( !TimetableConstants.AllowedGroups.Contains( group ) )
        {
            return [];
        }

        return await db.TimetableEntries
            .Where( e => e.Group == group && e.Date == date )
            .ToListAsync();
    }

    public async Task<List<TimetableEntry>> GetEntriesForWeekendAsync( string group, DateTime date )
    {
        if ( !TimetableConstants.AllowedGroups.Contains( group ) )
        {
            return [];
        }

        // Calculate Saturday and Sunday for the week of the given date
        var diff = ( int )date.DayOfWeek;
        // Adjust for Monday-start week (0 = Sunday, 1 = Monday, ..., 6 = Saturday)
        // If it's Sunday (0), we want to find the previous Saturday.
        // If it's anything else, we find the upcoming Saturday.
        
        // Let's define weekend as the Saturday and Sunday of the week containing 'date'.
        // Standard .NET DayOfWeek: Sunday = 0, Monday = 1, ..., Saturday = 6.
        
        DateTime saturday;
        if ( date.DayOfWeek == DayOfWeek.Sunday )
        {
            saturday = date.AddDays( -1 );
        }
        else
        {
            saturday = date.AddDays( 6 - diff );
        }
        
        var sunday = saturday.AddDays( 1 );

        var saturdayStr = saturday.ToString( "dd.MM.yyyy" );
        var sundayStr = sunday.ToString( "dd.MM.yyyy" );

        return await db.TimetableEntries
            .Where( e => e.Group == group && ( e.Date == saturdayStr || e.Date == sundayStr ) )
            .ToListAsync();
    }
}
