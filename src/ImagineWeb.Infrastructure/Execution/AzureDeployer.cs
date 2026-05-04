using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Azure;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public sealed class AzureDeployer : IAzureDeployer
{
    private readonly AzureDeployCredentials _creds;
    private readonly AzureSubscriptionDiscovery _discovery;
    private readonly AzureQuotaChecker _quotaChecker;
    private readonly ILogger<AzureDeployer> _logger;

    public AzureDeployer(
        IOptions<AzureDeployCredentials> creds,
        AzureSubscriptionDiscovery discovery,
        AzureQuotaChecker quotaChecker,
        ILogger<AzureDeployer> logger)
    {
        _creds = creds.Value;
        _discovery = discovery;
        _quotaChecker = quotaChecker;
        _logger = logger;
    }

    public Task<bool> IsConfiguredAsync(CancellationToken ct)
        => Task.FromResult(_creds.IsConfigured);

    public async Task<AzureDeployResult> DeployAsync(
        string appName, string solutionPath, CancellationToken ct, string? existingResourceGroup = null, string? preferredSubscriptionId = null)
    {
        var azdRoot = FindAzdRoot(solutionPath);
        var isStaticWebApp = IsStaticWebAppProject(azdRoot);

        var selected = await SelectSubscriptionAsync(ct, needsAppService: !isStaticWebApp, needsStaticWebApp: isStaticWebApp, preferredSubscriptionId: preferredSubscriptionId)
            ?? throw new InvalidOperationException(
                "No Azure subscription has available free resources. All subscriptions have reached their quota limits.");

        var envName = SanitizeEnvName(appName);

        if (OperatingSystem.IsWindows())
            PatchAzureYamlShellHooks(azdRoot);

        PatchUnsupportedLanguageInYaml(azdRoot);
        PatchDistPath(azdRoot);

        var resourceGroup = string.IsNullOrEmpty(existingResourceGroup) ? $"rg-{envName}" : existingResourceGroup;
        var azdEnv = new Dictionary<string, string>
        {
            ["AZURE_CLIENT_ID"]       = _creds.ClientId,
            ["AZURE_CLIENT_SECRET"]   = _creds.ClientSecret,
            ["AZURE_TENANT_ID"]       = _creds.TenantId,
            ["AZURE_SUBSCRIPTION_ID"] = selected.Id,
            ["AZURE_LOCATION"]        = _creds.DefaultRegion,
            ["AZURE_RESOURCE_GROUP"]  = resourceGroup,
            ["AZURE_ENV_NAME"]        = envName,
        };
        _logger.LogInformation("azd deploying {App} to subscription {Sub} ({Name})",
            appName, selected.Id, selected.Name);

        await SelectOrCreateEnvAsync(azdRoot, envName, ct, azdEnv);
        await RunAzdAsync(azdRoot, $"env set AZURE_SUBSCRIPTION_ID {selected.Id} -e {envName}", ct, azdEnv);
        await RunAzdAsync(azdRoot, $"env set AZURE_LOCATION {_creds.DefaultRegion} -e {envName}", ct, azdEnv);
        await RunAzdAsync(azdRoot, $"env set AZURE_RESOURCE_GROUP {resourceGroup} -e {envName}", ct, azdEnv);

        // Pre-flight via ARM API: validates the subscription-scoped Bicep template against
        // Azure (region, SKU, name conflicts, RBAC) without creating any resources.
        // Fails fast — saves the cost of a real azd-up + LLM fix cycle when the template is broken.
        await PreflightDeploymentAsync(azdRoot, envName, selected.Id, _creds.DefaultRegion, resourceGroup, ct, azdEnv);

        _logger.LogInformation("Running azd up for {App} from {Root}", appName, azdRoot);
        string upOutput;
        try
        {
            var (o, _) = await RunAzdAsync(azdRoot, $"up --no-prompt -e {envName}", ct, azdEnv);
            upOutput = o;
        }
        catch (InvalidOperationException ex) when (IsArgIndexingLagError(ex.Message))
        {
            // Known azd quirk: after a successful provision, `azd deploy` queries Azure Resource
            // Graph to locate resources tagged with `azd-service-name`. ARG has indexing latency
            // (a few seconds to ~1 min for fresh resources), so the very first deploy after
            // provision can fail with "unable to find a resource tagged …" even though the Bicep
            // is correct and the resource exists. Provision is idempotent and already done, so
            // we just wait and re-run deploy. This is deterministic — no premium request needed.
            _logger.LogWarning(
                "azd up failed because Azure Resource Graph hasn't indexed the freshly-provisioned " +
                "resources yet. Waiting 45s and retrying deploy only (provision already succeeded).");
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            var (o, _) = await RunAzdAsync(azdRoot, $"deploy --all --no-prompt -e {envName}", ct, azdEnv);
            upOutput = o;
        }

        var url = ExtractDeployedUrl(upOutput);
        if (string.IsNullOrEmpty(url))
            url = await GetAzdOutputValueAsync(azdRoot, envName, ct);

        var resources = await DetectDeployedResourcesAsync(azdRoot, envName, isStaticWebApp, ct, azdEnv);

        _logger.LogInformation("azd deployment complete for {App}: {Url}", appName, url);
        return new AzureDeployResult
        {
            DeployedUrl = url,
            ResourceGroupName = resourceGroup,
            SubscriptionId = selected.Id,
            DeployedResources = resources
        };
    }

    private async Task<DiscoveredSubscription?> SelectSubscriptionAsync(
        CancellationToken ct, bool needsAppService, bool needsStaticWebApp, string? preferredSubscriptionId = null)
    {
        var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
        if (subs.Count == 0)
        {
            _logger.LogWarning("No deployment-eligible subscriptions discovered");
            return null;
        }

        if (!string.IsNullOrEmpty(preferredSubscriptionId))
        {
            var preferred = subs.FirstOrDefault(s => s.Id.Equals(preferredSubscriptionId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                _logger.LogInformation("Reusing previously assigned subscription {Name} ({Id})", preferred.Name, preferred.Id);
                return preferred;
            }
            _logger.LogWarning("Preferred subscription {Id} is no longer accessible, falling back to quota-based selection", preferredSubscriptionId);
        }

        _logger.LogInformation("Checking quotas across {Count} discovered subscription(s) (needsAppService={NeedsApp}, needsStaticWebApp={NeedsSwa})...",
            subs.Count, needsAppService, needsStaticWebApp);
        var quotas = await _quotaChecker.CheckAllSubscriptionsAsync(ct);

        foreach (var (subId, name, quota) in quotas)
        {
            _logger.LogInformation("Subscription {Name} ({Id}): Free App Service Plans {Used}/{Limit}, Free Static Web Apps {SwaUsed}/{SwaLimit}",
                name, subId, quota.FreeAppServicePlansUsed, quota.FreeAppServicePlansLimit,
                quota.FreeStaticWebAppsUsed, quota.FreeStaticWebAppsLimit);
        }

        var selected = _quotaChecker.FindAvailableSubscription(quotas, needsAppService, needsStaticWebApp);
        if (selected is not null)
        {
            _logger.LogInformation("Selected subscription {Name} ({Id}) with available resources", selected.Name, selected.Id);
            return selected;
        }

        _logger.LogWarning("No subscription has available quota for the requested resource type");
        return null;
    }

    public async Task DeleteAsync(string resourceGroupName, CancellationToken ct, string? subscriptionId = null)
    {
        if (!_creds.IsConfigured)
            throw new InvalidOperationException("Azure deployment credentials are not configured.");

        var targetSubId = subscriptionId;
        if (string.IsNullOrEmpty(targetSubId))
        {
            var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
            targetSubId = subs.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException("No deployment-eligible subscriptions found.");
        }

        var credential = new ClientSecretCredential(_creds.TenantId, _creds.ClientId, _creds.ClientSecret);
        var client = new ArmClient(credential);

        var rgId = ResourceGroupResource.CreateResourceIdentifier(targetSubId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);
        await rg.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);

        _logger.LogInformation("Deleted resource group {RG} in subscription {Sub}", resourceGroupName, targetSubId);
    }

    // On Windows, azd cannot execute `shell: sh` hooks because /bin/bash is unavailable.
    // Replace any such hooks with `shell: pwsh` so PowerShell runs them instead.
    private static void PatchAzureYamlShellHooks(string azdRoot)
    {
        var yamlPath = Path.Combine(azdRoot, "azure.yaml");
        if (!File.Exists(yamlPath)) return;

        var content = File.ReadAllText(yamlPath);
        var patched = content.Replace("shell: sh", "shell: pwsh");
        if (patched != content)
            File.WriteAllText(yamlPath, patched);
    }

    // azd only supports specific language values (dotnet, js, ts, python, java, docker).
    // `language: html` and similar unsupported values cause `azd package` to fail with
    // "language is not supported by built-in framework services". Remove any such lines so
    // azd falls back to its default packaging behaviour for the given host type.
    private static readonly IReadOnlySet<string> ValidAzdLanguages =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet", "csharp", "fsharp", "js", "ts", "python", "java", "docker"
        };

    private void PatchUnsupportedLanguageInYaml(string azdRoot)
    {
        var yamlPath = Path.Combine(azdRoot, "azure.yaml");
        if (!File.Exists(yamlPath)) return;

        var lines = File.ReadAllLines(yamlPath);
        var patched = new List<string>(lines.Length);
        var changed = false;

        // Track which project path belongs to the current service so we can check for package.json.
        // The convention is project: ./site, so we resolve it relative to azdRoot.
        string? currentProjectPath = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            {
                var projectValue = trimmed["project:".Length..].Trim();
                currentProjectPath = Path.GetFullPath(Path.Combine(azdRoot, projectValue));
            }
            else if (trimmed.StartsWith("language:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["language:".Length..].Trim();

                if (!ValidAzdLanguages.Contains(value))
                {
                    _logger.LogWarning(
                        "azure.yaml: removing unsupported 'language: {Value}' before deployment (not in azd's supported language list)",
                        value);
                    changed = true;
                    continue;
                }

                // js/ts triggers `npm install`; without package.json azd fails with ENOENT.
                if ((value.Equals("js", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("ts", StringComparison.OrdinalIgnoreCase))
                    && currentProjectPath is not null
                    && !File.Exists(Path.Combine(currentProjectPath, "package.json")))
                {
                    _logger.LogWarning(
                        "azure.yaml: removing 'language: {Value}' because package.json not found in {ProjectPath}. " +
                        "azd runs npm install for js/ts services and would fail with ENOENT.",
                        value, currentProjectPath);
                    changed = true;
                    continue;
                }
            }

            patched.Add(line);
        }

        if (changed)
            File.WriteAllLines(yamlPath, patched);
    }

    // `dist` in azure.yaml is relative to `project:`, not the repo root.
    // When the LLM generates `project: ./site` + `dist: ./site`, azd resolves
    // the package source as `site/site` which doesn't exist.
    // Correct value for a static site where files live directly in site/ is `dist: .`
    private void PatchDistPath(string azdRoot)
    {
        var yamlPath = Path.Combine(azdRoot, "azure.yaml");
        if (!File.Exists(yamlPath)) return;

        var lines = File.ReadAllLines(yamlPath);
        var patched = new List<string>(lines.Length);
        var changed = false;

        string? currentProject = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
                currentProject = trimmed["project:".Length..].Trim().TrimStart('.', '/');

            if (trimmed.StartsWith("dist:", StringComparison.OrdinalIgnoreCase))
            {
                var distValue = trimmed["dist:".Length..].Trim();
                // Normalise to a comparable segment (strip leading ./ )
                var distSegment = distValue.TrimStart('.', '/', '\\');
                var projectSegment = (currentProject ?? "").TrimStart('.', '/', '\\');

                // dist: ./site with project: ./site → resolves to site/site
                if (!string.IsNullOrEmpty(projectSegment)
                    && distSegment.Equals(projectSegment, StringComparison.OrdinalIgnoreCase))
                {
                    var indent = line[..(line.Length - line.TrimStart().Length)];
                    _logger.LogWarning(
                        "azure.yaml: 'dist: {Dist}' with 'project: ./site' resolves to '{Project}/{Dist}' which doesn't exist. " +
                        "Replacing with 'dist: .' so azd packages from the project root.",
                        distValue, projectSegment, distSegment);
                    patched.Add($"{indent}dist: .");
                    changed = true;
                    continue;
                }
            }

            patched.Add(line);
        }

        if (changed)
            File.WriteAllLines(yamlPath, patched);
    }

    private static string FindAzdRoot(string solutionPath)    {
        if (File.Exists(Path.Combine(solutionPath, "azure.yaml")))
            return solutionPath;
        var sitePath = Path.Combine(solutionPath, "site");
        if (File.Exists(Path.Combine(sitePath, "azure.yaml")))
            return sitePath;

        throw new FileNotFoundException(
            $"azure.yaml not found in {solutionPath} or {sitePath}. IaC files are required for deployment.");
    }

    private static bool IsStaticWebAppProject(string azdRoot)
    {
        var yamlPath = Path.Combine(azdRoot, "azure.yaml");
        if (!File.Exists(yamlPath)) return false;
        var content = File.ReadAllText(yamlPath);
        return content.Contains("host: staticwebapp", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SelectOrCreateEnvAsync(string azdRoot, string envName, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        try
        {
            await RunAzdAsync(azdRoot, $"env select {envName}", ct, extraEnv);
            _logger.LogInformation("azd environment '{Env}' selected", envName);
        }
        catch
        {
            await RunAzdAsync(azdRoot, $"env new {envName} --no-prompt", ct, extraEnv);
            _logger.LogInformation("azd environment '{Env}' created", envName);
        }
    }

    private static string SanitizeEnvName(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
        return string.IsNullOrEmpty(sanitized) ? "default" : sanitized;
    }

    private static string ExtractDeployedUrl(string output)
    {
        var match = Regex.Match(output, @"(https://\S+\.azurestaticapps\.net)\b");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(output, @"(https://\S+\.azurewebsites\.net)\b");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(output, @"Endpoint:\s*(https://\S+)");
        if (match.Success) return match.Groups[1].Value;

        return "";
    }

    private async Task<string> GetAzdOutputValueAsync(string azdRoot, string envName, CancellationToken ct)
    {
        try
        {
            var (output, _) = await RunAzdAsync(azdRoot, $"env get-values -e {envName}", ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("WEBSITE_URL=", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("SERVICE_WEB_ENDPOINT_URL=", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("AZURE_STATIC_WEB_APP_URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        var val = trimmed[(eqIdx + 1)..].Trim('"', '\'', ' ');
                        if (val.StartsWith("https://")) return val;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not extract deployed URL from azd environment");
        }
        return "";
    }

    private async Task<string?> GetAzdEnvValueAsync(string azdRoot, string envName, string key, CancellationToken ct)
    {
        try
        {
            var (output, _) = await RunAzdAsync(azdRoot, $"env get-value {key} -e {envName}", ct);
            var value = output.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch { return null; }
    }

    /// <summary>
    /// Runs <c>azd provision --preview</c> as a non-destructive pre-flight. azd uses the same
    /// Service Principal env vars as the real deploy, so authentication / subscription discovery
    /// work identically — unlike <c>az deployment sub validate</c>, which depends on a separate
    /// <c>az login</c> session that is usually absent in this app's runtime.
    ///
    /// Behaviour:
    /// <list type="bullet">
    ///   <item>Bicep / template error → throw, so the caller routes it to a single combined fix.</item>
    ///   <item>Environment / auth error (subscription not found, unauthorized, quota, name conflict)
    ///         → log a warning and proceed; <c>azd up</c> will surface the real error or succeed.</item>
    ///   <item>azd version that doesn't support <c>--preview</c> → log warning, proceed.</item>
    /// </list>
    /// </summary>
    private async Task PreflightDeploymentAsync(
        string azdRoot, string envName, string subscriptionId, string location,
        string resourceGroup, CancellationToken ct, IReadOnlyDictionary<string, string> azdEnv)
    {
        _logger.LogInformation("Pre-flight: azd provision --preview for {Env}", envName);
        try
        {
            await RunAzdAsync(azdRoot, $"provision --preview --no-prompt -e {envName}", ct, azdEnv);
            _logger.LogInformation("Pre-flight passed for {Env}", envName);
        }
        catch (Exception ex)
        {
            var combined = ex.Message ?? "";
            if (IsBicepTemplateError(combined))
            {
                // Real IaC bug — fail fast so we don't burn quota and a paid fix request on a
                // half-broken provision. Caller catches this and routes to combined-fix.
                throw new InvalidOperationException(
                    "Pre-flight detected a Bicep template error that azd would fail on. " +
                    "Fixing this in code is cheaper than running the full deploy.\n" + combined, ex);
            }

            _logger.LogWarning(
                "Pre-flight skipped (environment-level issue, not a template bug): {Msg}. " +
                "Proceeding with azd up — it will use the same credentials and surface the real error if any.",
                combined.Length > 400 ? combined[..400] + "..." : combined);
        }
    }

    /// <summary>
    /// Distinguishes Bicep / ARM template errors (worth fixing in code) from infrastructure-level
    /// failures (auth, quota, missing subscription, name conflicts) where re-running with a code
    /// change does nothing useful.
    /// </summary>
    private static bool IsBicepTemplateError(string error)
    {
        var lower = error.ToLowerInvariant();

        // Hard signals that the failure is in the template itself.
        string[] templateSignals =
        {
            "invalidtemplate", "invalid template", "deploymenttemplatevalidation",
            "bcp0", "error bcp", "bicep build", "preflightvalidationcheckfailed",
            "preflightvalidationfailed", "missing required property", "is not a valid",
            "expected resource", "could not find resource type", "no module reference",
            "circular dependency", "syntax error", "expression evaluation failed",
            "linter rule", "outputs must be unique", "parameter is required",
            "deploymenttemplate", "templateresourcecirculardependency"
        };
        if (templateSignals.Any(s => lower.Contains(s)))
            return true;

        // Hard signals that this is environment / auth / quota — NOT a template issue.
        string[] envSignals =
        {
            "subscription", "not found", "unauthorized", "not authorized", "forbidden",
            "401", "403", "credentials", "tenant", "client_id", "clientsecret",
            "quota", "limit exceeded", "not enough", "name is already taken",
            "name already exists", "conflict", "azd is not", "azd: command not found",
            "preview is not supported", "unknown command", "unknown flag"
        };
        if (envSignals.Any(s => lower.Contains(s)))
            return false;

        // Default: treat as environment to avoid false-positive aborts. Worst case azd up runs
        // and fails — same outcome as before pre-flight existed.
        return false;
    }

    /// <summary>
    /// Detects the well-known azd race condition where <c>azd deploy</c> (the second phase of
    /// <c>azd up</c>) queries Azure Resource Graph for resources tagged with
    /// <c>azd-service-name</c> immediately after <c>azd provision</c> finishes — but ARG hasn't
    /// indexed the freshly-created resources yet, so it reports them as missing. Caller should
    /// wait briefly and retry <c>azd deploy</c>; provision is idempotent and already complete.
    /// </summary>
    private static bool IsArgIndexingLagError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        var lower = message.ToLowerInvariant();
        // azd CLI message:
        //   "getting target resource: resource not found: unable to find a resource tagged with
        //    'azd-service-name: <name>'. Ensure the service resource is correctly tagged in your
        //    infrastructure configuration, and rerun provision"
        return lower.Contains("unable to find a resource tagged")
            || (lower.Contains("resource not found") && lower.Contains("azd-service-name"));
    }

    private async Task<string> DetectDeployedResourcesAsync(
        string azdRoot, string envName, bool isStaticWebApp, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        try
        {
            var (showOutput, _) = await RunAzdAsync(azdRoot, $"show -e {envName}", ct, extraEnv);
            var resources = ParseAzdShowResources(showOutput);
            if (!string.IsNullOrEmpty(resources))
                return resources;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "azd show failed, falling back to azure.yaml detection");
        }

        return isStaticWebApp ? "Azure Static Web App (Free)" : "App Service (Free F1)";
    }

    private static string ParseAzdShowResources(string output)
    {
        var resources = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("Microsoft.Web/staticSites", StringComparison.OrdinalIgnoreCase))
                resources.Add("Azure Static Web App (Free)");
            else if (trimmed.Contains("Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase))
                resources.Add("App Service Plan (Free F1)");
            else if (trimmed.Contains("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase)
                     && !trimmed.Contains("staticSites", StringComparison.OrdinalIgnoreCase))
                resources.Add("App Service");
            else if (trimmed.Contains("Microsoft.Sql", StringComparison.OrdinalIgnoreCase))
                resources.Add("Azure SQL Database");
            else if (trimmed.Contains("Microsoft.DBforPostgreSQL", StringComparison.OrdinalIgnoreCase))
                resources.Add("PostgreSQL Database");
            else if (trimmed.Contains("Microsoft.Storage", StringComparison.OrdinalIgnoreCase))
                resources.Add("Storage Account");
            else if (trimmed.Contains("Microsoft.DocumentDB", StringComparison.OrdinalIgnoreCase))
                resources.Add("Cosmos DB");
            else if (trimmed.Contains("Microsoft.Cache", StringComparison.OrdinalIgnoreCase))
                resources.Add("Azure Cache for Redis");
        }
        return string.Join(", ", resources.Distinct());
    }

    // Resolves the azd executable, checking the known Windows install location and all
    // PATH sources (process, user, machine) so it works even when the calling process
    // was started before azd was installed or before the user PATH was refreshed.
    private static string FindAzdExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "azd";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultInstall = Path.Combine(localAppData, "Programs", "Azure Dev CLI", "azd.exe");
        if (File.Exists(defaultInstall)) return defaultInstall;

        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!searched.Add(dir.Trim())) continue;
                var candidate = Path.Combine(dir.Trim(), "azd.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return "azd"; // last resort – let the OS resolve it
    }

    // Builds a PATH string that merges process, user, and machine PATH sources.
    private static string BuildCombinedPath()
    {
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var parts = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
                parts.Add(p);
        }
        return string.Join(sep, parts);
    }

    private async Task<(string Output, string Error)> RunAzdAsync(
        string workingDir, string args, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var exe = FindAzdExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["PATH"] = BuildCombinedPath()
            }
        };

        if (extraEnv != null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        _logger.LogInformation("Running: azd {Args} in {Dir}", args, workingDir);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start azd CLI");

        // Intentionally do NOT propagate the caller's CancellationToken to the process I/O
        // or WaitForExitAsync. azd is a billable long-running operation: cancelling it mid-flight
        // (e.g. because the originating HTTP request was aborted by a browser refresh, or because
        // npm/vite build pushed past a request timeout) leaves Azure in a half-provisioned state
        // and produces exit code -1073741510 (STATUS_CONTROL_C_EXIT) when the OS tears down the
        // child tree. We always let azd run to completion and surface the real exit status.
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);

        if (ct.IsCancellationRequested)
            _logger.LogWarning(
                "Caller cancellation was requested while `azd {Args}` was running, but the process " +
                "was allowed to finish to avoid leaving Azure in a partially-provisioned state.",
                args);

        if (!string.IsNullOrWhiteSpace(output))
            _logger.LogInformation("azd stdout: {Output}", output);
        if (!string.IsNullOrWhiteSpace(error))
            _logger.LogWarning("azd stderr: {Error}", error);

        if (process.ExitCode != 0)
        {
            var cmd = args.Split(' ')[0];
            throw new InvalidOperationException(
                $"azd {cmd} failed (exit {process.ExitCode}): {error}\n{output}");
        }

        return (output, error);
    }
}
