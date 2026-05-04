# ImagineWeb

AI-powered platform that discovers web opportunities, generates complete websites from ideas or research, and deploys them — all through a local web UI.

## What It Does

```
Search → Scrape → AI Analysis → Code Generation → Deployment
                                      ↑
              User Ideas / Conversations ─┘
```

**Three core workflows:**

1. **Hunter** — Autonomous pipeline that searches the web, scrapes pages, and uses AI to score them for value/profit potential. Discovers niches and opportunities automatically.
2. **Build from Idea** — Describe a website idea in natural language. AI asks clarifying questions, then generates and deploys a complete site.
3. **Build from Hunter** — Pick a high-scoring page from Hunter results and generate a competitive site targeting that niche.

## Features

- **Multi-provider AI** — Ollama (local/free), GitHub Copilot SDK, OpenAI, Anthropic Claude
- **Autonomous web research** — Searches, scrapes, scores pages 1-10, follows high-value links
- **AI code generation** — Produces complete HTML/CSS/JS websites from specifications
- **One-click deployment** — GitHub Pages, Azure Static Web Apps, Azure App Service
- **Conversational refinement** — AI asks questions before building to ensure the result matches your vision
- **Iterative improvement** — Refine generated sites through follow-up instructions
- **Self-hosted search** — Uses SearXNG meta-search engine (aggregates Google, Bing, DuckDuckGo)
- **Project management** — Track all generated sites, redeploy, tear down, archive

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- At least one AI provider:
  - [Ollama](https://ollama.ai/) running locally (free, no API key needed)
  - OR [GitHub Copilot](https://github.com/features/copilot) subscription
  - OR OpenAI / Anthropic API key
- [SearXNG](https://docs.searxng.org/) for web search (Hunter pipeline) — see [SearXNG Setup](#searxng-setup) below
- [GitHub CLI](https://cli.github.com/) (`gh`) for GitHub Pages deployment (optional)

## Quick Start

```bash
git clone https://github.com/orifn1/ImagineWeb.git
cd ImagineWeb

# Copy the example config and fill in your settings
cp src/ImagineWeb.Api/appsettings.json.example src/ImagineWeb.Api/appsettings.json

# Build and run
dotnet run --project src/ImagineWeb.Api
```

Open `http://localhost:5555` in your browser.

## Configuration

All settings are configurable through the **Settings** page (`http://localhost:5555/settings`) in the web UI. You can also edit `src/ImagineWeb.Api/appsettings.json` directly.

Settings you change via the UI are saved to a local SQLite database (`hunter.db`) and override values from `appsettings.json`. The database file is created automatically on first run — you never need to touch it directly.

### AI Provider Setup (choose at least one)

| Provider | What you need | Cost |
|----------|--------------|------|
| **Ollama** | Install Ollama, pull a model (`ollama pull qwen3:14b`) | Free (runs locally) |
| **GitHub Copilot SDK** | Active Copilot subscription + GitHub token | Copilot subscription |
| **OpenAI** | API key from https://platform.openai.com | Pay-per-use |
| **Anthropic** | API key from https://console.anthropic.com | Pay-per-use |

### Getting a GitHub Token (for Copilot SDK)

The GitHub Copilot SDK needs a personal access token to authenticate. To get one:

1. Install [GitHub CLI](https://cli.github.com/) and log in:
   ```bash
   gh auth login
   ```
2. Copy your token:
   ```bash
   gh auth token
   ```
3. Paste the token into the Settings page under **Copilot SDK (Analysis) → GitHubToken** and/or **Code Generator → GitHubToken**.

If you don't provide a token, the app will open a browser window asking you to log in each time it needs to authenticate.

### Settings Sections

| Section | Purpose |
|---------|---------|
| **AI Provider & Routing** | Choose which AI handles page analysis. Supports separate providers for quick scan vs deep analysis phases. |
| **Ollama** | Connection to local Ollama server — URL, model name, context window size. |
| **Copilot SDK (Analysis)** | GitHub Copilot for analysis — model selection, concurrency, GitHub token. |
| **Code Generator** | Which AI builds websites. Supports Copilot SDK, VS Code CLI, Ollama, OpenAI, Anthropic. |
| **OpenAI / OpenAI-compatible** | API key, model, and base URL. Works with OpenRouter, DeepSeek, Groq via custom endpoint. |
| **Anthropic Claude** | API key and model selection for Claude models. |
| **Hunter Pipeline** | Controls for the autonomous search→scrape→analyze loop: parallelism, scoring thresholds, session limits. |
| **Seed Topics** | Initial search queries that bootstrap the Hunter pipeline. AI generates new topics as it discovers content. |
| **Executor** | GitHub username used when deploying generated sites to GitHub Pages. |
| **Azure Deployment** | Service principal credentials for deploying generated apps to Azure (optional). |
| **Azure DevOps** | CI/CD pipeline integration for automated builds (optional). |
| **Search Engines** | SearXNG meta-search engine endpoint and installation path. |

### Minimal Setup (Ollama — free, no API keys)

1. Install [Ollama](https://ollama.ai/) and pull a model:
   ```bash
   ollama pull qwen3:14b
   ollama serve
   ```
2. Copy `appsettings.json.example` to `appsettings.json`.
3. Run the app — Ollama is the default provider, everything works out of the box.

### Minimal Setup (GitHub Copilot)

1. Copy `appsettings.json.example` to `appsettings.json`.
2. Open Settings page → set **AI Provider & Routing → Provider** to `CopilotSdk`.
3. In **Copilot SDK (Analysis)** section, paste your GitHub token (see [Getting a GitHub Token](#getting-a-github-token-for-copilot-sdk)).
4. In **Code Generator** section, set Provider to `copilotSdk` and paste the same token.

## SearXNG Setup

SearXNG is a free, self-hosted meta-search engine that aggregates results from Google, Bing, DuckDuckGo, and many others. The Hunter pipeline uses it for web search. Without SearXNG, the Hunter pipeline cannot find pages to analyze.

The app can start/stop SearXNG automatically from the dashboard — you just need to install it once.

### Windows (via WSL)

Windows users run SearXNG inside Windows Subsystem for Linux (WSL):

1. **Install WSL** (if not already installed):
   ```powershell
   wsl --install -d Ubuntu
   ```
   Restart your computer if prompted. Once Ubuntu is installed, open it and create a user.

2. **Install SearXNG inside WSL** — open your Ubuntu terminal:
   ```bash
   sudo apt update && sudo apt install -y python3 python3-venv git

   cd ~
   git clone https://github.com/searxng/searxng.git
   cd searxng
   python3 -m venv venv
   source venv/bin/activate
   pip install -e .
   ```

3. **Configure SearXNG** — edit `~/searxng/searx/settings.yml`:
   ```bash
   nano ~/searxng/searx/settings.yml
   ```
   Find the `server:` section and set:
   ```yaml
   server:
     bind_address: "0.0.0.0"
     port: 8888
     secret_key: "any-random-string-here"
   ```
   Also find the `search:` section and set:
   ```yaml
   search:
     formats:
       - html
       - json
   ```
   Save and exit (Ctrl+X, Y, Enter).

4. **Configure the app** — in your `appsettings.json`, set:
   ```json
   "Search": {
     "SearXngBaseUrl": "http://localhost:8888",
     "SearxngPath": "/home/YOUR_WSL_USERNAME/searxng",
     "SearxngWslDistro": "Ubuntu"
   }
   ```
   Replace `YOUR_WSL_USERNAME` with your WSL username (run `whoami` inside WSL to check).

5. **Test it** — the app will start SearXNG automatically when you launch the Hunter pipeline, or you can start it manually from the dashboard.

### Linux / macOS

1. **Install SearXNG**:
   ```bash
   sudo apt update && sudo apt install -y python3 python3-venv git  # Debian/Ubuntu
   # or: brew install python git  # macOS

   cd ~
   git clone https://github.com/searxng/searxng.git
   cd searxng
   python3 -m venv venv
   source venv/bin/activate
   pip install -e .
   ```

2. **Configure SearXNG** — edit `~/searxng/searx/settings.yml`:
   Set `server.bind_address` to `"0.0.0.0"`, `server.port` to `8888`, add a `server.secret_key`, and add `json` to `search.formats` (same as the Windows instructions above).

3. **Configure the app** — in your `appsettings.json`:
   ```json
   "Search": {
     "SearXngBaseUrl": "http://localhost:8888",
     "SearxngPath": "/home/YOUR_USERNAME/searxng",
     "SearxngWslDistro": ""
   }
   ```
   Leave `SearxngWslDistro` empty on Linux/macOS.

### Running SearXNG Manually (optional)

If you prefer to start SearXNG yourself instead of letting the app manage it:

```bash
cd ~/searxng
source venv/bin/activate
python searx/webapp.py
```

As long as SearXNG is reachable at the URL specified in `SearXngBaseUrl`, the app will use it regardless of how it was started.

## Playwright (for page screenshots)

The app uses Playwright to capture screenshots of generated sites. On first run, install the browsers:

```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

This is optional — the app works without it, but you won't see site previews in the UI.

## Project Structure

```
src/
├── ImagineWeb.Api/            # ASP.NET Core web app (controllers, pages, startup)
├── ImagineWeb.Core/           # Business logic, interfaces, domain models
└── ImagineWeb.Infrastructure/ # External integrations (AI clients, search, scraping, deployment)
```

## Pages

| URL | Purpose |
|-----|---------|
| `/` | Dashboard — overview and quick actions |
| `/idea` | Start a new site from an idea (conversational AI flow) |
| `/projects` | Manage generated solutions — deploy, improve, delete |
| `/settings` | Configure AI providers, pipeline parameters, deployment targets |

## Technology Stack

- **Runtime:** .NET 10 / ASP.NET Core
- **Database:** SQLite (via EF Core) — stores settings, discovered pages, and analysis results
- **Frontend:** Server-rendered HTML with Bootstrap 5 + vanilla JS
- **AI:** Multi-provider abstraction (Ollama, Copilot SDK, OpenAI, Anthropic)
- **Search:** SearXNG meta-search with multi-engine fallback
- **Scraping:** Playwright + HttpClient
- **Deployment:** GitHub Pages, Azure Static Web Apps, Azure DevOps

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "SearxngPath is not configured" | Set the `Search:SearxngPath` in Settings or appsettings.json to where you cloned SearXNG |
| Hunter doesn't find anything | Make sure SearXNG is running (check dashboard status indicator) |
| "Copilot SDK timeout" | Your GitHub token may have expired — run `gh auth token` again and update Settings |
| Port 5555 already in use | Another instance is running, or change the port in `Program.cs` |
| Playwright errors | Run `playwright install chromium` to install browser binaries |

## License

MIT
