using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IContentQualityScorer
{
    ContentQuality Score(string html, string extractedText);
}
