namespace ImagineWeb.Core.Models;

/// <summary>
/// Configuration for the ImagineWeb application.
/// </summary>
public class HunterConfig
{
    public const string SectionName = "Hunter";

    /// <summary>
    /// Seed topics to start the autonomous discovery.
    /// </summary>
    public List<string> SeedTopics { get; set; } =
    [
        "profitable micro SaaS examples revenue numbers monthly",
        "failed startups 2025 2026 post-mortem lessons market gap",
        "API pricing comparison chart developer tools 2026",
        "niche website sold acquisition price domain value",
        "chrome extension revenue monthly users breakdown",
        "open data government API free commercial use list",
        "saas churn rate benchmarks by category 2025 2026",
        "indie hacker revenue report monthly earnings breakdown",
        "underserved market segments research data 2026",
        "product hunt launched revenue first month stats",
        "freelance marketplace rate comparison by skill 2026",
        "affiliate program commission rates comparison list",
        "no-code tool limitations workarounds developers need",
        "small business pain points survey results 2025 2026",
        "browser extension store gaps missing categories",
        "cost comparison calculator missing niche industry",
        "trending API integrations developer demand 2026",
        "comparison site profitable niche low competition",
        "workflow automation gaps between popular tools 2026",
        "solo developer profitable side project revenue stats"
    ];

    /// <summary>
    /// Maximum concurrent scraper threads.
    /// </summary>
    public int MaxScraperThreads { get; set; } = 5;

    /// <summary>
    /// Number of parallel search workers (more workers = faster topic coverage).
    /// </summary>
    public int SearchWorkerCount { get; set; } = 2;

    /// <summary>
    /// Maximum concurrent Ollama analysis requests (bottleneck control).
    /// </summary>
    public int MaxAnalysisConcurrency { get; set; } = 2;

    /// <summary>
    /// Bounded channel capacity for analysis queue.
    /// </summary>
    public int AnalysisQueueCapacity { get; set; } = 50;

    /// <summary>
    /// Maximum tokens to send to the AI model (~8000 tokens ≈ ~32000 chars).
    /// </summary>
    public int MaxContentChars { get; set; } = 32000;

    /// <summary>
    /// Delay between requests to the same domain (ms).
    /// </summary>
    public int PerDomainDelayMs { get; set; } = 2000;

    /// <summary>
    /// Maximum depth for recursive link following.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// Minimum best score (max of interestingness, profit) to trigger deep-dive into links.
    /// </summary>
    public int DeepDiveThreshold { get; set; } = 8;

    /// <summary>
    /// Minimum best score (max of interestingness, profit) to trigger Phase 2 deep analysis (value is exclusive: > threshold).
    /// </summary>
    public int Phase2Threshold { get; set; } = 7;

    public int CrossPageBatchSize { get; set; } = 20;

    /// <summary>
    /// Maximum total pages to discover before pausing search.
    /// </summary>
    public int MaxPagesPerSession { get; set; } = 1000;

    /// <summary>
    /// How often (in analyzed pages) the AI gets a strategy summary prompt.
    /// </summary>
    public int StrategySummaryInterval { get; set; } = 50;

    /// <summary>
    /// User-Agent string for web requests.
    /// </summary>
    public string UserAgent { get; set; } = "ImagineWeb/1.0 (Research Bot)";

    /// <summary>
    /// Domains to never scrape (login walls, social media, etc).
    /// </summary>
    public List<string> BlockedDomains { get; set; } =
    [
        "facebook.com", "instagram.com", "twitter.com", "x.com",
        "tiktok.com", "youtube.com", "linkedin.com", "pinterest.com",
        "accounts.google.com", "login.microsoftonline.com",
        "play.google.com", "apps.apple.com"
    ];

    public int PreScreenTopN { get; set; } = 5;
}
