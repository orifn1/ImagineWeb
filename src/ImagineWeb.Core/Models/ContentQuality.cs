namespace ImagineWeb.Core.Models;

public class ContentQuality
{
    public double TextToHtmlRatio { get; set; }
    public int StructuredDataCount { get; set; }
    public bool HasPaywall { get; set; }
    public bool HasFreshContent { get; set; }
    public int QualityScore { get; set; }

    public static int MinQualityForAnalysis => 3;
}
