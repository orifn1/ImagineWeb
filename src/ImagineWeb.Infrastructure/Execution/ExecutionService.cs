using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class ExecutionService : IExecutionService
{
    private readonly IHunterRepository _repository;
    private readonly ICopilotPromptGenerator _promptGenerator;
    private readonly IGitHubPagesDeployer _deployer;
    private readonly IAzureDeployer _azureDeployer;
    private readonly CodeGeneratorFactory _codeGeneratorFactory;
    private readonly IDeploymentPlanService _planService;
    private readonly ISolutionStorageService _solutionStorage;
    private readonly ExecutorConfig _config;
    private readonly ILogger<ExecutionService> _logger;

    public ExecutionService(
        IHunterRepository repository,
        ICopilotPromptGenerator promptGenerator,
        IGitHubPagesDeployer deployer,
        IAzureDeployer azureDeployer,
        CodeGeneratorFactory codeGeneratorFactory,
        IDeploymentPlanService planService,
        ISolutionStorageService solutionStorage,
        IOptions<ExecutorConfig> config,
        ILogger<ExecutionService> logger)
    {
        _repository = repository;
        _promptGenerator = promptGenerator;
        _deployer = deployer;
        _azureDeployer = azureDeployer;
        _codeGeneratorFactory = codeGeneratorFactory;
        _planService = planService;
        _solutionStorage = solutionStorage;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> StartImplementationAsync(int pageId, string method, CancellationToken ct, string? providerOverride = null)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (page.Status is not (PageStatus.Analyzed or PageStatus.DeployFailed))
            throw new InvalidOperationException($"Page {pageId} is in status {page.Status}, cannot implement");

        page.Status = PageStatus.Implementing;
        await _repository.UpdatePageAsync(page, ct);

        try
        {
            var solutionDir = await _promptGenerator.GeneratePromptFileAsync(page, ct);
            page.SolutionPath = solutionDir;
            await _repository.UpdatePageAsync(page, ct);

            if (method == "codeChatCli")
            {
                var generator = await _codeGeneratorFactory.GetGeneratorAsync(ct, providerOverride);
                var promptPath = Path.Combine(solutionDir, "prompt.md");
                var handle = await generator.StartAsync(new CodeGenerationRequest
                {
                    PromptFilePath = promptPath,
                    WorkingDirectory = solutionDir,
                    SystemMessageAppend = PromptSections.CopilotSdkDeploymentContext(solutionDir),
                    Streaming = true
                }, ct);
                page.GenerationId = handle.GenerationId;
                page.Status = PageStatus.Implementing;
            }
            else
            {
                page.Status = PageStatus.AwaitingApproval;
            }
            await _repository.UpdatePageAsync(page, ct);

            return solutionDir;
        }
        catch (Exception ex) when (ex is not KeyNotFoundException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Implementation failed for page {Id}", pageId);
            page.Status = PageStatus.Analyzed;
            await _repository.UpdatePageAsync(page, CancellationToken.None);
            throw;
        }
    }

    public async Task<string> ApproveAndDeployAsync(int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (page.Status is not (PageStatus.AwaitingApproval or PageStatus.DeployFailed))
            throw new InvalidOperationException($"Page {pageId} is in status {page.Status}, cannot deploy");

        if (string.IsNullOrEmpty(page.SolutionPath) || !Directory.Exists(page.SolutionPath))
            throw new InvalidOperationException($"Solution path not found for page {pageId}");

        page.Status = PageStatus.Deploying;
        await _repository.UpdatePageAsync(page, ct);

        try
        {
            var repoName = GenerateRepoName(page);
            var deployedUrl = await _deployer.CreateRepoAndDeployAsync(repoName, page.SolutionPath, ct);

            page.DeployedUrl = deployedUrl;
            page.GitHubRepo = repoName;
            page.DeployedAt = DateTime.UtcNow;
            page.Status = PageStatus.Deployed;
            await _repository.UpdatePageAsync(page, ct);

            _logger.LogInformation("Page {Id} deployed to {Url}", pageId, deployedUrl);
            try { await _solutionStorage.ArchiveSolutionAsync(page.SolutionPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Archive failed for page {Id}", pageId); }
            return deployedUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deploy failed for page {Id}", pageId);
            page.Status = PageStatus.DeployFailed;
            await _repository.UpdatePageAsync(page, ct);
            throw;
        }
    }

    public async Task RejectAsync(int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        page.Status = PageStatus.Analyzed;
        page.SolutionPath = null;
        await _repository.UpdatePageAsync(page, ct);
    }

    public async Task<DeploymentPlan> GetDeploymentPlanAsync(int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (string.IsNullOrEmpty(page.SolutionPath) || !Directory.Exists(page.SolutionPath))
            throw new InvalidOperationException($"Solution path not found for page {pageId}");

        return await _planService.BuildPlanAsync(page.SolutionPath, ct);
    }

    public async Task<string> ApproveAndDeployToAzureAsync(
        int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (page.Status is not (PageStatus.AwaitingApproval or PageStatus.DeployFailed))
            throw new InvalidOperationException($"Page {pageId} is in status {page.Status}, cannot deploy");

        if (string.IsNullOrEmpty(page.SolutionPath) || !Directory.Exists(page.SolutionPath))
            throw new InvalidOperationException($"Solution path not found for page {pageId}");

        if (!await _azureDeployer.IsConfiguredAsync(ct))
            throw new InvalidOperationException(
                "Azure deployment is not configured. Fill in AzureDeployment settings in appsettings.json.");

        page.Status = PageStatus.Deploying;
        await _repository.UpdatePageAsync(page, ct);

        try
        {
            var appName = GenerateRepoName(page);
            var azResult = await _azureDeployer.DeployAsync(appName, page.SolutionPath, ct, preferredSubscriptionId: page.AzureSubscriptionId);

            page.DeployedUrl = azResult.DeployedUrl;
            page.AzureResourceGroup = azResult.ResourceGroupName;
            page.AzureSubscriptionId = azResult.SubscriptionId;
            page.DeployedResources = azResult.DeployedResources;
            page.DeployedAt = DateTime.UtcNow;
            page.Status = PageStatus.Deployed;

            try
            {
                var plan = await _planService.BuildPlanAsync(page.SolutionPath, ct);
                page.EstimatedMonthlyCostUsd = plan.EstimatedMonthlyCostUsd;
            }
            catch { }

            await _repository.UpdatePageAsync(page, ct);

            _logger.LogInformation("Page {Id} deployed to Azure: {Url}", pageId, azResult.DeployedUrl);
            try { await _solutionStorage.ArchiveSolutionAsync(page.SolutionPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Archive failed for page {Id}", pageId); }
            return azResult.DeployedUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure deploy failed for page {Id}", pageId);
            page.Status = PageStatus.DeployFailed;
            await _repository.UpdatePageAsync(page, ct);
            throw;
        }
    }

    private static string GenerateRepoName(DiscoveredPage page)
    {
        var baseName = page.Title ?? page.OpportunityType.ToString();
        var slug = new string(baseName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        return $"wph-{slug}-{page.Id}";
    }

    public async Task TeardownDeploymentAsync(int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (page.Status != PageStatus.Deployed)
            throw new InvalidOperationException($"Page {pageId} is in status {page.Status}, nothing to tear down");

        if (!string.IsNullOrEmpty(page.AzureResourceGroup))
        {
            await _azureDeployer.DeleteAsync(page.AzureResourceGroup, ct, page.AzureSubscriptionId);
            _logger.LogInformation("Torn down Azure resources for page {Id}: {RG}", pageId, page.AzureResourceGroup);
            page.AzureResourceGroup = null;
            page.AzureSubscriptionId = null;
            page.EstimatedMonthlyCostUsd = null;
        }
        else if (!string.IsNullOrEmpty(page.GitHubRepo))
        {
            await _deployer.DeleteRepoAsync(page.GitHubRepo, ct);
            _logger.LogInformation("Torn down GitHub Pages for page {Id}: {Repo}", pageId, page.GitHubRepo);
            page.GitHubRepo = null;
        }

        page.DeployedUrl = null;
        page.DeployedAt = null;
        page.DeploymentTarget = null;
        page.Status = PageStatus.AwaitingApproval;
        await _repository.UpdatePageAsync(page, ct);
    }

    public async Task DeleteSolutionAsync(int pageId, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(pageId, ct)
            ?? throw new KeyNotFoundException($"Page {pageId} not found");

        if (!string.IsNullOrEmpty(page.AzureResourceGroup))
        {
            try
            {
                await _azureDeployer.DeleteAsync(page.AzureResourceGroup, ct, page.AzureSubscriptionId);
                _logger.LogInformation("Deleted Azure resource group {RG} for page {Id}", page.AzureResourceGroup, pageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Azure resource group {RG}, continuing with local cleanup", page.AzureResourceGroup);
            }
        }

        if (!string.IsNullOrEmpty(page.GitHubRepo))
        {
            try
            {
                await _deployer.DeleteRepoAsync(page.GitHubRepo, ct);
                _logger.LogInformation("Deleted remote repo {Repo} for page {Id}", page.GitHubRepo, pageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete remote repo {Repo}, continuing with local cleanup", page.GitHubRepo);
            }
        }

        if (!string.IsNullOrEmpty(page.SolutionPath) && Directory.Exists(page.SolutionPath))
        {
            Directory.Delete(page.SolutionPath, recursive: true);
            _logger.LogInformation("Deleted local solution at {Path} for page {Id}", page.SolutionPath, pageId);
        }

        if (!string.IsNullOrEmpty(page.SolutionPath))
        {
            try { await _solutionStorage.DeleteArchiveAsync(page.SolutionPath, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete archive for page {Id}", pageId); }
        }

        page.SolutionPath = null;
        page.GitHubRepo = null;
        page.AzureResourceGroup = null;
        page.DeploymentTarget = null;
        page.EstimatedMonthlyCostUsd = null;
        page.DeployedUrl = null;
        page.DeployedAt = null;

        if (page.Status is PageStatus.AwaitingApproval or PageStatus.DeployFailed or PageStatus.Deployed)
            page.Status = PageStatus.Analyzed;

        await _repository.UpdatePageAsync(page, ct);
    }
}
