using Microsoft.EntityFrameworkCore;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Data;

public class HunterDbContext : DbContext
{
    public DbSet<DiscoveredPage> Pages => Set<DiscoveredPage>();
    public DbSet<SearchTopic> Topics => Set<SearchTopic>();
    public DbSet<SeenUrl> SeenUrls => Set<SeenUrl>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DeployedSite> DeployedSites => Set<DeployedSite>();
    public DbSet<ShowcaseEntry> ShowcaseEntries => Set<ShowcaseEntry>();

    public HunterDbContext(DbContextOptions<HunterDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<DiscoveredPage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Url).IsUnique();
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.ProfitScore);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Domain);

            entity.Property(e => e.Url).HasMaxLength(2048);
            entity.Property(e => e.Domain).HasMaxLength(256);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.ProfitCategory).HasMaxLength(64);

            entity.Property(e => e.OpportunityType).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.EstimatedEffort).HasMaxLength(256);
            entity.Property(e => e.EstimatedReward).HasMaxLength(256);
            entity.HasIndex(e => e.OpportunityType);

            entity.Property(e => e.SolutionPath).HasMaxLength(512);
            entity.Property(e => e.DeployedUrl).HasMaxLength(2048);
            entity.Property(e => e.GitHubRepo).HasMaxLength(256);
            entity.Property(e => e.TargetAudience).HasMaxLength(1024);
            entity.Property(e => e.SiteBuildReason).HasMaxLength(1024);

            entity.Property(e => e.DeploymentTarget).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.AzureResourceGroup).HasMaxLength(256);
            entity.Property(e => e.EstimatedMonthlyCostUsd).HasColumnType("decimal(10,4)");

            entity.Property(e => e.MarketValidation).HasMaxLength(2048);
            entity.Property(e => e.AnalysisProvider).HasMaxLength(32);
            entity.Property(e => e.Differentiator).HasMaxLength(2048);

            entity.Property(e => e.PageContactEmails).HasMaxLength(1024);
            entity.Property(e => e.PageContactFormUrl).HasMaxLength(2048);
            entity.Property(e => e.PageSocialLinks).HasMaxLength(2048);
            entity.Property(e => e.PageAuthorName).HasMaxLength(256);
            entity.Property(e => e.BacklinkType).HasMaxLength(64);
            entity.Property(e => e.BacklinkReason).HasMaxLength(1024);
        });

        modelBuilder.Entity<SearchTopic>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Query);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Priority);
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.Query).HasMaxLength(512);
            entity.Property(e => e.Category).HasMaxLength(128);
            entity.Property(e => e.Origin).HasMaxLength(32);
            entity.Property(e => e.Strategy).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<SeenUrl>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Url).IsUnique();
            entity.Property(e => e.Url).HasMaxLength(2048);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256);
        });

        modelBuilder.Entity<DeployedSite>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.TornDown);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.DeploymentTarget).HasMaxLength(32);
            e.Property(x => x.AzureResourceGroup).HasMaxLength(256);
            e.Property(x => x.AzureSubscriptionId).HasMaxLength(64);
            e.Property(x => x.GitHubRepo).HasMaxLength(256);
        });

        modelBuilder.Entity<ShowcaseEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Visible, x.SortOrder });
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.ThumbnailUrl).HasMaxLength(2048);
        });
    }
}
