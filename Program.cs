using Microsoft.EntityFrameworkCore;
using TimeTableWebAPI.Data;
using TimeTableWebAPI.Services;

var builder = WebApplication.CreateBuilder( args );

var connectionString = builder.Configuration.GetConnectionString( "DefaultConnection" );

// Fallback to AzureSqlConnection if DefaultConnection is not provided in a non-dev environment
if ( !builder.Environment.IsDevelopment() && string.IsNullOrEmpty( connectionString ) )
{
    connectionString = builder.Configuration.GetConnectionString( "AzureSqlConnection" );
}

// Ensure the connection string is provided for production
if ( !builder.Environment.IsDevelopment() && string.IsNullOrEmpty( connectionString ) )
{
    throw new InvalidOperationException( "DefaultConnection is not configured for the production environment." );
}

// Add services to the container.
builder.Services.AddCors( options =>
{
    options.AddDefaultPolicy( policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    } );
} );

builder.Services.AddDbContext<AppDbContext>( options =>
{
    if ( builder.Environment.IsDevelopment() )
    {
        options.UseSqlite( connectionString );
    }
    else
    {
        options.UseSqlServer( connectionString );
    }
} );

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddHttpClient();
builder.Services.AddHostedService<TimetableScraperService>();
builder.Services.AddScoped<ITimetableService, TimetableService>();

try
{
    var app = builder.Build();

    // Log the environment and connection string presence
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation( "Starting app in {Environment} mode", app.Environment.EnvironmentName );
    logger.LogInformation( "Using {Provider} database provider", app.Environment.IsDevelopment() ? "SQLite" : "SQL Server" );
    logger.LogInformation( "Connection string present: {IsPresent}", !string.IsNullOrEmpty( connectionString ) );

    // Configure the HTTP request pipeline.
    if ( app.Environment.IsDevelopment() )
    {
      app.MapOpenApi();
    }

    app.UseCors();

    app.UseHttpsRedirection();

    app.MapGet( "/timetable", async ( ITimetableService timetableService ) =>
      await timetableService.GetAllEntriesAsync() )
      .WithName( "GetTimetable" );

    app.MapGet( "/timetable/group/{group}", async ( string group, ITimetableService timetableService ) =>
      await timetableService.GetEntriesByGroupAsync( group ) )
      .WithName( "GetGroupTimetable" );

    app.MapGet( "/timetable/group/{group}/weekend/{date}", async ( string group, string date, ITimetableService timetableService ) =>
    {
        if ( DateTime.TryParse( date, out var weekendDate ) )
        {
            return Results.Ok( await timetableService.GetEntriesForWeekendAsync( group, weekendDate ) );
        }
        return Results.BadRequest( "Invalid date format. Use YYYY-MM-DD" );
    } )
    .WithName( "GetGroupWeekendTimetable" );

    app.Run();
}
catch ( Exception ex )
{
    // Ensure the exception is logged to console during startup
    Console.WriteLine( $"Fatal error during app startup: {ex}" );
    throw;
}
