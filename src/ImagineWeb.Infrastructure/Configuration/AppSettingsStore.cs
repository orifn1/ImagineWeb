using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Data;

namespace ImagineWeb.Infrastructure.Configuration;

public class AppSettingsStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AppSettingsStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<Dictionary<string, string?>> ReadAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
        return await db.AppSettings
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task WriteAllAsync(Dictionary<string, string?> values, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
        var now = DateTime.UtcNow;

        var existingKeys = await db.AppSettings
            .Where(s => values.Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var (key, value) in values)
        {
            if (existingKeys.TryGetValue(key, out var existing))
            {
                existing.Value = value;
                existing.UpdatedAt = now;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = now });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
