using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ImagineWeb.Infrastructure.Execution;

/// <summary>
/// Shared helpers for code generators that emit code as fenced markdown blocks
/// (Ollama, OpenAI, Anthropic, etc. — anything not session-driven like Copilot SDK).
/// </summary>
internal static class CodeBlockFileExtractor
{
    public static string BuildSystemMessage(string workingDirectory, string? systemAppend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert web developer. Generate complete, production-ready code.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL OUTPUT FORMAT RULES:");
        sb.AppendLine("- Output ALL files using fenced code blocks with the FULL file path as the info string.");
        sb.AppendLine($"- Every file path MUST start with: {workingDirectory}");
        sb.AppendLine($"- Application files go into: {Path.Combine(workingDirectory, "site")}");
        sb.AppendLine("- Example:");
        sb.AppendLine($"  ```html {Path.Combine(workingDirectory, "site", "index.html")}");
        sb.AppendLine("  <!DOCTYPE html>...");
        sb.AppendLine("  ```");
        sb.AppendLine();
        sb.AppendLine("- Generate ALL necessary files: HTML, CSS, JavaScript, assets.");
        sb.AppendLine("- Do NOT use placeholder content — generate real, working content.");
        sb.AppendLine("- Do NOT explain the code — just output the files.");

        if (!string.IsNullOrEmpty(systemAppend))
        {
            sb.AppendLine();
            sb.AppendLine(systemAppend);
        }

        return sb.ToString();
    }

    public static int ExtractAndWriteFiles(string response, string workingDirectory, string generationId, ILogger logger)
    {
        var filePattern = new Regex(
            @"```[\w]*\s+(" + Regex.Escape(workingDirectory).Replace("\\\\", @"[\\/]") + @"[\\/][^\n`]+)\s*\n([\s\S]*?)```",
            RegexOptions.Multiline);

        var matches = filePattern.Matches(response);
        var filesWritten = 0;

        if (matches.Count == 0)
        {
            var fallbackPattern = new Regex(
                @"```[\w]*\s*((?:site[\\/])?[\w\-./\\]+\.(?:html|css|js|json|svg|md|txt|xml|ico|webmanifest))\s*\n([\s\S]*?)```",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            matches = fallbackPattern.Matches(response);
            foreach (Match match in matches)
            {
                var relativePath = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(workingDirectory, relativePath);
                WriteFile(filePath, match.Groups[2].Value, generationId, logger);
                filesWritten++;
            }
        }
        else
        {
            foreach (Match match in matches)
            {
                var filePath = match.Groups[1].Value.Trim();
                WriteFile(filePath, match.Groups[2].Value, generationId, logger);
                filesWritten++;
            }
        }

        logger.LogInformation("Generation {Id}: extracted and wrote {Count} files from response", generationId, filesWritten);
        return filesWritten;
    }

    private static void WriteFile(string filePath, string content, string generationId, ILogger logger)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, content);
            logger.LogInformation("Generation {Id}: wrote file {Path}", generationId, filePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Generation {Id}: failed to write file {Path}", generationId, filePath);
        }
    }
}
