using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class ClarificationSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<string, ClarificationSession> _sessions = new();
    private readonly string _basePath;
    private readonly ILogger<ClarificationSessionStore> _logger;

    public ClarificationSessionStore(IOptions<ExecutorConfig> config, ILogger<ClarificationSessionStore> logger)
    {
        _logger = logger;
        _basePath = Path.Combine(AppContext.BaseDirectory, config.Value.SolutionsBasePath);
        LoadFromDisk();
    }

    public ClarificationSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public void Set(ClarificationSession session)
    {
        _sessions[session.Id] = session;
        SaveToDisk(session);
    }

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out _)) return false;
        return true;
    }

    public IReadOnlyList<ClarificationSession> GetAll() =>
        _sessions.Values.OrderByDescending(s => s.CreatedAt).ToList();

    private void SaveToDisk(ClarificationSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.SolutionPath)) return;
            var metaDir = Path.Combine(session.SolutionPath, ".meta");
            Directory.CreateDirectory(metaDir);
            var json = JsonSerializer.Serialize(session, JsonOpts);
            File.WriteAllText(Path.Combine(metaDir, "clarification.json"), json);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist clarification session {Id}", session.Id); }
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        var searchDirs = new List<string>();

        // Scan platform subfolders (Azure/, Android/)
        foreach (var subDir in new[] { "Azure", "Android" })
        {
            var platformDir = Path.Combine(_basePath, subDir);
            if (Directory.Exists(platformDir))
                searchDirs.AddRange(Directory.GetDirectories(platformDir, "clarify-*"));
        }

        // Also scan root for legacy sessions (pre-migration)
        searchDirs.AddRange(Directory.GetDirectories(_basePath, "clarify-*"));

        foreach (var dir in searchDirs)
        {
            var file = Path.Combine(dir, ".meta", "clarification.json");
            if (!File.Exists(file))
            {
                file = Path.Combine(dir, "clarification.json");
                if (!File.Exists(file)) continue;
            }

            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<ClarificationSession>(json, JsonOpts);
                if (session is not null)
                {
                    session.SolutionPath = dir;
                    _sessions[session.Id] = session;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load clarification session from {Path}", file); }
        }

        _logger.LogInformation("Loaded {Count} clarification sessions from disk", _sessions.Count);
    }
}
