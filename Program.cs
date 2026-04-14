using Microsoft.EntityFrameworkCore;
using TimeTableWebAPI.Data;
using TimeTableWebAPI.Services;

var builder = WebApplication.CreateBuilder( args );

var connectionString = builder.Configuration.GetConnectionString( "DefaultConnection" );

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

var app = builder.Build();

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
