using System.Reflection;

namespace ImagineWeb.Infrastructure.Analysis;

public static class PromptTemplates
{
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly Lock CacheLock = new();

    public static string Phase1 => Load("Phase1");
    public static string Phase2 => Load("Phase2");
    public static string Strategy => Load("Strategy");
    public static string CrossPage => Load("CrossPage");
    public static string TopicPruning => Load("TopicPruning");
    public static string PreScreen => Load("PreScreen");

    private static string Load(string name)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(name, out var cached))
                return cached;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ImagineWeb.Infrastructure.Analysis.Prompts.{name}.txt";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        lock (CacheLock) { Cache[name] = content; }
        return content;
    }
}
