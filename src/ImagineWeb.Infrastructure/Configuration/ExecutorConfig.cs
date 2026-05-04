namespace ImagineWeb.Infrastructure.Configuration;

public class ExecutorConfig
{
    public const string SectionName = "Executor";

    public string GitHubUsername { get; set; } = string.Empty;
    public string SolutionsBasePath { get; set; } = "solutions";
}
