using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class CopilotPromptGenerator : ICopilotPromptGenerator
{
    private readonly IPageAnalyzer _analyzer;
    private readonly ILlmGate _llmGate;
    private readonly ExecutorConfig _config;
    private readonly ILogger<CopilotPromptGenerator> _logger;

    public CopilotPromptGenerator(
        IPageAnalyzer analyzer,
        ILlmGate llmGate,
        IOptions<ExecutorConfig> config,
        ILogger<CopilotPromptGenerator> logger)
    {
        _analyzer = analyzer;
        _llmGate = llmGate;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> GeneratePromptFileAsync(DiscoveredPage page, CancellationToken ct)
    {
        var solutionDir = Path.Combine(
            AppContext.BaseDirectory,
            _config.SolutionsBasePath,
            $"solution-{page.Id}");

        Directory.CreateDirectory(solutionDir);

        var prompt = await BuildPromptAsync(page, ct);
        var promptPath = Path.Combine(solutionDir, "prompt.md");
        await File.WriteAllTextAsync(promptPath, prompt, ct);

        var monetizationConfig = BuildMonetizationConfig(page);
        var configPath = Path.Combine(solutionDir, "monetization.json");
        await File.WriteAllTextAsync(configPath, monetizationConfig, ct);

        _logger.LogInformation("Generated Copilot prompt at {Path} for page {Id}", promptPath, page.Id);
        return solutionDir;
    }

    private async Task<string> BuildPromptAsync(DiscoveredPage page, CancellationToken ct)
    {
        var refinedPlan = await RefineActionPlanAsync(page, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Build: {page.Title}");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"- **Source URL:** {page.Url}");
        sb.AppendLine($"- **Opportunity Type:** {page.OpportunityType}");
        sb.AppendLine($"- **Profit Score:** {page.ProfitScore}/10");
        sb.AppendLine($"- **Feasibility:** {page.FeasibilityScore}/10");
        sb.AppendLine($"- **Estimated Reward:** {page.EstimatedReward}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.TargetAudience))
        {
            sb.AppendLine("## Target Audience");
            sb.AppendLine(page.TargetAudience);
            sb.AppendLine();
        }

        sb.AppendLine("## What to Build");
        sb.AppendLine(page.OpportunityReason);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.Differentiator))
        {
            sb.AppendLine("## Key Differentiator");
            sb.AppendLine(page.Differentiator);
            sb.AppendLine();
        }

        sb.AppendLine("## Action Plan");
        sb.AppendLine(refinedPlan);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.DataSources))
        {
            sb.AppendLine("## Data Sources & APIs");
            foreach (var ds in page.DataSources.Split("|||", StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"- {ds}");
            sb.AppendLine();
        }

        sb.AppendLine("## Monetization (REQUIRED)");
        sb.AppendLine();
        sb.AppendLine(GetMonetizationInstructions(page));

        if (!string.IsNullOrEmpty(page.AffiliatePrograms))
        {
            sb.AppendLine("### Affiliate Programs");
            foreach (var program in page.AffiliatePrograms.Split("|||", StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"- {program}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(page.MonetizationChannels))
        {
            sb.AppendLine("### Revenue Channels");
            foreach (var channel in page.MonetizationChannels.Split("|||", StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"- {channel}");
            sb.AppendLine();
        }

        if (page.OpportunityType is OpportunityType.DataMonetization or OpportunityType.FreeAsset)
            sb.AppendLine("- If data is involved, embed sample data as JSON in a `<script>` tag or a `.json` file");

        if (page.OpportunityType == OpportunityType.ContentGap)
        {
            sb.AppendLine("- Create comprehensive, original content that fills the identified gap");
            sb.AppendLine("- Include SEO meta tags (title, description, Open Graph)");
        }

        if (!string.IsNullOrEmpty(page.ExtractedSignals))
        {
            sb.AppendLine();
            sb.AppendLine("## Data Signals Extracted from Source");
            foreach (var signal in page.ExtractedSignals.Split("|||", StringSplitOptions.RemoveEmptyEntries).Take(15))
                sb.AppendLine($"- {signal}");
        }

        if (!string.IsNullOrEmpty(page.DistributionChannels))
        {
            sb.AppendLine();
            sb.AppendLine("## Distribution Strategy");
            sb.AppendLine($"Distribution Score: {page.DistributionScore}/10");
            sb.AppendLine("The site should be optimized for these free distribution channels:");
            try
            {
                var channels = System.Text.Json.JsonSerializer.Deserialize<List<ImagineWeb.Core.Models.DistributionChannel>>(page.DistributionChannels);
                if (channels is not null)
                    foreach (var ch in channels)
                        sb.AppendLine($"- **{ch.Method}**: {ch.Description} (effort: {ch.Effort}, reach: {ch.ExpectedReach})");
            }
            catch { sb.AppendLine(page.DistributionChannels); }
            if (page.IsBacklinkCandidate)
                sb.AppendLine($"- **Backlink target**: Source page ({page.Url}) is a {page.BacklinkType} — design the site to be link-worthy for this type of page.");
        }

        sb.AppendLine();
        sb.AppendLine(PromptSections.StrictCodeRules());
        sb.AppendLine();
        sb.AppendLine(PromptSections.ProductionQualityRules());
        sb.AppendLine();
        sb.AppendLine(PromptSections.AzureDeploymentContext());
        sb.AppendLine();
        sb.AppendLine(PromptSections.IaCRequirements());
        sb.AppendLine();
        sb.AppendLine(PromptSections.SelfValidation());

        return sb.ToString();
    }

    private static string GetMonetizationInstructions(DiscoveredPage page)
    {
        var sb = new StringBuilder();

        switch (page.OpportunityType)
        {
            case OpportunityType.TrendRiding:
            case OpportunityType.ContentGap:
                sb.AppendLine("Content/SEO play. Include:");
                sb.AppendLine("1. Comparison table with affiliate links (`href=\"#affiliate-PRODUCTNAME\"` placeholders)");
                sb.AppendLine("2. Email capture offering a free resource (checklist/guide/template)");
                sb.AppendLine("3. Ad placement containers: `<div class=\"ad-slot\" data-slot=\"top|sidebar|bottom\">`");
                sb.AppendLine("4. Prominent affiliate CTA buttons on every product mention");
                sb.AppendLine("5. Premium guide upsell with Lemon Squeezy checkout");
                break;

            case OpportunityType.Arbitrage:
                sb.AppendLine("Arbitrage tool. Include:");
                sb.AppendLine("1. Interactive profit calculator (buy/sell price, quantity → net profit after fees)");
                sb.AppendLine("2. Price comparison table (hardcode initial data, structure for updates)");
                sb.AppendLine("3. Affiliate links to buy-low and sell-high platforms");
                sb.AppendLine("4. Email capture for price alerts");
                sb.AppendLine("5. Premium tier: real-time alerts via Lemon Squeezy checkout");
                break;

            case OpportunityType.AutomationTarget:
                sb.AppendLine("Automation/SaaS opportunity. Build a working tool, not just a landing page:");
                sb.AppendLine("1. Functional tool/calculator demonstrating automation value (ROI calc, time-saved estimator)");
                sb.AppendLine("2. Comparison table of existing automation tools with affiliate links");
                sb.AppendLine("3. Waitlist/early-access email signup");
                sb.AppendLine("4. Paid template/spreadsheet upsell via Lemon Squeezy");
                sb.AppendLine("5. Consultation booking via Stripe Payment Link");
                break;

            case OpportunityType.UnservedNiche:
                sb.AppendLine("Underserved niche. Build a useful resource:");
                sb.AppendLine("1. Curated, searchable/filterable directory");
                sb.AppendLine("2. Featured listing slots (`data-sponsored=\"true\"`) for future paid placements");
                sb.AppendLine("3. Affiliate links in each listing");
                sb.AppendLine("4. Email capture for weekly curated updates");
                sb.AppendLine("5. Paid listing submission via Lemon Squeezy");
                break;

            case OpportunityType.FreeAsset:
            case OpportunityType.DataMonetization:
                sb.AppendLine("Data/asset play. Build an interactive data tool:");
                sb.AppendLine("1. Data browser with search, sort, filter (data embedded as JSON)");
                sb.AppendLine("2. Gated premium data — show 20% free, gate rest behind email signup");
                sb.AppendLine("3. CSV/PDF export via Lemon Squeezy one-time purchase");
                sb.AppendLine("4. API/data-feed upsell teaser");
                sb.AppendLine("5. Ad placement containers");
                break;

            case OpportunityType.SkillGap:
                sb.AppendLine("Skill gap. Build an educational resource:");
                sb.AppendLine("1. Interactive tutorial with collapsible sections, progress tracking (localStorage)");
                sb.AppendLine("2. Affiliate links to courses, books, tools");
                sb.AppendLine("3. Email capture for free cheat sheet");
                sb.AppendLine("4. Premium course/ebook upsell via Lemon Squeezy");
                sb.AppendLine("5. Consultation CTA via Stripe Payment Link");
                break;

            default:
                sb.AppendLine("Include at least TWO revenue mechanisms:");
                sb.AppendLine("1. Affiliate links (`href=\"#affiliate-PRODUCTNAME\"` placeholders)");
                sb.AppendLine("2. Email capture form (Formspree/Mailchimp embed)");
                sb.AppendLine("3. Paid digital product via Lemon Squeezy");
                sb.AppendLine("4. Ad placement containers: `<div class=\"ad-slot\">`");
                break;
        }

        return sb.ToString();
    }

    private static string BuildMonetizationConfig(DiscoveredPage page)
    {
        var checklist = new List<object>
        {
            new { item = "Replace affiliate link placeholders", howTo = "Search for href='#affiliate-' and replace with real affiliate URLs. Sign up at the affiliate programs listed below.", status = "pending" },
            new { item = "Set up Lemon Squeezy checkout", howTo = "1) Create free account at lemonsqueezy.com. 2) Create a product. 3) Copy the checkout URL. 4) Replace YOURSTORE.lemonsqueezy.com/buy/PRODUCT_ID in the HTML.", status = "pending" },
            new { item = "Configure email capture", howTo = "1) Create free Formspree.io account (50 submissions/month free) or Mailchimp (500 contacts free). 2) Get form endpoint URL. 3) Replace form action placeholder in HTML.", status = "pending" },
            new { item = "Add analytics tracking", howTo = "Add Google Analytics or Plausible script tag to track visitors and conversions.", status = "pending" },
            new { item = "Set up Google AdSense (optional)", howTo = "Apply at adsense.google.com, get ad code, replace ad-slot divs in HTML.", status = "pending" }
        };

        if (page.IsBacklinkCandidate && !string.IsNullOrEmpty(page.BacklinkType))
            checklist.Add(new { item = $"Request backlink from source page ({page.BacklinkType})", howTo = $"The source page ({page.Url}) is a {page.BacklinkType}. Contact the owner to suggest adding your site.", status = "pending" });

        if (!string.IsNullOrEmpty(page.PageContactEmails) || !string.IsNullOrEmpty(page.PageContactFormUrl))
        {
            var contact = !string.IsNullOrEmpty(page.PageContactEmails)
                ? $"Email: {page.PageContactEmails.Replace("|||", ", ")}"
                : $"Contact form: {page.PageContactFormUrl}";
            checklist.Add(new { item = "Reach out to source page owner", howTo = $"Introduce your tool and request a mention or link. {contact}", status = "pending" });
        }

        var config = new Dictionary<string, object>
        {
            ["pageId"] = page.Id,
            ["opportunityType"] = page.OpportunityType.ToString(),
            ["sourceUrl"] = page.Url,
            ["activationChecklist"] = checklist,
            ["estimatedReward"] = page.EstimatedReward ?? "",
            ["estimatedEffort"] = page.EstimatedEffort ?? ""
        };

        if (page.DistributionScore > 0)
            config["distributionScore"] = page.DistributionScore;

        if (!string.IsNullOrEmpty(page.DistributionChannels))
        {
            try
            {
                var channels = System.Text.Json.JsonSerializer.Deserialize<List<ImagineWeb.Core.Models.DistributionChannel>>(page.DistributionChannels);
                if (channels is { Count: > 0 })
                    config["distributionChannels"] = channels;
            }
            catch { }
        }

        return System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> RefineActionPlanAsync(DiscoveredPage page, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(page.ActionPlan))
            return "Create a web application that provides genuine value and includes at least two revenue mechanisms. Deploy to Azure using azd.";

        try
        {
            var refinementPrompt = $"""
                Convert this action plan into step-by-step build instructions for a web application
                that will be deployed to Azure via Azure Developer CLI (azd).

                YOU decide the architecture (static site, full-stack, containerized) based on the requirements.
                When server-side code is needed, prefer C# / ASP.NET Core.
                Use free tier / minimal-cost Azure resources.

                All application code goes in `site/`. IaC files (azure.yaml, infra/) go at the project root.

                The app must include at least two revenue mechanisms (affiliate links, paid product checkout,
                email capture, freemium model, etc.).

                Opportunity type: {page.OpportunityType}

                Original plan:
                {page.ActionPlan}

                Be concrete: specify key pages/routes, data sources, monetization integration points. Max 30 lines.
                """;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            string refined;
            using (await _llmGate.AcquireAsync(LlmPriority.Executor, cts.Token))
                refined = await _analyzer.RawPromptAsync(refinementPrompt, cts.Token);
            return string.IsNullOrWhiteSpace(refined) ? page.ActionPlan : refined;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLM refinement timed out for page {Id}, using original action plan", page.Id);
            return page.ActionPlan;
        }
        catch
        {
            return page.ActionPlan;
        }
    }
}
