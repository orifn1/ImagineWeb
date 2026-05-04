namespace ImagineWeb.Core.Models;

/// <summary>
/// A search topic managed by the AI-driven discovery engine.
/// </summary>
public class SearchTopic
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// AI-assigned priority (1-10). Higher = more promising.
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// Who suggested this topic: "seed", "ai", "user".
    /// </summary>
    public string Origin { get; set; } = "seed";

    public TopicStatus Status { get; set; } = TopicStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SearchedAt { get; set; }
    public int ResultCount { get; set; }

    /// <summary>
    /// Number of times the AI re-suggested this or similar topic.
    /// </summary>
    public int ReinforceCount { get; set; }

    public double AvgPageScore { get; set; }
    public int HighValueCount { get; set; }
    public int TotalPagesProduced { get; set; }
    public SearchStrategy Strategy { get; set; } = SearchStrategy.Broad;
}

public enum TopicStatus
{
    Pending,
    Searching,
    Searched,
    Exhausted,
    Failed
}
