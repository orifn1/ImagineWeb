using Azure.Identity;
using Azure.ResourceManager;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class AzureSubscriptionDiscovery
{
    private readonly AzureDeployCredentials _creds;
    private readonly ILogger<AzureSubscriptionDiscovery> _logger;

    private List<DiscoveredSubscription>? _cache;
    private DateTime _cacheTime;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public AzureSubscriptionDiscovery(
        IOptions<AzureDeployCredentials> creds,
        ILogger<AzureSubscriptionDiscovery> logger)
    {
        _creds = creds.Value;
        _logger = logger;
    }

    public async Task<List<DiscoveredSubscription>> GetDeploymentSubscriptionsAsync(CancellationToken ct)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheTime < CacheDuration)
            return _cache;

        if (!_creds.IsConfigured)
        {
            _logger.LogWarning("Azure deployment credentials not configured");
            return [];
        }

        var credential = new ClientSecretCredential(_creds.TenantId, _creds.ClientId, _creds.ClientSecret);
        var client = new ArmClient(credential);
        var result = new List<DiscoveredSubscription>();

        _logger.LogInformation("Discovering Azure subscriptions for tenant {TenantId}...", _creds.TenantId);

        await foreach (var sub in client.GetSubscriptions().GetAllAsync(ct))
        {
            if (_creds.ExcludedSubscriptionIds.Any(ex =>
                    ex.Equals(sub.Data.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Excluded subscription {Name} ({Id})", sub.Data.DisplayName, sub.Data.SubscriptionId);
                continue;
            }

            result.Add(new DiscoveredSubscription(sub.Data.SubscriptionId, sub.Data.DisplayName));
            _logger.LogInformation("Discovered subscription: {Name} ({Id})", sub.Data.DisplayName, sub.Data.SubscriptionId);
        }

        _logger.LogInformation("Found {Count} deployment-eligible subscription(s)", result.Count);

        _cache = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    public void InvalidateCache() => _cache = null;
}

public record DiscoveredSubscription(string Id, string Name);
