namespace ImagineWeb.Api;

public static class LayoutHelper
{
    public static string Wrap(string title, string bodyContent, string? activeNav = null, bool isAdmin = true)
    {
        var buildActive = activeNav is "Build from Idea" or "Build from Hunter" ? " active" : "";
        var exploreActive = activeNav is "Findings" or "Topics" ? " active" : "";
        var navbarBody = $"""
                <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#navMain">
                  <span class="navbar-toggler-icon"></span>
                </button>
                <div class="collapse navbar-collapse" id="navMain">
                  <ul class="navbar-nav ms-auto align-items-center">
                    {NavItem("/", "Dashboard", "bi-house", activeNav)}
                    <li class="nav-item dropdown">
                      <a class="nav-link dropdown-toggle{buildActive}" href="#" role="button" data-bs-toggle="dropdown">
                        <i class="bi bi-hammer me-1"></i>Build
                      </a>
                      <ul class="dropdown-menu">
                        <li><a class="dropdown-item{(activeNav == "Build from Idea" ? " active" : "")}" href="/clarify/idea"><i class="bi bi-lightbulb me-2"></i>From Idea</a></li>
                        <li><a class="dropdown-item{(activeNav == "Build from Hunter" ? " active" : "")}" href="/clarify/hunter"><i class="bi bi-crosshair me-2"></i>From Hunter</a></li>
                      </ul>
                    </li>
                    {NavItem("/projects", "Projects", "bi-collection", activeNav)}
                    <li class="nav-item dropdown">
                      <a class="nav-link dropdown-toggle{exploreActive}" href="#" role="button" data-bs-toggle="dropdown">
                        <i class="bi bi-binoculars me-1"></i>Explore
                      </a>
                      <ul class="dropdown-menu">
                        <li><a class="dropdown-item{(activeNav == "Findings" ? " active" : "")}" href="/api/hunter/report"><i class="bi bi-bar-chart me-2"></i>Findings</a></li>
                        <li><a class="dropdown-item{(activeNav == "Topics" ? " active" : "")}" href="/api/hunter/topics"><i class="bi bi-compass me-2"></i>Topics</a></li>
                      </ul>
                    </li>
                    {NavItem("/settings", "Settings", "bi-sliders", activeNav)}
                    <li class="nav-item ms-2">
                      <button class="theme-toggle" id="themeToggle" title="Toggle dark mode">
                        <i class="bi bi-moon-stars"></i>
                      </button>
                    </li>
                  </ul>
                </div>
                """;

        return $$"""
            <!DOCTYPE html>
            <html lang="en" data-bs-theme="light">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet">
            <link href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" rel="stylesheet">
            <script src="https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.min.js"></script>
            <title>{{title}} — ImagineWeb</title>
            <style>
            :root {
                --at-bg: #f0f2f5;
                --at-bg-subtle: rgba(255,255,255,0.6);
                --at-card-bg: rgba(255,255,255,0.75);
                --at-card-border: rgba(226,230,234,0.6);
                --at-card-shadow: 0 2px 12px rgba(0,0,0,0.04);
                --at-card-shadow-hover: 0 12px 32px rgba(0,0,0,0.10);
                --at-radius: 1rem;
                --at-radius-sm: 0.625rem;
                --at-transition: 0.25s cubic-bezier(.4,0,.2,1);
                --at-accent: #4f46e5;
                --at-accent-rgb: 79,70,229;
                --at-accent-soft: rgba(79,70,229,0.08);
                --at-accent-2: #7c3aed;
                --at-accent-3: #06b6d4;
                --at-gradient: linear-gradient(135deg, #4f46e5, #7c3aed, #06b6d4);
                --at-mesh: radial-gradient(at 20% 80%, rgba(79,70,229,0.06) 0%, transparent 50%),
                           radial-gradient(at 80% 20%, rgba(6,182,212,0.06) 0%, transparent 50%),
                           radial-gradient(at 50% 50%, rgba(124,58,237,0.04) 0%, transparent 50%);
            }
            [data-bs-theme="dark"] {
                --at-bg: #0f1117;
                --at-bg-subtle: rgba(22,24,32,0.7);
                --at-card-bg: rgba(30,33,42,0.75);
                --at-card-border: rgba(55,60,72,0.5);
                --at-card-shadow: 0 2px 12px rgba(0,0,0,0.25);
                --at-card-shadow-hover: 0 12px 32px rgba(0,0,0,0.4);
                --at-accent-soft: rgba(79,70,229,0.15);
                --at-mesh: radial-gradient(at 20% 80%, rgba(79,70,229,0.08) 0%, transparent 50%),
                           radial-gradient(at 80% 20%, rgba(6,182,212,0.08) 0%, transparent 50%),
                           radial-gradient(at 50% 50%, rgba(124,58,237,0.06) 0%, transparent 50%);
            }
            body { background: var(--at-bg); font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; min-height: 100vh; }
            #bgAurora { position: fixed; top: 0; left: 0; width: 100%; height: 100%; z-index: 0; pointer-events: none; overflow: hidden; }
            #bgAurora .orb { position: absolute; border-radius: 50%; filter: blur(80px); opacity: 0.35; mix-blend-mode: screen; will-change: transform; }
            [data-bs-theme="dark"] #bgAurora .orb { opacity: 0.25; }
            #bgAurora .orb-1 { width: 50vw; height: 50vw; max-width: 600px; max-height: 600px; background: radial-gradient(circle, rgba(79,70,229,0.6), transparent 70%); top: -10%; left: -5%; animation: auroraFloat1 18s ease-in-out infinite; }
            #bgAurora .orb-2 { width: 45vw; height: 45vw; max-width: 550px; max-height: 550px; background: radial-gradient(circle, rgba(124,58,237,0.5), transparent 70%); top: 50%; right: -10%; animation: auroraFloat2 22s ease-in-out infinite; }
            #bgAurora .orb-3 { width: 40vw; height: 40vw; max-width: 500px; max-height: 500px; background: radial-gradient(circle, rgba(6,182,212,0.5), transparent 70%); bottom: -15%; left: 30%; animation: auroraFloat3 20s ease-in-out infinite; }
            #bgAurora .orb-4 { width: 30vw; height: 30vw; max-width: 400px; max-height: 400px; background: radial-gradient(circle, rgba(79,70,229,0.3), transparent 70%); top: 30%; left: 50%; animation: auroraFloat4 25s ease-in-out infinite; }
            @keyframes auroraFloat1 { 0%,100% { transform: translate(0,0) scale(1); } 33% { transform: translate(8vw,12vh) scale(1.15); } 66% { transform: translate(-3vw,6vh) scale(0.95); } }
            @keyframes auroraFloat2 { 0%,100% { transform: translate(0,0) scale(1); } 33% { transform: translate(-10vw,-8vh) scale(1.1); } 66% { transform: translate(5vw,-15vh) scale(0.9); } }
            @keyframes auroraFloat3 { 0%,100% { transform: translate(0,0) scale(1); } 33% { transform: translate(12vw,-10vh) scale(1.2); } 66% { transform: translate(-8vw,5vh) scale(0.85); } }
            @keyframes auroraFloat4 { 0%,100% { transform: translate(0,0) scale(0.9); } 50% { transform: translate(-12vw,10vh) scale(1.15); } }
            #bg3d { position: fixed; top: 0; left: 0; width: 100%; height: 100%; z-index: 1; pointer-events: none; }
            .navbar { background: var(--at-card-bg); backdrop-filter: blur(16px) saturate(180%); -webkit-backdrop-filter: blur(16px) saturate(180%); border-bottom: 1px solid var(--at-card-border); position: relative; z-index: 20; }
            .navbar::after { content: ''; position: absolute; bottom: 0; left: 0; right: 0; height: 2px; background: var(--at-gradient); opacity: 0.7; }
            .navbar-brand { font-weight: 700; letter-spacing: -0.3px; background: var(--at-gradient); -webkit-background-clip: text; -webkit-text-fill-color: transparent; background-clip: text; }
            .navbar .nav-link { font-size: 0.875rem; padding-bottom: 0.6rem !important; transition: color var(--at-transition); position: relative; }
            .navbar .nav-link:hover, .navbar .nav-link.active { color: var(--at-accent) !important; }
            .navbar .nav-link.active { font-weight: 600; }
            .navbar .nav-link.active::after { content: ''; position: absolute; bottom: 0; left: 50%; transform: translateX(-50%); width: 20px; height: 2px; background: var(--at-gradient); border-radius: 2px; }
            .navbar .dropdown-menu { background: var(--at-card-bg); backdrop-filter: blur(16px); border: 1px solid var(--at-card-border); border-radius: var(--at-radius-sm); box-shadow: var(--at-card-shadow-hover); }
            .navbar .dropdown-item { font-size: 0.875rem; padding: .5rem 1rem; border-radius: var(--at-radius-sm); margin: 0 .25rem; }
            .navbar .dropdown-item:hover { background: var(--at-accent-soft); }
            .navbar .dropdown-item.active, .navbar .dropdown-item:active { background: var(--at-accent); color: #fff; }
            .page-header { margin-bottom: 1.5rem; }
            .page-header h1 { font-size: 1.5rem; font-weight: 700; margin-bottom: 0.25rem; }
            .page-header p { color: #6c757d; font-size: 0.875rem; margin-bottom: 0; }
            .credits-chip { display: inline-flex; align-items: center; padding: 0.3rem 0.7rem; border-radius: 999px; font-size: 0.78rem; font-weight: 700; text-decoration: none; background: linear-gradient(135deg, rgba(79,70,229,0.12), rgba(6,182,212,0.12)); color: var(--at-accent); border: 1px solid rgba(79,70,229,0.25); transition: all var(--at-transition); }
            .credits-chip:hover { background: var(--at-gradient); color: #fff; border-color: transparent; box-shadow: 0 4px 12px rgba(79,70,229,0.35); }
            .credits-chip.is-low { background: rgba(220,38,38,0.12); color: #dc2626; border-color: rgba(220,38,38,0.3); }
            .credits-chip.is-low:hover { background: #dc2626; color: #fff; }
            .card { background: var(--at-card-bg); backdrop-filter: blur(12px) saturate(160%); -webkit-backdrop-filter: blur(12px) saturate(160%); border: 1px solid var(--at-card-border); border-radius: var(--at-radius); box-shadow: var(--at-card-shadow); transition: box-shadow var(--at-transition), transform var(--at-transition), border-color var(--at-transition); }
            .card-hover:hover, .card:hover { box-shadow: var(--at-card-shadow-hover); }
            .stat-card { text-align: center; padding: 1.25rem 1rem; position: relative; overflow: hidden; }
            .stat-card::before { content: ''; position: absolute; top: 0; left: 0; right: 0; height: 3px; border-radius: var(--at-radius) var(--at-radius) 0 0; pointer-events: none; }
            .stat-card::after { content: ''; position: absolute; top: 0; right: 0; width: 80px; height: 80px; border-radius: 50%; opacity: 0.06; transform: translate(20px, -20px); pointer-events: none; }
            .stat-card .stat-icon { font-size: 1.5rem; margin-bottom: 0.5rem; opacity: 0.8; }
            .stat-card .stat-value { font-size: 1.75rem; font-weight: 700; line-height: 1.2; }
            .stat-card .stat-label { font-size: 0.7rem; color: #6c757d; text-transform: uppercase; letter-spacing: 0.6px; margin-top: 0.25rem; }
            .stat-info-icon { position: absolute; top: 0.5rem; right: 0.5rem; font-size: 0.85rem; color: #adb5bd; cursor: help; opacity: 0.65; transition: opacity var(--at-transition); z-index: 5; padding: 2px; }
            .stat-info-icon:hover { opacity: 1; color: var(--at-accent); }
            .stat-card-green::before { background: linear-gradient(90deg, #10b981, #34d399); }
            .stat-card-green::after { background: #10b981; }
            .stat-card-green .stat-icon { color: #10b981; }
            .stat-card-blue::before { background: var(--at-gradient); }
            .stat-card-blue::after { background: var(--at-accent); }
            .stat-card-blue .stat-icon { color: var(--at-accent); }
            .stat-card-purple::before { background: linear-gradient(90deg, #7c3aed, #a78bfa); }
            .stat-card-purple::after { background: #7c3aed; }
            .stat-card-purple .stat-icon { color: #7c3aed; }
            .stat-card-amber::before { background: linear-gradient(90deg, #f59e0b, #fbbf24); }
            .stat-card-amber::after { background: #f59e0b; }
            .stat-card-amber .stat-icon { color: #f59e0b; }
            .stat-card-teal::before { background: linear-gradient(90deg, #06b6d4, #22d3ee); }
            .stat-card-teal::after { background: #06b6d4; }
            .stat-card-teal .stat-icon { color: #06b6d4; }
            .badge-score-high { background: linear-gradient(135deg, #10b981, #059669); }
            .badge-score-mid { background: linear-gradient(135deg, #f59e0b, #d97706); color: #fff; }
            .badge-score-low { background: linear-gradient(135deg, #ef4444, #dc2626); }
            .toast-container { position: fixed; top: 1rem; right: 1rem; z-index: 9999; }
            .content-section { margin-bottom: 1.5rem; }
            .section-title { font-size: 1rem; font-weight: 600; color: #495057; margin-bottom: 0.75rem; padding-bottom: 0.5rem; border-bottom: 1px solid var(--at-card-border); }
            .empty-state { color: #6c757d; font-style: italic; padding: 1rem 0; }
            footer { color: #adb5bd; font-size: 0.8rem; text-align: center; padding: 1.5rem 0; border-top: 1px solid var(--at-card-border); margin-top: 2rem; }
            .fade-in { animation: fadeIn 0.4s cubic-bezier(.4,0,.2,1) both; }
            @keyframes fadeIn { from { opacity: 0; transform: translateY(12px); } to { opacity: 1; transform: translateY(0); } }
            .stagger-1 { animation-delay: 0.05s; }
            .stagger-2 { animation-delay: 0.1s; }
            .stagger-3 { animation-delay: 0.15s; }
            .stagger-4 { animation-delay: 0.2s; }
            .stagger-5 { animation-delay: 0.25s; }
            .stagger-6 { animation-delay: 0.3s; }
            .skeleton { background: linear-gradient(90deg, #e9ecef 25%, #f8f9fa 50%, #e9ecef 75%); background-size: 200% 100%; animation: shimmer 1.5s infinite; border-radius: var(--at-radius-sm); }
            [data-bs-theme="dark"] .skeleton { background: linear-gradient(90deg, #2d3238 25%, #3a4048 50%, #2d3238 75%); background-size: 200% 100%; }
            @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
            .skeleton-text { height: .875rem; margin-bottom: .5rem; }
            .skeleton-title { height: 1.25rem; width: 60%; margin-bottom: .75rem; }
            .skeleton-card { padding: 1.25rem; }
            .theme-toggle { background: var(--at-card-bg); backdrop-filter: blur(8px); border: 1px solid var(--at-card-border); border-radius: 50%; width: 2rem; height: 2rem; display: flex; align-items: center; justify-content: center; cursor: pointer; transition: all var(--at-transition); font-size: 0.9rem; }
            .theme-toggle:hover { background: var(--at-accent-soft); border-color: var(--at-accent); transform: rotate(15deg); }
            .quick-link { text-decoration: none; transition: all var(--at-transition); }
            .quick-link:hover { box-shadow: var(--at-card-shadow-hover); transform: translateY(-4px); border-color: rgba(var(--at-accent-rgb), 0.3); }
            .controls-card { border-left: 3px solid transparent; border-image: var(--at-gradient) 1; }
            .score-ring { width: 48px; height: 48px; position: relative; }
            .score-ring svg { transform: rotate(-90deg); }
            .score-ring .ring-bg { stroke: var(--at-card-border); }
            .score-ring .ring-fg { transition: stroke-dashoffset 0.8s cubic-bezier(.4,0,.2,1); }
            .score-ring .ring-value { position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); font-size: 0.8rem; font-weight: 700; }
            .queue-bar { height: 6px; border-radius: 3px; background: var(--at-card-border); overflow: hidden; }
            .queue-bar-fill { height: 100%; border-radius: 3px; background: var(--at-gradient); transition: width 0.5s cubic-bezier(.4,0,.2,1); }
            .pulse-dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; animation: pulse 2s infinite; }
            @keyframes pulse { 0%, 100% { opacity: 1; transform: scale(1); } 50% { opacity: 0.5; transform: scale(0.8); } }
            .glow-card { position: relative; }
            .glow-card::before { content: ''; position: absolute; inset: -1px; border-radius: var(--at-radius); background: var(--at-gradient); opacity: 0; transition: opacity var(--at-transition); z-index: -1; }
            .glow-card:hover::before { opacity: 0.15; }
            </style>
            </head>
            <body>
            <div id="bgAurora">
                <div class="orb orb-1"></div>
                <div class="orb orb-2"></div>
                <div class="orb orb-3"></div>
                <div class="orb orb-4"></div>
            </div>
            <canvas id="bg3d"></canvas>
            <script>window.__aitk_isAdmin = {{(isAdmin ? "true" : "false")}};</script>
            <nav class="navbar navbar-expand-lg sticky-top">
              <div class="container">
                <a class="navbar-brand" href="/"><i class="bi bi-graph-up-arrow me-2" style="background:var(--at-gradient);-webkit-background-clip:text;-webkit-text-fill-color:transparent"></i>ImagineWeb</a>
                {{navbarBody}}
              </div>
            </nav>
            <div class="container py-4" style="position:relative;z-index:10">
            {{bodyContent}}
            <footer>ImagineWeb &mdash; Opportunity Discovery Platform
            </footer>
            </div>
            <div class="toast-container" id="toastContainer"></div>
            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js"></script>
            <script>
            document.addEventListener('DOMContentLoaded', function() {
                document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function(el) {
                    new bootstrap.Tooltip(el);
                });
                const saved = localStorage.getItem('at-theme');
                if (saved === 'dark' || (!saved && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
                    document.documentElement.setAttribute('data-bs-theme', 'dark');
                }
                updateThemeIcon();
            });

            document.getElementById('themeToggle').addEventListener('click', function() {
                const current = document.documentElement.getAttribute('data-bs-theme');
                const next = current === 'dark' ? 'light' : 'dark';
                document.documentElement.setAttribute('data-bs-theme', next);
                localStorage.setItem('at-theme', next);
                updateThemeIcon();
            });

            function updateThemeIcon() {
                const icon = document.querySelector('#themeToggle i');
                const isDark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
                icon.className = isDark ? 'bi bi-sun' : 'bi bi-moon-stars';
            }

            function showToast(message, type) {
                type = type || 'info';
                const icons = { success: 'bi-check-circle-fill', danger: 'bi-x-circle-fill', warning: 'bi-exclamation-triangle-fill', info: 'bi-info-circle-fill' };
                const colors = { success: 'bg-success text-white', danger: 'bg-danger text-white', warning: 'bg-warning text-dark', info: 'bg-primary text-white' };
                const cls = colors[type] || colors.info;
                const ico = icons[type] || icons.info;
                const closeClass = type === 'warning' ? 'btn-close' : 'btn-close btn-close-white';
                const el = document.createElement('div');
                el.className = 'toast align-items-center border-0 ' + cls;
                el.setAttribute('role', 'alert');
                el.innerHTML = '<div class="d-flex"><div class="toast-body"><i class="bi ' + ico + ' me-2"></i>' + message + '</div><button type="button" class="' + closeClass + ' me-2 m-auto" data-bs-dismiss="toast"></button></div>';
                document.getElementById('toastContainer').appendChild(el);
                new bootstrap.Toast(el, { delay: 4000 }).show();
                el.addEventListener('hidden.bs.toast', function() { el.remove(); });
            }
            async function postAction(url, btn) {
                if (btn) { btn.disabled = true; btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Working...'; }
                try {
                    const res = await fetch(url, { method: 'POST' });
                    const data = await res.json();
                    if (res.ok) { showToast(data.message || 'Done', 'success'); setTimeout(function() { location.reload(); }, 1200); }
                    else { showToast(data.error || 'Request failed', 'danger'); if (btn) { btn.disabled = false; btn.innerHTML = btn.dataset.label || 'Retry'; } }
                } catch(e) { showToast('Network error: ' + e.message, 'danger'); if (btn) { btn.disabled = false; } }
            }
            async function deleteAction(url, confirmMsg) {
                if (!confirm(confirmMsg)) return;
                try {
                    const res = await fetch(url, { method: 'DELETE' });
                    const data = await res.json();
                    showToast(data.message || data.error || 'Done', res.ok ? 'success' : 'danger');
                    if (res.ok) setTimeout(function() { location.reload(); }, 1200);
                } catch(e) { showToast('Error: ' + e.message, 'danger'); }
            }
            function animateCounter(el, target, duration) {
                duration = duration || 800;
                var start = parseInt(el.textContent) || 0;
                if (start === target) return;
                var startTime = null;
                function step(ts) {
                    if (!startTime) startTime = ts;
                    var progress = Math.min((ts - startTime) / duration, 1);
                    var eased = 1 - Math.pow(1 - progress, 3);
                    el.textContent = Math.round(start + (target - start) * eased);
                    if (progress < 1) requestAnimationFrame(step);
                }
                requestAnimationFrame(step);
            }
            function buildScoreRing(score, max, color, size) {
                size = size || 48;
                var r = (size - 6) / 2;
                var circ = 2 * Math.PI * r;
                var pct = Math.min(score / max, 1);
                var offset = circ * (1 - pct);
                return '<div class="score-ring" style="width:' + size + 'px;height:' + size + 'px">' +
                    '<svg width="' + size + '" height="' + size + '"><circle class="ring-bg" cx="' + size/2 + '" cy="' + size/2 + '" r="' + r + '" fill="none" stroke-width="4"/>' +
                    '<circle class="ring-fg" cx="' + size/2 + '" cy="' + size/2 + '" r="' + r + '" fill="none" stroke="' + color + '" stroke-width="4" stroke-linecap="round" stroke-dasharray="' + circ + '" stroke-dashoffset="' + offset + '"/></svg>' +
                    '<span class="ring-value">' + score + '</span></div>';
            }

            window.authEventSource = function(url) { return new EventSource(url); };

            // ── Unified LLM Provider Selector (shared across clarify/idea/projects/hunter pages) ──
            window.LlmProviderUI = (function() {
                let _cache = null;
                async function load(force) {
                    if (_cache && !force) return _cache;
                    const res = await fetch('/api/settings/llm-providers');
                    _cache = await res.json();
                    return _cache;
                }
                function placeholderFor(providerKey) {
                    const k = (providerKey || '').toLowerCase();
                    if (k === 'openai') return 'gpt-4o-mini, gpt-4.1, o1-mini, or OpenRouter slug';
                    if (k === 'anthropic') return 'claude-sonnet-4-5, claude-opus-4-5, claude-haiku-4-5';
                    if (k === 'ollama') return 'gpt-oss:20b, llama3.1:70b, qwen2.5:32b';
                    if (k === 'copilotsdk') return 'gpt-5-mini, claude-opus-4.6';
                    return 'model id';
                }
                /**
                 * Mounts a provider+model pair UI into the given container element.
                 * options: { containerId, kind: 'analysis'|'codegen', label, helpText, defaultProvider, showReasoning, defaultReasoning }
                 * Returns: { getProvider(), getModel(), getReasoning(), refresh() }
                 */
                async function mount(opts) {
                    const container = typeof opts.containerId === 'string'
                        ? document.getElementById(opts.containerId) : opts.containerId;
                    if (!container) return null;
                    const data = await load();
                    const filterCodegen = opts.kind === 'codegen';
                    const providers = data.providers.filter(p => filterCodegen ? p.supportsCodegen : true);
                    const def = opts.defaultProvider
                        || (filterCodegen ? data.defaultCodegenProvider : data.defaultProvider);
                    const label = opts.label || 'AI Provider';
                    const helpText = opts.helpText || 'Override the global default for this run only.';
                    const showReasoning = opts.showReasoning !== false;
                    const defaultReasoning = opts.defaultReasoning || 'medium';
                    const id = 'llmsel-' + Math.random().toString(36).slice(2, 8);
                    const providerOpts = providers.map(p => {
                        const sel = (p.key.toLowerCase() === (def || '').toLowerCase()) ? 'selected' : '';
                        const flag = p.configured ? '' : ' (not configured)';
                        return '<option value="' + p.key + '" ' + sel + '>' + p.label + flag + '</option>';
                    }).join('');
                    const reasoningHtml = showReasoning
                        ? '<div class="col-md-3"><select id="' + id + '-reasoning" class="form-select form-select-sm" title="Reasoning effort">' +
                          '<option value="low"' + (defaultReasoning === 'low' ? ' selected' : '') + '>Low</option>' +
                          '<option value="medium"' + (defaultReasoning === 'medium' ? ' selected' : '') + '>Medium</option>' +
                          '<option value="high"' + (defaultReasoning === 'high' ? ' selected' : '') + '>High</option>' +
                          '</select></div>' : '';
                    const modelCol = showReasoning ? 'col-md-4' : 'col-md-7';
                    container.innerHTML =
                        '<div class="card border-0 bg-light-subtle mb-3"><div class="card-body py-2 px-3">' +
                        '<div class="d-flex align-items-center mb-2"><i class="bi bi-cpu me-2 text-primary"></i>' +
                        '<strong class="small">' + label + '</strong></div>' +
                        '<div class="row g-2">' +
                        '<div class="col-md-5"><select id="' + id + '-provider" class="form-select form-select-sm">' +
                        providerOpts + '</select></div>' +
                        '<div class="' + modelCol + '"><input id="' + id + '-model" type="text" class="form-control form-control-sm" placeholder="" /></div>' +
                        reasoningHtml +
                        '</div>' +
                        '<div class="form-text small mt-1">' + helpText + ' Leave model blank to use the provider default.</div>' +
                        '</div></div>';
                    const providerSel = document.getElementById(id + '-provider');
                    const modelInp = document.getElementById(id + '-model');
                    const reasoningSel = showReasoning ? document.getElementById(id + '-reasoning') : null;
                    function updatePlaceholder() {
                        const p = data.providers.find(x => x.key === providerSel.value);
                        modelInp.placeholder = (p && p.defaultModel) ? ('default: ' + p.defaultModel) : placeholderFor(providerSel.value);
                    }
                    providerSel.addEventListener('change', updatePlaceholder);
                    updatePlaceholder();
                    return {
                        getProvider: () => providerSel.value || null,
                        getModel: () => (modelInp.value || '').trim() || null,
                        getReasoning: () => reasoningSel ? reasoningSel.value : null,
                        setProvider: (v) => { providerSel.value = v; updatePlaceholder(); },
                        setModel: (v) => { modelInp.value = v || ''; },
                        setReasoning: (v) => { if (reasoningSel && v) reasoningSel.value = v; },
                        refresh: async () => { _cache = null; await load(true); }
                    };
                }
                return { load, mount };
            })();

            // ── 3D Neural Network Background ──
            (function() {
                if (typeof THREE === 'undefined') return;
                var canvas = document.getElementById('bg3d');
                if (!canvas) return;
                var scene = new THREE.Scene();
                var camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 1, 1000);
                camera.position.z = 300;
                var renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true, canvas: canvas });
                renderer.setSize(window.innerWidth, window.innerHeight);
                renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
                renderer.setClearColor(0x000000, 0);
                var count = window.innerWidth < 768 ? 60 : 120;
                var connDist = 150;
                var positions = new Float32Array(count * 3);
                var velocities = new Float32Array(count * 3);
                var pColors = new Float32Array(count * 3);
                var palette = [[79/255,70/255,229/255],[124/255,58/255,237/255],[6/255,182/255,212/255]];
                for (var i = 0; i < count; i++) {
                    var i3 = i * 3;
                    positions[i3] = (Math.random() - 0.5) * 600;
                    positions[i3+1] = (Math.random() - 0.5) * 400;
                    positions[i3+2] = (Math.random() - 0.5) * 200;
                    velocities[i3] = (Math.random() - 0.5) * 0.25;
                    velocities[i3+1] = (Math.random() - 0.5) * 0.25;
                    velocities[i3+2] = (Math.random() - 0.5) * 0.08;
                    var c = palette[i % 3];
                    pColors[i3] = c[0]; pColors[i3+1] = c[1]; pColors[i3+2] = c[2];
                }
                var ptGeom = new THREE.BufferGeometry();
                ptGeom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                ptGeom.setAttribute('color', new THREE.BufferAttribute(pColors, 3));
                var ptMat = new THREE.PointsMaterial({ size: 5, vertexColors: true, transparent: true, opacity: 0.7, sizeAttenuation: true, depthWrite: false });
                scene.add(new THREE.Points(ptGeom, ptMat));
                var maxLines = count * 5;
                var linePos = new Float32Array(maxLines * 6);
                var lineGeom = new THREE.BufferGeometry();
                lineGeom.setAttribute('position', new THREE.BufferAttribute(linePos, 3));
                var lineMat = new THREE.LineBasicMaterial({ color: 0x6366f1, transparent: true, opacity: 0.18, depthWrite: false });
                var lineSegs = new THREE.LineSegments(lineGeom, lineMat);
                scene.add(lineSegs);
                var mouseX = 0, mouseY = 0;
                document.addEventListener('mousemove', function(e) {
                    mouseX = (e.clientX / window.innerWidth - 0.5) * 2;
                    mouseY = (e.clientY / window.innerHeight - 0.5) * 2;
                });
                function isDark() { return document.documentElement.getAttribute('data-bs-theme') === 'dark'; }
                function applyThemeOpacity() {
                    var d = isDark();
                    ptMat.opacity = d ? 0.85 : 0.7;
                    lineMat.opacity = d ? 0.22 : 0.15;
                }
                applyThemeOpacity();
                new MutationObserver(applyThemeOpacity).observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });
                var frame = 0;
                function animate() {
                    requestAnimationFrame(animate);
                    frame++;
                    for (var i = 0; i < count; i++) {
                        var i3 = i * 3;
                        positions[i3] += velocities[i3];
                        positions[i3+1] += velocities[i3+1];
                        positions[i3+2] += velocities[i3+2];
                        if (Math.abs(positions[i3]) > 300) velocities[i3] *= -1;
                        if (Math.abs(positions[i3+1]) > 200) velocities[i3+1] *= -1;
                        if (Math.abs(positions[i3+2]) > 100) velocities[i3+2] *= -1;
                    }
                    ptGeom.attributes.position.needsUpdate = true;
                    if (frame % 3 === 0) {
                        var li = 0, cd2 = connDist * connDist;
                        for (var a = 0; a < count && li < maxLines; a++) {
                            for (var b = a + 1; b < count && li < maxLines; b++) {
                                var dx = positions[a*3] - positions[b*3], dy = positions[a*3+1] - positions[b*3+1], dz = positions[a*3+2] - positions[b*3+2];
                                if (dx*dx + dy*dy + dz*dz < cd2) {
                                    var o = li * 6;
                                    linePos[o]=positions[a*3]; linePos[o+1]=positions[a*3+1]; linePos[o+2]=positions[a*3+2];
                                    linePos[o+3]=positions[b*3]; linePos[o+4]=positions[b*3+1]; linePos[o+5]=positions[b*3+2];
                                    li++;
                                }
                            }
                        }
                        for (var x = li * 6; x < linePos.length; x++) linePos[x] = 0;
                        lineGeom.attributes.position.needsUpdate = true;
                        lineGeom.setDrawRange(0, li * 2);
                    }
                    camera.position.x += (mouseX * 25 - camera.position.x) * 0.015;
                    camera.position.y += (-mouseY * 18 - camera.position.y) * 0.015;
                    camera.lookAt(0, 0, 0);
                    renderer.render(scene, camera);
                }
                animate();
                window.addEventListener('resize', function() {
                    camera.aspect = window.innerWidth / window.innerHeight;
                    camera.updateProjectionMatrix();
                    renderer.setSize(window.innerWidth, window.innerHeight);
                });
            })();
            </script>
            </body>
            </html>
            """;
    }

    private static string NavItem(string href, string label, string icon, string? activeNav)
    {
        var active = string.Equals(activeNav, label, StringComparison.OrdinalIgnoreCase) ? " active" : "";
        return $"<li class=\"nav-item\"><a class=\"nav-link{active}\" href=\"{href}\"><i class=\"bi {icon} me-1\"></i>{label}</a></li>";
    }
}
