using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class GitHubPagesDeployer : IGitHubPagesDeployer
{
    private readonly ExecutorConfig _config;
    private readonly ILogger<GitHubPagesDeployer> _logger;

    public GitHubPagesDeployer(IOptions<ExecutorConfig> config, ILogger<GitHubPagesDeployer> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<bool> IsGhCliAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, _) = await RunProcessAsync("gh", "--version", null, ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CreateRepoAndDeployAsync(string repoName, string solutionPath, CancellationToken ct)
    {
        var sitePath = Path.Combine(solutionPath, "site");
        if (!Directory.Exists(sitePath))
            throw new DirectoryNotFoundException($"Expected a 'site/' folder in {solutionPath}");

        _logger.LogInformation("Creating GitHub repo {Repo} and deploying from {Path}", repoName, sitePath);

        await RunProcessAsync("git", "init", sitePath, ct);
        await RunProcessAsync("git", "add -A", sitePath, ct);
        await RunProcessAsync("git", "commit -m \"Initial deployment by ImagineWeb\"", sitePath, ct);

        var (_, branchOutput) = await RunProcessAsync("git", "rev-parse --abbrev-ref HEAD", sitePath, ct);
        var branch = branchOutput.Trim();
        if (string.IsNullOrEmpty(branch)) branch = "master";

        var ghUser = _config.GitHubUsername;
        if (string.IsNullOrEmpty(ghUser))
        {
            var (_, whoami) = await RunProcessAsync("gh", "api user --jq .login", null, ct);
            ghUser = whoami.Trim();
        }

        await RunProcessAsync("gh", $"repo create {repoName} --public --source . --push", sitePath, ct);

        await Task.Delay(2000, ct);

        await RunProcessAsync("gh", $"api repos/{ghUser}/{repoName}/pages -X POST -f build_type=legacy -f source[branch]={branch} -f source[path]=/", sitePath, ct);

        var deployedUrl = $"https://{ghUser}.github.io/{repoName}/";
        _logger.LogInformation("Deployed to {Url}", deployedUrl);
        return deployedUrl;
    }

    public async Task DeleteRepoAsync(string repoName, CancellationToken ct)
    {
        _logger.LogInformation("Deleting GitHub repo {Repo}", repoName);

        var (exitCode, output) = await RunProcessAsync("gh", $"repo delete {repoName} --yes", null, ct);

        if (exitCode != 0 && output.Contains("delete_repo", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Missing delete_repo scope, requesting it now");
            await RunProcessAsync("gh", "auth refresh -h github.com -s delete_repo", null, ct);
            (exitCode, output) = await RunProcessAsync("gh", $"repo delete {repoName} --yes", null, ct);
        }

        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to delete repo {repoName}: {output}");

        _logger.LogInformation("Deleted GitHub repo {Repo}", repoName);
    }

    private async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string? workingDirectory, CancellationToken ct)
    {
        var resolvedPath = ResolveExecutable(fileName);
        _logger.LogDebug("Running: {File} {Args} in {Dir}", resolvedPath, arguments, workingDirectory ?? ".");

        var psi = new ProcessStartInfo
        {
            FileName = resolvedPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {resolvedPath}");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("{File} {Args} exited with code {Code}: {Error}",
                resolvedPath, arguments, process.ExitCode, error);
        }

        return (process.ExitCode, output);
    }

    private static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar))
            return fileName;

        var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var combinedPath = string.Join(Path.PathSeparator.ToString(), processPath, machinePath, userPath);

        var extensions = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';') ?? [".exe", ".cmd", ".bat"];

        foreach (var dir in combinedPath.Split(Path.PathSeparator).Distinct())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;

            foreach (var ext in extensions)
            {
                var withExt = candidate + ext;
                if (File.Exists(withExt)) return withExt;
            }
        }

        return fileName;
    }
}
