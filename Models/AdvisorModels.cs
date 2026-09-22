namespace AmrPoultryFarmWeb.Models;

public enum InsightLevel { Good = 0, Info = 1, Warning = 2, Critical = 3 }

/// <summary>One actionable suggestion surfaced to the farmer, tied to a scored category
/// (FCR, Growth, Livability, Health Program, Environment).</summary>
public record AdvisorInsight(InsightLevel Level, string Category, string Title, string Message, List<string> Factors);

/// <summary>One 0-100 score behind the overall Flock Health Score, plotted as a bar in the UI.</summary>
public record ScoreComponent(string Label, double Score);

/// <summary>Output of <see cref="Services.FlockAdvisor"/> — a composite score plus the suggestions
/// that explain it, computed live from the batch's own records (nothing here is persisted).</summary>
public class AdvisorResult
{
    public double OverallScore { get; set; }
    public List<ScoreComponent> Components { get; set; } = new();
    public List<AdvisorInsight> Insights { get; set; } = new();
}
