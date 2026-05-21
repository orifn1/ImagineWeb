using Azure.Identity;
using ImagineWeb.Api;
using ImagineWeb.Core.Services;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Azure Key Vault (optional — loads secrets if KeyVaultName is configured) ──
var keyVaultName = builder.Configuration["KeyVaultName"];
if (!string.IsNullOrEmpty(keyVaultName))
{
    var kvUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
    builder.Configuration.AddAzureKeyVault(kvUri, new DefaultAzureCredential());
}

// Resolve solutions path — respects EXECUTOR__SOLUTIONSBASEPATH env var (e.g., /home/solutions on Azure)
var configuredSolutionsPath = builder.Configuration["Executor:SolutionsBasePath"];
if (string.IsNullOrEmpty(configuredSolutionsPath) || !Path.IsPathRooted(configuredSolutionsPath))
{
    var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
    builder.Configuration["Executor:SolutionsBasePath"] = Path.Combine(repoRoot, "solutions");
}

// ── DB-backed settings (overrides appsettings.json) ──────────
var dbPath = builder.Configuration["Database:Path"] ?? Path.Combine(AppContext.BaseDirectory, "hunter.db");
var dbConnectionString = $"Data Source={dbPath}";
builder.Configuration.AddDbConfiguration(dbConnectionString);

// ── Service Registration ──────────────────────────────────────
builder.Services.AddImagineWebServices(builder.Configuration, dbConnectionString);

var app = builder.Build();

// ── Database Migrations & Recovery ────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
    await DatabaseMigrator.MigrateAsync(db, app.Logger);
}

// ── Migrate existing solutions to Azure/ subfolder ────────────
{
    var solBase = builder.Configuration["Executor:SolutionsBasePath"]!;
    var azureDir = Path.Combine(solBase, "Azure");
    if (!Directory.Exists(azureDir))
        Directory.CreateDirectory(azureDir);

    var androidDir = Path.Combine(solBase, "Android");
    if (!Directory.Exists(androidDir))
        Directory.CreateDirectory(androidDir);

    // Move clarify-* dirs and zips from root to Azure/
    if (Directory.Exists(solBase))
    {
        foreach (var dir in Directory.GetDirectories(solBase, "clarify-*"))
        {
            var dest = Path.Combine(azureDir, Path.GetFileName(dir));
            if (!Directory.Exists(dest))
            {
                Directory.Move(dir, dest);
                app.Logger.LogInformation("Migrated solution folder {Source} → {Dest}", dir, dest);
            }
        }
        foreach (var zip in Directory.GetFiles(solBase, "clarify-*.zip"))
        {
            var dest = Path.Combine(azureDir, Path.GetFileName(zip));
            if (!File.Exists(dest))
            {
                File.Move(zip, dest);
                app.Logger.LogInformation("Migrated solution archive {Source} → {Dest}", zip, dest);
            }
        }
    }
}

// ── Shutdown: Ctrl+C handler ──────────────────────────────────
var shutdownManager = app.Services.GetRequiredService<ShutdownManager>();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!shutdownManager.IsShuttingDown)
    {
        Console.WriteLine("\n⏸  Graceful shutdown initiated. Press Ctrl+C again for immediate stop.");
        shutdownManager.RequestGraceful();
    }
    else
    {
        Console.WriteLine("\n⏹  Immediate shutdown! Saving what we have...");
        shutdownManager.RequestImmediate();
    }
};

// ── Middleware ─────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();

var solutionsDir = builder.Configuration["Executor:SolutionsBasePath"]!;
var screenshotsPath = Path.Combine(solutionsDir, ".screenshots");
if (!Directory.Exists(screenshotsPath)) Directory.CreateDirectory(screenshotsPath);
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(screenshotsPath),
    RequestPath = "/screenshots"
});

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("""
    
    ╔══════════════════════════════════════════════════════════╗
    ║   ImagineWeb — Opportunity Discovery Platform       ║
    ║                                                          ║
    ║   Dashboard:  https://localhost:5556                      ║
    ║   Findings:   https://localhost:5556/api/hunter/report    ║
    ║   Settings:   https://localhost:5556/settings             ║
    ║                                                          ║
    ║   Press Ctrl+C for graceful stop                         ║
    ║   Press Ctrl+C twice for immediate stop                  ║
    ╚══════════════════════════════════════════════════════════╝
    
    """);
});

if (app.Environment.IsDevelopment())
{
    app.Urls.Add("http://0.0.0.0:5555");
    app.Urls.Add("https://0.0.0.0:5556");
}
app.Run();
