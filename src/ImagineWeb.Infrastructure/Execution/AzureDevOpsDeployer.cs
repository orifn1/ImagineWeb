using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

/// <summary>
/// Deploys a generated solution to Azure via Azure DevOps:
/// 1. Scaffolds missing IaC files (Bicep, azure.yaml, azure-pipelines.yml)
/// 2. Creates a new Azure DevOps repo
/// 3. Pushes solution code to the repo
/// 4. Creates and configures an Azure Pipeline with Azure credentials
/// 5. Triggers the pipeline and waits for completion
/// </summary>
public sealed class AzureDevOpsDeployer : IAzureDevOpsDeployer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AzureDevOpsConfig _devOpsConfig;
    private readonly AzureDeployCredentials _azureCreds;
    private readonly HttpClient _http;
    private readonly ILogger<AzureDevOpsDeployer> _logger;

    public AzureDevOpsDeployer(
        IOptions<AzureDevOpsConfig> devOpsConfig,
        IOptions<AzureDeployCredentials> azureCreds,
        HttpClient http,
        ILogger<AzureDevOpsDeployer> logger)
    {
        _devOpsConfig = devOpsConfig.Value;
        _azureCreds = azureCreds.Value;
        _http = http;
        _logger = logger;

        ConfigureHttpClient();
    }

    public Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        var configured = !string.IsNullOrEmpty(_devOpsConfig.OrganizationUrl)
            && !string.IsNullOrEmpty(_devOpsConfig.PersonalAccessToken)
            && !string.IsNullOrEmpty(_devOpsConfig.ProjectName);
        return Task.FromResult(configured);
    }

    public async Task<AzureDevOpsDeployResult> DeployAsync(
        string appName, string solutionPath, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct))
            throw new InvalidOperationException(
                "Azure DevOps is not configured. Fill in OrganizationUrl, PersonalAccessToken, and ProjectName in appsettings.json → AzureDevOps.");

        var sitePath = Path.Combine(solutionPath, "site");
        var deployRoot = Directory.Exists(sitePath) ? sitePath : solutionPath;

        // 2. Create Azure DevOps repo
        var (repoId, repoUrl, remoteUrl) = await CreateRepoAsync(appName, ct);
        _logger.LogInformation("Created Azure DevOps repo {Repo}: {Url}", appName, repoUrl);

        // 3. Push code to the repo
        await PushToRepoAsync(deployRoot, remoteUrl, ct);
        _logger.LogInformation("Pushed code to {Repo}", remoteUrl);

        // 4. Create pipeline from azure-pipelines.yml
        var (pipelineId, pipelineUrl) = await CreatePipelineAsync(appName, repoId, ct);
        _logger.LogInformation("Created pipeline {Id} for {App}", pipelineId, appName);

        // 5. Set pipeline variables (Azure credentials for azd)
        await SetPipelineVariablesAsync(pipelineId, appName, ct);

        // 6. Run the pipeline
        var runId = await RunPipelineAsync(pipelineId, ct);
        _logger.LogInformation("Pipeline run {RunId} started for {App}", runId, appName);

        // 7. Wait for pipeline to complete
        var finalStatus = await WaitForPipelineAsync(pipelineId, runId, ct);

        return new AzureDevOpsDeployResult
        {
            RepoUrl = repoUrl,
            RepoName = appName,
            PipelineUrl = pipelineUrl,
            PipelineRunId = runId,
            Status = finalStatus
        };
    }

    public async Task<PipelineStatus> GetPipelineStatusAsync(string projectName, int pipelineRunId, CancellationToken ct)
    {
        var url = $"{_devOpsConfig.OrganizationUrl}/{projectName}/_apis/build/builds/{pipelineRunId}?api-version=7.1";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString();
        var result = doc.RootElement.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;

        return MapBuildStatus(status, result);
    }

    public async Task<(string RepoUrl, string RepoName)> CreateRepoAndPushAsync(
        string appName, string solutionPath, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct))
            throw new InvalidOperationException("Azure DevOps is not configured.");

        var sitePath = Path.Combine(solutionPath, "site");
        var deployRoot = Directory.Exists(sitePath) ? sitePath : solutionPath;

        var (_, repoUrl, remoteUrl) = await CreateRepoAsync(appName, ct);
        _logger.LogInformation("Created Azure DevOps repo {Repo}: {Url}", appName, repoUrl);

        await PushToRepoAsync(deployRoot, remoteUrl, ct);
        _logger.LogInformation("Pushed code to {Repo}", repoUrl);

        return (repoUrl, appName);
    }

    public async Task DeleteRepoAsync(string repoName, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct)) return;

        try
        {
            var repoId = await GetRepoIdByNameAsync(repoName, ct);
            var url = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/git/repositories/{repoId}?api-version=7.1";
            var response = await _http.DeleteAsync(url, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Deleted Azure DevOps repo {Repo}", repoName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete Azure DevOps repo {Repo}", repoName);
        }
    }

    private async Task<string> GetRepoIdByNameAsync(string repoName, CancellationToken ct)
    {
        var url = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}?api-version=7.1";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    #region Azure DevOps REST API Operations

    private async Task<(string repoId, string webUrl, string remoteUrl)> CreateRepoAsync(string repoName, CancellationToken ct)
    {
        var projectId = await GetProjectIdAsync(ct);

        var payload = new { name = repoName, project = new { id = projectId } };
        var url = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/git/repositories?api-version=7.1";

        var response = await _http.PostAsync(url, JsonContent(payload), ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to create repo '{repoName}': {response.StatusCode} — {error}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var repoId = doc.RootElement.GetProperty("id").GetString()!;
        var webUrl = doc.RootElement.GetProperty("webUrl").GetString()!;
        var remoteUrl = doc.RootElement.GetProperty("remoteUrl").GetString()!;

        return (repoId, webUrl, remoteUrl);
    }

    private async Task PushToRepoAsync(string localPath, string remoteUrl, CancellationToken ct)
    {
        var patForUrl = Uri.EscapeDataString(_devOpsConfig.PersonalAccessToken);
        var authRemoteUrl = remoteUrl.Replace("https://", $"https://pat:{patForUrl}@");

        await RunGitAsync(localPath, "init", ct);
        await RunGitAsync(localPath, $"checkout -b {_devOpsConfig.DefaultBranch}", ct);
        await RunGitAsync(localPath, "add -A", ct);
        await RunGitAsync(localPath, "commit -m \"Initial commit — auto-deployed by ImagineWeb\"", ct);
        await RunGitAsync(localPath, $"remote add origin {authRemoteUrl}", ct);
        await RunGitAsync(localPath, $"push -u origin {_devOpsConfig.DefaultBranch}", ct);
    }

    private async Task<(int pipelineId, string pipelineUrl)> CreatePipelineAsync(string appName, string repoId, CancellationToken ct)
    {
        var url = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/pipelines?api-version=7.1";

        var payload = new
        {
            name = $"{appName}-pipeline",
            folder = "\\",
            configuration = new
            {
                type = "yaml",
                path = "/azure-pipelines.yml",
                repository = new
                {
                    id = repoId,
                    type = "azureReposGit"
                }
            }
        };

        var response = await _http.PostAsync(url, JsonContent(payload), ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to create pipeline for '{appName}': {response.StatusCode} — {error}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var pipelineId = doc.RootElement.GetProperty("id").GetInt32();

        var pipelineUrl = doc.RootElement.TryGetProperty("_links", out var links)
            && links.TryGetProperty("web", out var web)
            && web.TryGetProperty("href", out var href)
                ? href.GetString()!
                : $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_build?definitionId={pipelineId}";

        return (pipelineId, pipelineUrl);
    }

    private async Task SetPipelineVariablesAsync(int pipelineId, string appName, CancellationToken ct)
    {
        // Get the build definition to update its variables
        var getUrl = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/build/definitions/{pipelineId}?api-version=7.1";
        var getResponse = await _http.GetAsync(getUrl, ct);
        getResponse.EnsureSuccessStatusCode();

        var definitionJson = await getResponse.Content.ReadAsStringAsync(ct);
        using var definitionDoc = JsonDocument.Parse(definitionJson);

        var azureConfig = _azureCreds;

        var variables = new Dictionary<string, object>
        {
            ["AZURE_CLIENT_ID"] = new { value = azureConfig.ClientId, isSecret = false },
            ["AZURE_CLIENT_SECRET"] = new { value = azureConfig.ClientSecret, isSecret = true },
            ["AZURE_TENANT_ID"] = new { value = azureConfig.TenantId, isSecret = false },
            ["AZURE_SUBSCRIPTION_ID"] = new { value = "", isSecret = false },
            ["AZURE_LOCATION"] = new { value = azureConfig.DefaultRegion, isSecret = false },
            ["AZURE_ENV_NAME"] = new { value = appName, isSecret = false }
        };

        // Rebuild the definition with variables added
        var updatedDefinition = RebuildDefinitionWithVariables(definitionJson, variables);

        var putUrl = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/build/definitions/{pipelineId}?api-version=7.1";
        var putResponse = await _http.PutAsync(putUrl, new StringContent(updatedDefinition, Encoding.UTF8, "application/json"), ct);

        if (!putResponse.IsSuccessStatusCode)
        {
            var error = await putResponse.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Failed to set pipeline variables: {Status} — {Error}", putResponse.StatusCode, error);
        }
    }

    private async Task<int> RunPipelineAsync(int pipelineId, CancellationToken ct)
    {
        var url = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.ProjectName}/_apis/pipelines/{pipelineId}/runs?api-version=7.1";

        var payload = new
        {
            resources = new
            {
                repositories = new
                {
                    self = new { refName = $"refs/heads/{_devOpsConfig.DefaultBranch}" }
                }
            }
        };

        var response = await _http.PostAsync(url, JsonContent(payload), ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to run pipeline {pipelineId}: {response.StatusCode} — {error}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<PipelineStatus> WaitForPipelineAsync(int pipelineId, int runId, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(_devOpsConfig.PipelineTimeoutMinutes);
        var pollInterval = TimeSpan.FromSeconds(_devOpsConfig.PipelinePollIntervalSeconds);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await GetPipelineStatusAsync(_devOpsConfig.ProjectName, runId, ct);

            if (status is PipelineStatus.Succeeded or PipelineStatus.Failed or PipelineStatus.Canceled)
            {
                _logger.LogInformation("Pipeline run {RunId} completed with status: {Status}", runId, status);
                return status;
            }

            _logger.LogDebug("Pipeline run {RunId} still {Status}, polling again in {Seconds}s", runId, status, pollInterval.TotalSeconds);
            await Task.Delay(pollInterval, ct);
        }

        _logger.LogWarning("Pipeline run {RunId} timed out after {Minutes} minutes", runId, timeout.TotalMinutes);
        return PipelineStatus.Running;
    }

    #endregion

    #region Helpers

    private void ConfigureHttpClient()
    {
        if (string.IsNullOrEmpty(_devOpsConfig.PersonalAccessToken)) return;

        var base64Pat = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_devOpsConfig.PersonalAccessToken}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Pat);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<string> GetProjectIdAsync(CancellationToken ct)
    {
        var url = $"{_devOpsConfig.OrganizationUrl}/_apis/projects/{_devOpsConfig.ProjectName}?api-version=7.1";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static StringContent JsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string RebuildDefinitionWithVariables(string definitionJson, Dictionary<string, object> variables)
    {
        using var doc = JsonDocument.Parse(definitionJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "variables")
                {
                    writer.WritePropertyName("variables");
                    writer.WriteStartObject();

                    // Preserve existing variables
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var existing in prop.Value.EnumerateObject())
                            existing.Value.WriteTo(writer);
                    }

                    // Add new variables
                    foreach (var (key, val) in variables)
                    {
                        writer.WritePropertyName(key);
                        JsonSerializer.Serialize(writer, val, JsonOpts);
                    }

                    writer.WriteEndObject();
                    continue;
                }

                prop.WriteTo(writer);
            }

            // If there was no "variables" key at all, add it
            if (!doc.RootElement.TryGetProperty("variables", out _))
            {
                writer.WritePropertyName("variables");
                writer.WriteStartObject();
                foreach (var (key, val) in variables)
                {
                    writer.WritePropertyName(key);
                    JsonSerializer.Serialize(writer, val, JsonOpts);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed (exit {process.ExitCode}): {error}");
    }

    private static PipelineStatus MapBuildStatus(string? status, string? result) => status switch
    {
        "completed" => result switch
        {
            "succeeded" => PipelineStatus.Succeeded,
            "failed" => PipelineStatus.Failed,
            "canceled" => PipelineStatus.Canceled,
            _ => PipelineStatus.Failed
        },
        "inProgress" => PipelineStatus.Running,
        "notStarted" => PipelineStatus.Queued,
        _ => PipelineStatus.Running
    };

    #endregion
}
