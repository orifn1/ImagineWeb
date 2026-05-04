using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Resources;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class AzureQuotaChecker
{
    private readonly AzureDeployCredentials _creds;
    private readonly AzureSubscriptionDiscovery _discovery;
    private readonly ILogger<AzureQuotaChecker> _logger;

    public AzureQuotaChecker(
        IOptions<AzureDeployCredentials> creds,
        AzureSubscriptionDiscovery discovery,
        ILogger<AzureQuotaChecker> logger)
    {
        _creds = creds.Value;
        _discovery = discovery;
        _logger = logger;
    }

    public async Task<SubscriptionQuota> CheckQuotaAsync(CancellationToken ct)
    {
        var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
        if (subs.Count == 0) return new SubscriptionQuota();
        return await CheckQuotaForSubscriptionIdAsync(subs[0].Id, ct);
    }

    public async Task<SubscriptionQuota> CheckQuotaForSubscriptionAsync(
        string? subscriptionId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
            return await CheckQuotaForSubscriptionIdAsync(subscriptionId, ct);

        return await CheckQuotaAsync(ct);
    }

    public async Task<List<(string SubscriptionId, string Name, SubscriptionQuota Quota)>> CheckAllSubscriptionsAsync(CancellationToken ct)
    {
        var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
        var results = new List<(string, string, SubscriptionQuota)>();
        foreach (var sub in subs)
        {
            var quota = await CheckQuotaForSubscriptionIdAsync(sub.Id, ct);
            results.Add((sub.Id, sub.Name, quota));
        }
        return results;
    }

    public DiscoveredSubscription? FindAvailableSubscription(
        List<(string SubscriptionId, string Name, SubscriptionQuota Quota)> quotas,
        bool needsAppService, bool needsStaticWebApp)
    {
        foreach (var (subId, name, quota) in quotas)
        {
            if (needsAppService && !quota.CanDeployFreeAppService) continue;
            if (needsStaticWebApp && !quota.CanDeployFreeStaticWebApp) continue;
            return new DiscoveredSubscription(subId, name);
        }
        return null;
    }

    private async Task<SubscriptionQuota> CheckQuotaForSubscriptionIdAsync(string subscriptionId, CancellationToken ct)
    {
        if (!_creds.IsConfigured) return new SubscriptionQuota();

        try
        {
            var credential = new ClientSecretCredential(_creds.TenantId, _creds.ClientId, _creds.ClientSecret);
            var client = new ArmClient(credential);
            var subscription = client.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(subscriptionId));

            int freePlans = 0, freeStaticApps = 0;

            await foreach (var rg in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
            {
                try
                {
                    await foreach (var plan in rg.GetAppServicePlans().GetAllAsync(cancellationToken: ct))
                    {
                        if (plan.Data.Sku?.Name is "F1" or "FREE")
                            freePlans++;
                    }

                    await foreach (var site in rg.GetStaticSites().GetAllAsync(cancellationToken: ct))
                    {
                        if (site.Data.Sku?.Name?.Equals("Free", StringComparison.OrdinalIgnoreCase) == true)
                            freeStaticApps++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not list resources in RG {Name}", rg.Data.Name);
                }
            }

            return new SubscriptionQuota
            {
                FreeAppServicePlansUsed = freePlans,
                FreeStaticWebAppsUsed = freeStaticApps,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check quota for subscription {Sub}", subscriptionId);
            return new SubscriptionQuota();
        }
    }

    public async Task<List<string>> GetExistingFreeAppServicePlansAsync(CancellationToken ct)
    {
        if (!_creds.IsConfigured) return [];

        var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
        var plans = new List<string>();
        var credential = new ClientSecretCredential(_creds.TenantId, _creds.ClientId, _creds.ClientSecret);
        var client = new ArmClient(credential);

        foreach (var sub in subs)
        {
            try
            {
                var subscription = client.GetSubscriptionResource(
                    SubscriptionResource.CreateResourceIdentifier(sub.Id));

                await foreach (var rg in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
                {
                    try
                    {
                        await foreach (var plan in rg.GetAppServicePlans().GetAllAsync(cancellationToken: ct))
                        {
                            if (plan.Data.Sku?.Name is "F1" or "FREE")
                            {
                                var appCount = 0;
                                await foreach (var _ in plan.GetWebAppsAsync(cancellationToken: ct))
                                    appCount++;

                                plans.Add($"[{sub.Name}] {rg.Data.Name}/{plan.Data.Name} ({appCount} app(s))");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list App Service plans for subscription {Sub}", sub.Id);
            }
        }
        return plans;
    }
}
