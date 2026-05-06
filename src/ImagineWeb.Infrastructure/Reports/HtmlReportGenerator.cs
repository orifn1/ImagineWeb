using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Reports;

public class HtmlReportGenerator : IReportGenerator
{
    private readonly IHunterRepository _repository;

    public HtmlReportGenerator(IHunterRepository repository) => _repository = repository;

    public async Task<string> GenerateReportAsync(CancellationToken ct)
    {
        var pages = await _repository.GetAllAnalyzedPagesAsync(ct);
        var topics = await _repository.GetAllTopicsAsync(ct);
        return BuildHtml(pages, topics, isFinal: true);
    }

    public async Task<string> GeneratePartialReportAsync(CancellationToken ct)
    {
        var pages = await _repository.GetAllAnalyzedPagesAsync(ct);
        var topics = await _repository.GetAllTopicsAsync(ct);
        return BuildHtml(pages, topics, isFinal: false);
    }



    private static string BuildHtml(List<DiscoveredPage> pages, List<SearchTopic> topics, bool isFinal)
    {
        var sb = new StringBuilder();
        var reportType = isFinal ? "Final" : "Interim";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

        var highValue = pages.Count(p => Math.Max(p.ProfitScore, p.InterestingnessScore) >= 8);
        var avgScore = pages.Count > 0 ? pages.Average(p => Math.Max(p.ProfitScore, p.InterestingnessScore)) : 0;
        var uniqueDomains = pages
            .Where(p => !string.IsNullOrEmpty(p.Domain))
            .GroupBy(p => p.Domain)
            .OrderByDescending(g => g.Average(p => Math.Max(p.ProfitScore, p.InterestingnessScore)))
            .Select(g => new { Domain = g.Key, AvgScore = g.Average(p => Math.Max(p.ProfitScore, p.InterestingnessScore)), Count = g.Count() })
            .ToList();

        sb.AppendLine($"""
            <div class="page-header">
                <h1>Findings Report</h1>
                <p>{reportType} Report &mdash; Generated {timestamp}</p>
            </div>
            """);

        sb.AppendLine("<div class=\"row g-3 mb-4\">");
        sb.AppendLine($"""
            <div class="col-sm-6 col-md-3 fade-in stagger-1">
                <div class="card stat-card stat-card-amber glow-card" style="cursor:pointer" title="Click to filter high-value findings (score >= 8)" onclick="filterHighValue()">
                    <div class="stat-icon"><i class="bi bi-trophy"></i></div>
                    <div class="stat-value">{highValue}</div>
                    <div class="stat-label">High-Value Finds</div>
                </div>
            </div>
            """);
        sb.AppendLine($"""
            <div class="col-sm-6 col-md-3 fade-in stagger-2">
                <div class="card stat-card stat-card-purple glow-card" title="Average best score (max of interestingness, profit) across all {pages.Count} analyzed pages">
                    <div class="stat-icon"><i class="bi bi-graph-up"></i></div>
                    <div class="stat-value">{avgScore:F1}</div>
                    <div class="stat-label">Avg Score</div>
                </div>
            </div>
            """);
        sb.AppendLine($"""
            <div class="col-sm-6 col-md-3 fade-in stagger-3">
                <div class="card stat-card stat-card-blue glow-card">
                    <div class="stat-icon"><i class="bi bi-layers"></i></div>
                    <div class="stat-value">{pages.Count}</div>
                    <div class="stat-label">Total Analyzed</div>
                </div>
            </div>
            """);
        sb.AppendLine($"""
            <div class="col-sm-6 col-md-3 fade-in stagger-4">
                <div class="card stat-card stat-card-teal glow-card" style="cursor:pointer" onclick="toggleDomains()">
                    <div class="stat-icon"><i class="bi bi-globe"></i></div>
                    <div class="stat-value">{uniqueDomains.Count}</div>
                    <div class="stat-label">Domains <i class="bi bi-chevron-down" style="font-size:0.5rem"></i></div>
                </div>
            </div>
            """);
        sb.AppendLine("</div>");

        if (uniqueDomains.Count > 0)
        {
            sb.AppendLine("<div id=\"domainList\" class=\"mb-4\" style=\"display:none\">");
            sb.AppendLine("<div class=\"card\"><div class=\"card-body py-2\">");
            sb.AppendLine("<table class=\"table table-sm table-borderless mb-0 small\">");
            sb.AppendLine("<thead><tr><th>Domain</th><th class=\"text-end\">Avg Score</th><th class=\"text-end\">Pages</th></tr></thead><tbody>");
            foreach (var d in uniqueDomains.Take(15))
            {
                var scoreClass = d.AvgScore >= 7 ? "text-success fw-bold" : d.AvgScore >= 5 ? "text-warning fw-bold" : "text-muted";
                sb.AppendLine($"<tr><td>{HttpUtility.HtmlEncode(d.Domain)}</td><td class=\"text-end {scoreClass}\">{d.AvgScore:F1}</td><td class=\"text-end\">{d.Count}</td></tr>");
            }
            sb.AppendLine("</tbody></table></div></div></div>");
        }

        var oppGroups = pages
            .Where(p => p.OpportunityType != OpportunityType.None)
            .GroupBy(p => p.OpportunityType)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (oppGroups.Count > 0)
        {
            sb.AppendLine("<div class=\"row g-3 mb-4\">");
            sb.AppendLine("<div class=\"col-lg-8\">");
            sb.AppendLine("<h5 class=\"section-title\"><i class=\"bi bi-bullseye me-2\"></i>Opportunity Breakdown <small class=\"text-muted fw-normal\">(click to filter)</small></h5>");
            sb.AppendLine("<div class=\"row g-2\">");
            foreach (var g in oppGroups)
            {
                var oppType = HttpUtility.HtmlEncode(g.Key.ToString());
                sb.AppendLine($"""
                    <div class="col-sm-6 col-md-4">
                        <div class="card stat-card opp-filter glow-card" data-opp="{oppType}" style="cursor:pointer;padding:.75rem" onclick="filterByOpportunity('{oppType}')">
                            <div class="stat-value" style="font-size:1.25rem">{g.Count()}</div>
                            <div class="stat-label">{oppType}</div>
                        </div>
                    </div>
                    """);
            }
            sb.AppendLine("</div></div>");
            sb.AppendLine("<div class=\"col-lg-4\"><div class=\"card h-100\"><div class=\"card-body d-flex flex-column\">");
            sb.AppendLine("<h6 class=\"fw-semibold mb-2\" style=\"font-size:0.8rem\"><i class=\"bi bi-pie-chart me-1\"></i>Distribution</h6>");
            sb.AppendLine("<div class=\"flex-grow-1 d-flex align-items-center justify-content-center\" style=\"min-height:140px\"><canvas id=\"oppChart\"></canvas></div>");
            sb.AppendLine("</div></div></div>");
            sb.AppendLine("</div>");

            // Build Chart.js data inline
            var oppLabels = string.Join(",", oppGroups.Select(g => $"'{HttpUtility.JavaScriptStringEncode(g.Key.ToString())}'"));
            var oppData = string.Join(",", oppGroups.Select(g => g.Count()));
            var oppColors = new[] { "rgba(79,70,229,0.8)", "rgba(124,58,237,0.8)", "rgba(6,182,212,0.8)", "rgba(16,185,129,0.8)", "rgba(245,158,11,0.8)", "rgba(239,68,68,0.7)", "rgba(99,102,241,0.7)", "rgba(34,211,238,0.7)" };
            var oppColorsStr = string.Join(",", oppGroups.Select((_, i) => $"'{oppColors[i % oppColors.Length]}'"));
            sb.AppendLine("<script>");
            sb.AppendLine("(function(){");
            sb.AppendLine("var ctx=document.getElementById('oppChart');");
            sb.AppendLine("if(!ctx||typeof Chart==='undefined')return;");
            sb.AppendLine($"new Chart(ctx,{{type:'doughnut',data:{{labels:[{oppLabels}],datasets:[{{data:[{oppData}],backgroundColor:[{oppColorsStr}],borderWidth:0,borderRadius:3,spacing:2}}]}},options:{{cutout:'60%',responsive:true,maintainAspectRatio:false,plugins:{{legend:{{position:'bottom',labels:{{boxWidth:10,padding:8,font:{{size:10}},usePointStyle:true,pointStyle:'rectRounded'}}}}}}}}}});");
            sb.AppendLine("})();");
            sb.AppendLine("</script>");
        }

        var allFindings = pages.Where(p => Math.Max(p.ProfitScore, p.InterestingnessScore) > 0)
            .OrderByDescending(p => Math.Max(p.ProfitScore, p.InterestingnessScore)).ToList();
        if (allFindings.Count > 0)
        {
            sb.AppendLine($"""
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h5 class="section-title mb-0 border-0 pb-0"><i class="bi bi-trophy me-2"></i>Findings (<span id="visibleCount">{allFindings.Count}</span>)</h5>
                    <div class="d-flex gap-2 align-items-center">
                        <button class="btn btn-sm btn-outline-secondary d-none" id="btnClearFilter" onclick="clearFilter()"><i class="bi bi-x-circle me-1"></i>Clear filter</button>
                        <select class="form-select form-select-sm" style="width:auto" id="sortSelect" onchange="sortFindings(this.value)">
                            <option value="score-desc">Score (high → low)</option>
                            <option value="score-asc">Score (low → high)</option>
                            <option value="feasibility-desc">Feasibility (high → low)</option>
                            <option value="sitebuild-desc">Site Build (high → low)</option>
                            <option value="opportunity">Opportunity Type</option>
                            <option value="domain">Domain</option>
                        </select>
                    </div>
                </div>
                """);
            sb.AppendLine("<div id=\"findingsContainer\">");
            foreach (var page in allFindings)
                AppendFinding(sb, page);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("""
            <style>
            .finding-toggle { padding: 0.1rem 0.4rem; line-height: 1; }
            .finding-toggle i { transition: transform 0.2s ease; display: inline-block; }
            .finding-toggle[aria-expanded="true"] i { transform: rotate(180deg); }
            </style>
            <script>
            // Rotate chevron icon when collapse opens/closes
            document.addEventListener('show.bs.collapse', function(e) {
                var btn = document.querySelector('[data-bs-target="#' + e.target.id + '"]');
                if (btn && btn.classList.contains('finding-toggle')) btn.setAttribute('aria-expanded', 'true');
            });
            document.addEventListener('hide.bs.collapse', function(e) {
                var btn = document.querySelector('[data-bs-target="#' + e.target.id + '"]');
                if (btn && btn.classList.contains('finding-toggle')) btn.setAttribute('aria-expanded', 'false');
            });
            function sortFindings(criteria) {
                const container = document.getElementById('findingsContainer');
                if (!container) return;
                const items = Array.from(container.querySelectorAll('.card'));
                items.sort((a, b) => {
                    switch (criteria) {
                        case 'score-desc': return (+b.dataset.score) - (+a.dataset.score);
                        case 'score-asc': return (+a.dataset.score) - (+b.dataset.score);
                        case 'feasibility-desc': return (+b.dataset.feasibility) - (+a.dataset.feasibility);
                        case 'sitebuild-desc': return (+b.dataset.sitebuild) - (+a.dataset.sitebuild);
                        case 'opportunity': return a.dataset.opportunity.localeCompare(b.dataset.opportunity);
                        case 'domain': return a.dataset.domain.localeCompare(b.dataset.domain);
                        default: return 0;
                    }
                });
                items.forEach(el => container.appendChild(el));
            }
            function toggleDomains() {
                var el = document.getElementById('domainList');
                if (el) el.style.display = el.style.display === 'none' ? '' : 'none';
            }
            function filterHighValue() {
                clearFilter();
                const container = document.getElementById('findingsContainer');
                if (!container) return;
                const items = container.querySelectorAll('.card');
                var visible = 0;
                items.forEach(el => {
                    if (+el.dataset.score >= 7) { el.style.display = ''; visible++; }
                    else { el.style.display = 'none'; }
                });
                document.getElementById('visibleCount').textContent = visible;
                activeFilter = '__highvalue__';
                var clearBtn = document.getElementById('btnClearFilter');
                if (clearBtn) clearBtn.classList.remove('d-none');
                container.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
            var activeFilter = null;
            function filterByOpportunity(type) {
                const container = document.getElementById('findingsContainer');
                if (!container) return;
                const items = container.querySelectorAll('.card');
                const clearBtn = document.getElementById('btnClearFilter');
                if (activeFilter === type) { clearFilter(); return; }
                activeFilter = type;
                var visible = 0;
                items.forEach(el => {
                    if (el.dataset.opportunity === type) { el.style.display = ''; visible++; }
                    else { el.style.display = 'none'; }
                });
                document.getElementById('visibleCount').textContent = visible;
                if (clearBtn) clearBtn.classList.remove('d-none');
                document.querySelectorAll('.opp-filter').forEach(c => {
                    c.style.outline = c.dataset.opp === type ? '2px solid var(--at-accent, #4f46e5)' : '';
                });
            }
            function clearFilter() {
                activeFilter = null;
                const container = document.getElementById('findingsContainer');
                if (!container) return;
                const items = container.querySelectorAll('.card');
                items.forEach(el => el.style.display = '');
                document.getElementById('visibleCount').textContent = items.length;
                var clearBtn = document.getElementById('btnClearFilter');
                if (clearBtn) clearBtn.classList.add('d-none');
                document.querySelectorAll('.opp-filter').forEach(c => c.style.outline = '');
            }
            </script>
            <script>
            (async function populateProviderSelects() {
                if (!window.LlmProviderUI) return;
                try {
                    var data = await window.LlmProviderUI.load();
                    var selects = document.querySelectorAll('select[id^="provider-"]');
                    selects.forEach(function(sel) {
                        data.providers.forEach(function(p) {
                            var opt = document.createElement('option');
                            opt.value = p.key;
                            opt.textContent = p.label + (p.configured ? '' : ' (not configured)');
                            if (!p.configured) opt.disabled = true;
                            sel.appendChild(opt);
                        });
                    });
                } catch(e) { /* provider data unavailable */ }
            })();
            </script>
            """);

        return sb.ToString();
    }

    private static void AppendStat(StringBuilder sb, string number, string label)
    {
        sb.AppendLine($"""
            <div class="col-sm-6 col-md-4 col-lg-3">
                <div class="card stat-card">
                    <div class="stat-value">{HttpUtility.HtmlEncode(number)}</div>
                    <div class="stat-label">{HttpUtility.HtmlEncode(label)}</div>
                </div>
            </div>
            """);
    }

    private static void AppendFinding(StringBuilder sb, DiscoveredPage page)
    {
        var bestScore = Math.Max(page.ProfitScore, page.InterestingnessScore);
        var scoreColor = bestScore >= 8 ? "#10b981" : bestScore >= 4 ? "#f59e0b" : "#ef4444";
        var scoreClass = bestScore >= 8 ? "badge-score-high" : bestScore >= 4 ? "badge-score-mid" : "badge-score-low";
        var encodedUrl = HttpUtility.HtmlEncode(page.Url);
        var encodedTitle = HttpUtility.HtmlEncode(string.IsNullOrEmpty(page.Title) ? page.Url : page.Title);
        var domainAttr = HttpUtility.HtmlEncode(page.Domain ?? "");
        var oppAttr = HttpUtility.HtmlEncode(page.OpportunityType.ToString());
        var collapseId = $"finding-{page.Id}";
        var ringSize = 44;
        var ringR = (ringSize - 6) / 2;
        var ringCirc = 2 * Math.PI * ringR;
        var ringOffset = ringCirc * (1.0 - Math.Min(bestScore / 10.0, 1.0));

        sb.AppendLine($"<div class=\"card mb-3 glow-card\" data-score=\"{bestScore}\" data-feasibility=\"{page.FeasibilityScore}\" data-sitebuild=\"{page.SiteBuildScore}\" data-opportunity=\"{oppAttr}\" data-domain=\"{domainAttr}\">");
        sb.AppendLine("<div class=\"card-body\">");

        sb.AppendLine("<div class=\"d-flex align-items-center gap-3 mb-2\">");
        sb.AppendLine($"""
            <div class="score-ring" style="width:{ringSize}px;height:{ringSize}px;flex-shrink:0" data-bs-toggle="tooltip" title="Best Score: {bestScore}/10 (Interest: {page.InterestingnessScore}, Profit: {page.ProfitScore})">
                <svg width="{ringSize}" height="{ringSize}"><circle class="ring-bg" cx="{ringSize / 2}" cy="{ringSize / 2}" r="{ringR}" fill="none" stroke-width="4"/>
                <circle class="ring-fg" cx="{ringSize / 2}" cy="{ringSize / 2}" r="{ringR}" fill="none" stroke="{scoreColor}" stroke-width="4" stroke-linecap="round" stroke-dasharray="{ringCirc:F1}" stroke-dashoffset="{ringOffset:F1}"/></svg>
                <span class="ring-value">{bestScore}</span>
            </div>
            """);
        sb.AppendLine("<div class=\"flex-grow-1\">");
        sb.AppendLine($"<div class=\"d-flex align-items-center gap-2 flex-wrap\"><span class=\"badge bg-dark\" style=\"font-size:0.65rem\">#{page.Id}</span>");
        sb.AppendLine($"<a href=\"{encodedUrl}\" target=\"_blank\" class=\"fw-semibold text-decoration-none\">{encodedTitle}</a>");
        sb.AppendLine($"<span class=\"badge\" style=\"background:rgba(var(--at-accent-rgb),0.12);color:var(--at-accent);font-size:0.7rem\">{HttpUtility.HtmlEncode(page.ProfitCategory ?? "Unknown")}</span>");
        if (page.OpportunityType != OpportunityType.None)
            sb.AppendLine($"<span class=\"badge\" style=\"background:var(--at-accent-soft);font-size:0.7rem\">{page.OpportunityType}</span>");
        if (page.Phase2Skipped)
            sb.AppendLine("<span class=\"badge bg-secondary\" style=\"font-size:0.65rem\" title=\"Quick scan only — use Deep Analyze for full plan\">&#9889; Quick scan</span>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<p class=\"text-muted small mb-0 mt-1\">{HttpUtility.HtmlEncode(page.AiSummary ?? "")}</p>");
        sb.AppendLine("</div>");
        sb.AppendLine($"<button class=\"btn btn-sm btn-outline-secondary finding-toggle ms-auto\" data-bs-toggle=\"collapse\" data-bs-target=\"#{collapseId}\" title=\"Details\"><i class=\"bi bi-chevron-down\"></i></button>");
        sb.AppendLine("</div>");

        if (!string.IsNullOrEmpty(page.OpportunityReason))
            sb.AppendLine($"<div class=\"alert alert-success py-2 px-3 small mb-2\"><i class=\"bi bi-bullseye me-1\"></i><strong>Opportunity:</strong> {HttpUtility.HtmlEncode(page.OpportunityReason)}</div>");

        // ── Collapsible details ────────────────────────────────────
        sb.AppendLine($"<div class=\"collapse\" id=\"{collapseId}\">");

        if (!string.IsNullOrEmpty(page.AiRecommendation))
            sb.AppendLine($"<div class=\"alert alert-primary py-2 px-3 small mb-2\"><i class=\"bi bi-lightbulb me-1\"></i>{HttpUtility.HtmlEncode(page.AiRecommendation)}</div>");

        if (!string.IsNullOrEmpty(page.ActionPlan))
        {
            sb.AppendLine("<div class=\"alert alert-light py-2 px-3 small mb-2 border\"><i class=\"bi bi-list-check me-1\"></i><strong>Action Plan:</strong>");
            sb.AppendLine(RenderActionPlanSteps(page.ActionPlan));
            sb.AppendLine("</div>");
        }

        if (!string.IsNullOrEmpty(page.DataSources))
        {
            var sources = page.DataSources.Split("|||", StringSplitOptions.RemoveEmptyEntries);
            if (sources.Length > 0)
            {
                sb.AppendLine("<div class=\"alert alert-info py-2 px-3 small mb-2\"><i class=\"bi bi-database me-1\"></i><strong>Data Sources:</strong> ");
                sb.AppendLine(string.Join(", ", sources.Select(s => HttpUtility.HtmlEncode(s))));
                sb.AppendLine("</div>");
            }
        }

        if (!string.IsNullOrEmpty(page.Risks))
            sb.AppendLine($"<div class=\"alert alert-warning py-2 px-3 small mb-2\"><i class=\"bi bi-exclamation-triangle me-1\"></i><strong>Risks:</strong> {HttpUtility.HtmlEncode(page.Risks)}</div>");

        if (page.DistributionScore > 0 || page.IsBacklinkCandidate || !string.IsNullOrEmpty(page.DistributionChannels))
        {
            var distClass = page.DistributionScore >= 7 ? "alert-success" : page.DistributionScore >= 5 ? "alert-info" : "alert-secondary";
            sb.AppendLine($"<div class=\"alert {distClass} py-2 px-3 small mb-2\">");
            sb.AppendLine($"<i class=\"bi bi-megaphone me-1\"></i><strong>Distribution ({page.DistributionScore}/10):</strong>");
            if (page.IsBacklinkCandidate)
                sb.AppendLine($"<br/><span class=\"badge bg-success me-1\">Backlink: {HttpUtility.HtmlEncode(page.BacklinkType)}</span> {HttpUtility.HtmlEncode(page.BacklinkReason ?? "")}");
            if (!string.IsNullOrEmpty(page.PageContactEmails))
                sb.AppendLine($"<br/>📧 {HttpUtility.HtmlEncode(page.PageContactEmails.Replace("|||", ", "))}");
            if (!string.IsNullOrEmpty(page.PageContactFormUrl))
                sb.AppendLine($"<br/>📋 <a href=\"{HttpUtility.HtmlEncode(page.PageContactFormUrl)}\" target=\"_blank\">Contact form</a>");
            if (!string.IsNullOrEmpty(page.DistributionChannels))
            {
                try
                {
                    var channels = System.Text.Json.JsonSerializer.Deserialize<List<ImagineWeb.Core.Models.DistributionChannel>>(page.DistributionChannels);
                    if (channels is { Count: > 0 })
                        foreach (var ch in channels)
                            sb.AppendLine($"<br/>• <strong>{HttpUtility.HtmlEncode(ch.Method)}</strong>: {HttpUtility.HtmlEncode(ch.Description)} <span class=\"text-muted\">({ch.Effort} effort, {HttpUtility.HtmlEncode(ch.ExpectedReach)})</span>");
                }
                catch { }
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<div class=\"d-flex flex-wrap gap-3 small text-muted mb-2 rounded px-3 py-2\" style=\"background:var(--at-bg-subtle,#f8f9fa)\">");
        var feasColor = page.FeasibilityScore >= 7 ? "#10b981" : page.FeasibilityScore >= 5 ? "#f59e0b" : page.FeasibilityScore > 0 ? "#ef4444" : "#adb5bd";
        var feasPct = page.FeasibilityScore > 0 ? page.FeasibilityScore * 10 : 0;
        sb.AppendLine($"<span class=\"d-flex align-items-center gap-1\">Feasibility <div class=\"queue-bar\" style=\"width:50px;display:inline-block\"><div class=\"queue-bar-fill\" style=\"width:{feasPct}%;background:{feasColor}\"></div></div> <strong>{(page.FeasibilityScore > 0 ? $"{page.FeasibilityScore}/10" : "—")}</strong></span>");
        var sbColor = page.SiteBuildScore >= 7 ? "#10b981" : page.SiteBuildScore >= 5 ? "#f59e0b" : page.SiteBuildScore > 0 ? "#ef4444" : "#adb5bd";
        var sbPct = page.SiteBuildScore > 0 ? page.SiteBuildScore * 10 : 0;
        sb.AppendLine($"<span class=\"d-flex align-items-center gap-1\">Site Build <div class=\"queue-bar\" style=\"width:50px;display:inline-block\"><div class=\"queue-bar-fill\" style=\"width:{sbPct}%;background:{sbColor}\"></div></div> <strong>{(page.SiteBuildScore > 0 ? $"{page.SiteBuildScore}/10" : "—")}</strong></span>");
        if (!string.IsNullOrEmpty(page.EstimatedEffort))
            sb.AppendLine($"<span>Effort: <strong class=\"text-dark\">{HttpUtility.HtmlEncode(page.EstimatedEffort)}</strong></span>");
        if (!string.IsNullOrEmpty(page.EstimatedReward))
            sb.AppendLine($"<span>Reward: <strong class=\"text-dark\">{HttpUtility.HtmlEncode(page.EstimatedReward)}</strong></span>");
        sb.AppendLine("</div>");

        if (page.SiteBuildScore > 0 && !string.IsNullOrEmpty(page.SiteBuildReason))
        {
            var alertClass = page.SiteBuildScore >= 7 ? "alert-success" : page.SiteBuildScore >= 5 ? "alert-warning" : "alert-danger";
            sb.AppendLine($"<div class=\"alert {alertClass} py-2 px-3 small mb-2\"><i class=\"bi bi-building me-1\"></i><strong>Static Site Fit:</strong> {HttpUtility.HtmlEncode(page.SiteBuildReason)}</div>");
        }

        if (!string.IsNullOrEmpty(page.ExtractedSignals))
        {
            var signals = page.ExtractedSignals.Split("|||", StringSplitOptions.RemoveEmptyEntries);
            if (signals.Length > 0)
            {
                sb.AppendLine("<div class=\"small text-muted bg-light rounded px-3 py-2 mb-2\"><i class=\"bi bi-graph-up me-1\"></i><strong>Signals:</strong> ");
                sb.AppendLine(string.Join(", ", signals.Take(10).Select(s => HttpUtility.HtmlEncode(s))));
                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("<div class=\"d-flex gap-2 mt-2 flex-wrap align-items-center\">");

        if (page.Status == PageStatus.Analyzed)
        {
            sb.AppendLine($"<select class=\"form-select form-select-sm\" id=\"provider-{page.Id}\" style=\"width:auto;max-width:180px\" title=\"AI Provider override\"><option value=\"\">Default provider</option></select>");
            sb.AppendLine($"<button class=\"btn btn-sm btn-success\" data-bs-toggle=\"tooltip\" title=\"Build a Copilot prompt from this opportunity and start code generation\" onclick=\"postAction('/api/executor/implement/{page.Id}?method=promptFile&provider=' + encodeURIComponent(document.getElementById('provider-{page.Id}').value), this)\" data-label=\"Generate Prompt\"><i class=\"bi bi-file-text me-1\"></i>Generate Prompt</button>");
            sb.AppendLine($"<button class=\"btn btn-sm btn-primary\" data-bs-toggle=\"tooltip\" title=\"Open interactive code chat to build an app from this finding\" onclick=\"postAction('/api/executor/implement/{page.Id}?method=codeChatCli&provider=' + encodeURIComponent(document.getElementById('provider-{page.Id}').value), this)\" data-label=\"Code Chat\"><i class=\"bi bi-robot me-1\"></i>Code Chat</button>");
            sb.AppendLine($"<button class=\"btn btn-sm btn-warning\" data-bs-toggle=\"tooltip\" title=\"Run a second-pass feasibility study for a full build plan\" onclick=\"postAction('/api/hunter/pages/{page.Id}/deep-analyze?provider=' + encodeURIComponent(document.getElementById('provider-{page.Id}').value), this)\" data-label=\"Deep Analyze\"><i class=\"bi bi-stars me-1\"></i>Deep Analyze</button>");
        }
        else if (page.Status == PageStatus.AwaitingApproval)
        {
            sb.AppendLine("<span class=\"badge bg-info align-self-center\">Awaiting Approval</span>");
            sb.AppendLine($"<button class=\"btn btn-sm btn-success\" onclick=\"postAction('/api/executor/approve/{page.Id}', this)\" data-label=\"Approve\"><i class=\"bi bi-check-lg me-1\"></i>Approve &amp; Deploy</button>");
            sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"postAction('/api/executor/reject/{page.Id}', this)\" data-label=\"Reject\"><i class=\"bi bi-x-lg me-1\"></i>Reject</button>");
        }
        else if (page.Status == PageStatus.Implementing)
        {
            sb.AppendLine("<span class=\"badge bg-warning text-dark align-self-center\"><span class=\"spinner-border spinner-border-sm me-1\"></span>Implementing</span>");
        }
        else if (page.Status == PageStatus.Deployed)
        {
            sb.AppendLine("<span class=\"badge bg-success align-self-center\"><i class=\"bi bi-check-circle me-1\"></i>Deployed</span>");
            if (!string.IsNullOrEmpty(page.DeployedUrl))
                sb.AppendLine($"<a href=\"{HttpUtility.HtmlEncode(page.DeployedUrl)}\" target=\"_blank\" class=\"btn btn-sm btn-outline-success\"><i class=\"bi bi-box-arrow-up-right me-1\"></i>Visit Site</a>");
        }

        sb.AppendLine("</div>");

        // Close collapsible div
        sb.AppendLine("</div>");

        sb.AppendLine("</div></div>");
    }

    private static string RenderActionPlanSteps(string raw)
    {
        // Strip leading noise like `:` or `:[ ` that models sometimes emit
        var text = raw.TrimStart(':', ' ', '\t');

        // If it looks like a JSON array, try to extract step strings
        if (text.StartsWith('['))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<JsonElement>>(text);
                if (items is { Count: > 0 })
                {
                    var steps = items
                        .Select(e => e.ValueKind == JsonValueKind.String
                            ? e.GetString()
                            : e.TryGetProperty("action", out var a) ? a.GetString()
                            : e.TryGetProperty("step", out var s) ? s.GetString()
                            : e.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (steps.Count > 0)
                        return "<ol class=\"mb-0 ps-3 mt-1\">" + string.Join("", steps.Select(s => $"<li>{HttpUtility.HtmlEncode(s)}</li>")) + "</ol>";
                }
            }
            catch { }
        }

        // Multi-line: each line is a step
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            var items = lines
                .Select(l => Regex.Replace(l, @"^\d+\.\s*", ""))
                .Where(l => l.Length > 0)
                .ToList();
            return "<ol class=\"mb-0 ps-3 mt-1\">" + string.Join("", items.Select(s => $"<li>{HttpUtility.HtmlEncode(s)}</li>")) + "</ol>";
        }

        // Single line with inline numbering: "1. Step one. 2. Step two."
        var inlineSteps = Regex.Split(text, @"(?<=\s|^)\d+\.\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
        if (inlineSteps.Count > 1)
            return "<ol class=\"mb-0 ps-3 mt-1\">" + string.Join("", inlineSteps.Select(s => $"<li>{HttpUtility.HtmlEncode(s)}</li>")) + "</ol>";

        // Fallback: plain text with line breaks
        return HttpUtility.HtmlEncode(text).Replace("\n", "<br/>");
    }
}
