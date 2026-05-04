namespace ImagineWeb.Core.Models;

public class SeenUrl
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}
