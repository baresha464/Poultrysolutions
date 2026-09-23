using System.ComponentModel.DataAnnotations;

namespace AmrPoultryFarmWeb.Models;

public enum DemoRequestStatus { New = 0, Contacted = 1, Converted = 2, Closed = 3 }

/// <summary>A "Request a demo" lead from the public landing page. Platform-level (not tied to any
/// client) and only visible to the Super Admin.</summary>
public class DemoRequest
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    [Required, MaxLength(20)]
    public string Phone { get; set; } = "";

    [MaxLength(100)]
    public string Location { get; set; } = "";        // village / mandal / district

    public int? Sheds { get; set; }
    public int? BirdsPerBatch { get; set; }

    [MaxLength(80)]
    public string Integrator { get; set; } = "";

    [MaxLength(500)]
    public string Message { get; set; } = "";

    [MaxLength(5)]
    public string Language { get; set; } = "en";

    public DemoRequestStatus Status { get; set; } = DemoRequestStatus.New;

    [MaxLength(500)]
    public string AdminNotes { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
