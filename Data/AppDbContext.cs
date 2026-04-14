using Microsoft.EntityFrameworkCore;

namespace TimeTableWebAPI.Data;

public class TimetableEntry
{
    public int Id { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Lecturer { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Lecture, Lab, etc.
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TimetableEntry> TimetableEntries => Set<TimetableEntry>();
}
