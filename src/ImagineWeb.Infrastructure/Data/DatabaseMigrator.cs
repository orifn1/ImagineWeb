using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Data;

public static class DatabaseMigrator
{
    public static async Task MigrateAsync(HunterDbContext db, ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        await MigratePageColumnsAsync(db, conn);
        await MigrateTopicColumnsAsync(db, conn);
        await EnsureAppSettingsTableAsync(db);
        await EnsureShowcaseTableAsync(db, conn);
        await EnsureDeployedSitesTableAsync(db);
        await RecoverStuckPagesAsync(db, logger);
    }

    private static async Task MigratePageColumnsAsync(HunterDbContext db, System.Data.Common.DbConnection conn)
    {
        var existingColumns = await GetTableColumnsAsync(conn, "Pages");

        (string column, string sql)[] migrations =
        [
            ("DeploymentTarget", "ALTER TABLE Pages ADD COLUMN DeploymentTarget TEXT NULL"),
            ("AzureResourceGroup", "ALTER TABLE Pages ADD COLUMN AzureResourceGroup TEXT NULL"),
            ("EstimatedMonthlyCostUsd", "ALTER TABLE Pages ADD COLUMN EstimatedMonthlyCostUsd TEXT NULL"),
            ("GenerationId", "ALTER TABLE Pages ADD COLUMN GenerationId TEXT NULL"),
            ("EvidenceCitations", "ALTER TABLE Pages ADD COLUMN EvidenceCitations TEXT NULL"),
            ("MarketValidation", "ALTER TABLE Pages ADD COLUMN MarketValidation TEXT NULL"),
            ("OpportunityScore", "ALTER TABLE Pages ADD COLUMN OpportunityScore INTEGER NOT NULL DEFAULT 0"),
            ("ExecutionScore", "ALTER TABLE Pages ADD COLUMN ExecutionScore INTEGER NOT NULL DEFAULT 0"),
            ("AnalysisProvider", "ALTER TABLE Pages ADD COLUMN AnalysisProvider TEXT NULL"),
            ("CompetitorUrls", "ALTER TABLE Pages ADD COLUMN CompetitorUrls TEXT NULL"),
            ("Differentiator", "ALTER TABLE Pages ADD COLUMN Differentiator TEXT NULL"),
            ("LaunchChecklist", "ALTER TABLE Pages ADD COLUMN LaunchChecklist TEXT NULL"),
            ("Risks", "ALTER TABLE Pages ADD COLUMN Risks TEXT NULL"),
            ("DataSources", "ALTER TABLE Pages ADD COLUMN DataSources TEXT NULL"),
            ("ContentQualityScore", "ALTER TABLE Pages ADD COLUMN ContentQualityScore INTEGER NOT NULL DEFAULT 0"),
            ("CompetitorData", "ALTER TABLE Pages ADD COLUMN CompetitorData TEXT NULL"),
            ("EnrichmentData", "ALTER TABLE Pages ADD COLUMN EnrichmentData TEXT NULL"),
            ("Phase2Skipped", "ALTER TABLE Pages ADD COLUMN Phase2Skipped INTEGER NOT NULL DEFAULT 0"),
            ("DistributionScore", "ALTER TABLE Pages ADD COLUMN DistributionScore INTEGER NOT NULL DEFAULT 0"),
            ("DistributionChannels", "ALTER TABLE Pages ADD COLUMN DistributionChannels TEXT NULL"),
            ("PageContactEmails", "ALTER TABLE Pages ADD COLUMN PageContactEmails TEXT NULL"),
            ("PageContactFormUrl", "ALTER TABLE Pages ADD COLUMN PageContactFormUrl TEXT NULL"),
            ("PageSocialLinks", "ALTER TABLE Pages ADD COLUMN PageSocialLinks TEXT NULL"),
            ("PageAuthorName", "ALTER TABLE Pages ADD COLUMN PageAuthorName TEXT NULL"),
            ("IsBacklinkCandidate", "ALTER TABLE Pages ADD COLUMN IsBacklinkCandidate INTEGER NOT NULL DEFAULT 0"),
            ("BacklinkType", "ALTER TABLE Pages ADD COLUMN BacklinkType TEXT NULL"),
            ("BacklinkReason", "ALTER TABLE Pages ADD COLUMN BacklinkReason TEXT NULL"),
            ("UserId", "ALTER TABLE Pages ADD COLUMN UserId TEXT NULL"),
            ("AzureSubscriptionId", "ALTER TABLE Pages ADD COLUMN AzureSubscriptionId TEXT NULL"),
            ("DeployedResources", "ALTER TABLE Pages ADD COLUMN DeployedResources TEXT NULL"),
            ("InterestingnessScore", "ALTER TABLE Pages ADD COLUMN InterestingnessScore INTEGER NOT NULL DEFAULT 0"),
            ("SiteConcept", "ALTER TABLE Pages ADD COLUMN SiteConcept TEXT NULL"),
            ("UniqueAngle", "ALTER TABLE Pages ADD COLUMN UniqueAngle TEXT NULL")
        ];

        foreach (var (column, sql) in migrations)
        {
            if (!existingColumns.Contains(column))
                await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task MigrateTopicColumnsAsync(HunterDbContext db, System.Data.Common.DbConnection conn)
    {
        var topicColumns = await GetTableColumnsAsync(conn, "Topics");

        (string column, string sql)[] topicMigrations =
        [
            ("AvgPageScore", "ALTER TABLE Topics ADD COLUMN AvgPageScore REAL NOT NULL DEFAULT 0"),
            ("HighValueCount", "ALTER TABLE Topics ADD COLUMN HighValueCount INTEGER NOT NULL DEFAULT 0"),
            ("TotalPagesProduced", "ALTER TABLE Topics ADD COLUMN TotalPagesProduced INTEGER NOT NULL DEFAULT 0"),
            ("Strategy", "ALTER TABLE Topics ADD COLUMN Strategy TEXT NOT NULL DEFAULT 'Broad'"),
            ("UserId", "ALTER TABLE Topics ADD COLUMN UserId TEXT NULL")
        ];

        foreach (var (column, sql) in topicMigrations)
        {
            if (!topicColumns.Contains(column))
                await db.Database.ExecuteSqlRawAsync(sql);
        }

        await db.Database.ExecuteSqlRawAsync("UPDATE Topics SET Strategy = 'Broad' WHERE Strategy IS NULL");
    }

    private static async Task EnsureAppSettingsTableAsync(HunterDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT '0001-01-01'
            )
        """);
    }

    private static async Task EnsureShowcaseTableAsync(HunterDbContext db, System.Data.Common.DbConnection conn)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ShowcaseEntries" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NULL,
                "Url" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "ThumbnailUrl" TEXT NULL,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                "Visible" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000'
            )
        """);

        var showcaseColumns = await GetTableColumnsAsync(conn, "ShowcaseEntries");
        if (!showcaseColumns.Contains("ShowTitle"))
            await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""ShowcaseEntries"" ADD COLUMN ""ShowTitle"" INTEGER NOT NULL DEFAULT 1");
    }

    private static async Task EnsureDeployedSitesTableAsync(HunterDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "DeployedSites" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL,
                "Url" TEXT NULL,
                "DeploymentTarget" TEXT NULL,
                "AzureResourceGroup" TEXT NULL,
                "AzureSubscriptionId" TEXT NULL,
                "GitHubRepo" TEXT NULL,
                "DailyCreditCost" INTEGER NOT NULL DEFAULT 0,
                "LastDebitedOn" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000',
                "TornDown" INTEGER NOT NULL DEFAULT 0,
                "DeployedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000',
                "TornDownAt" TEXT NULL
            )
        """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_DeployedSites_UserId" ON "DeployedSites" ("UserId");
        """);
    }

    private static async Task RecoverStuckPagesAsync(HunterDbContext db, ILogger logger)
    {
        var stuck = await db.Pages
            .Where(p => p.Status == PageStatus.Implementing || p.Status == PageStatus.Deploying)
            .ToListAsync();

        foreach (var p in stuck)
        {
            var hasCompletedSolution = !string.IsNullOrEmpty(p.SolutionPath) && Directory.Exists(p.SolutionPath);
            if (hasCompletedSolution && p.Status == PageStatus.Implementing)
            {
                logger.LogWarning("Recovering stuck page {Id} from Implementing → AwaitingApproval (solution exists at {Path})", p.Id, p.SolutionPath);
                p.Status = PageStatus.AwaitingApproval;
            }
            else
            {
                logger.LogWarning("Recovering stuck page {Id} from {Status} → Analyzed", p.Id, p.Status);
                p.Status = PageStatus.Analyzed;
            }
        }

        if (stuck.Count > 0)
            await db.SaveChangesAsync();
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(System.Data.Common.DbConnection conn, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
