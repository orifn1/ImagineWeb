namespace ImagineWeb.Infrastructure.Configuration;

public class BlobStorageConfig
{
    public const string SectionName = "BlobStorage";

    public bool Enabled { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "solutions";
    public int ArchiveRetentionDays { get; set; } = 30;
}
