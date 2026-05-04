using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class AzureRetailPricingClient
{
    private const string BaseUrl = "https://prices.azure.com/api/retail/prices";

    private readonly HttpClient _http;
    private readonly ILogger<AzureRetailPricingClient> _logger;

    public AzureRetailPricingClient(HttpClient http, ILogger<AzureRetailPricingClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<decimal> GetMonthlyPriceAsync(
        string serviceName, string skuName, string region, CancellationToken ct)
    {
        var filter = $"serviceName eq '{serviceName}' and skuName eq '{skuName}' " +
                     $"and armRegionName eq '{region}' and priceType eq 'Consumption'";
        var url = $"{BaseUrl}?$filter={Uri.EscapeDataString(filter)}&meterRegion='primary'";

        try
        {
            var response = await _http.GetFromJsonAsync<PricingApiResponse>(url, ct);
            if (response?.Items is not { Count: > 0 }) return 0;

            var item = response.Items[0];
            return EstimateMonthly(item.RetailPrice, item.UnitOfMeasure);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch price for {Service}/{Sku} in {Region}", serviceName, skuName, region);
            return 0;
        }
    }

    public async Task<decimal> GetAppServiceMonthlyPriceAsync(
        string skuName, string region, CancellationToken ct)
    {
        return await GetMonthlyPriceAsync("Azure App Service", skuName, region, ct);
    }

    public async Task<decimal> GetStaticWebAppMonthlyPriceAsync(
        string skuName, string region, CancellationToken ct)
    {
        return await GetMonthlyPriceAsync("Azure Static Web Apps", skuName, region, ct);
    }

    private static decimal EstimateMonthly(decimal unitPrice, string unitOfMeasure)
    {
        if (unitOfMeasure.Contains("Hour", StringComparison.OrdinalIgnoreCase))
            return unitPrice * 730; // avg hours/month
        if (unitOfMeasure.Contains("Day", StringComparison.OrdinalIgnoreCase))
            return unitPrice * 30;
        if (unitOfMeasure.Contains("Month", StringComparison.OrdinalIgnoreCase))
            return unitPrice;
        return unitPrice;
    }

    private sealed class PricingApiResponse
    {
        [JsonPropertyName("Items")]
        public List<PricingItem> Items { get; set; } = [];
    }

    private sealed class PricingItem
    {
        [JsonPropertyName("retailPrice")]
        public decimal RetailPrice { get; set; }

        [JsonPropertyName("unitOfMeasure")]
        public string UnitOfMeasure { get; set; } = "";

        [JsonPropertyName("skuName")]
        public string SkuName { get; set; } = "";

        [JsonPropertyName("meterName")]
        public string MeterName { get; set; } = "";

        [JsonPropertyName("serviceName")]
        public string ServiceName { get; set; } = "";
    }
}
