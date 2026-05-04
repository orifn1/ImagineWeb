namespace ImagineWeb.Core.Models;

/// <summary>
/// Real-time status of the hunting pipeline.
/// </summary>
public class HunterStatus
{
    public bool IsRunning { get; set; }
    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.None;
    public DateTime? StartedAt { get; set; }
    public TimeSpan Uptime => StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;

    // Counters
    public int TotalTopicsGenerated { get; set; }
    public int TopicsSearched { get; set; }
    public int TotalPagesDiscovered { get; set; }
    public int PagesScraped { get; set; }
    public int PagesAnalyzed { get; set; }
    public int PagesFailed { get; set; }
    public int HighValueFindings { get; set; }

    // Queue depths
    public int SearchQueueDepth { get; set; }
    public int ScrapeQueueDepth { get; set; }
    public int AnalysisQueueDepth { get; set; }

    // Performance
    public double AvgAnalysisTimeMs { get; set; }
    public double AvgScrapeTimeMs { get; set; }

    public string CurrentActivity { get; set; } = "Idle";
}

public enum ShutdownMode
{
    None,
    Graceful,
    Immediate
}
