using System.Reflection;

namespace ImagineWeb.Infrastructure.Execution;

public static class PromptSections
{
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly Lock CacheLock = new();

    public static string AzureDeploymentContext() => Load("AzureDeploymentContext");

    public static string IaCRequirements() => Load("IaCRequirements");

    public static string IaCFixRules() => Load("IaCFixRules");

    public static string StrictCodeRules() => Load("StrictCodeRules");

    public static string SelfValidation() => Load("SelfValidation");

    public static string ProductionQualityRules() => Load("ProductionQualityRules");

    public static string CopilotSdkDeploymentContext(string workingDirectory) =>
        $$"""
        DEPLOYMENT CONTEXT:
        This application will be deployed to Azure via Azure Developer CLI (azd).
        Infrastructure-as-Code files are auto-scaffolded — do NOT create azure.yaml, infra/, or azure-pipelines.yml during initial code generation.

        YOU decide the architecture and tech stack. Prefer C# / ASP.NET Core for server-side code.
        Use free tier / minimal-cost Azure resources.

        RULES:
        1. ALL application code MUST be inside {{workingDirectory}}/site/
        2. Do NOT create or modify files outside site/ during initial code generation (IaC is auto-scaffolded after code generation)
        3. Use absolute file paths when creating or editing files
        4. Do NOT hardcode ports or URLs — use default framework bindings (ASP.NET Core reads ASPNETCORE_URLS; Node.js must use process.env.PORT || 8080)
        5. For Vite/React projects, keep the default build output in `dist/` — do NOT change `outDir` in vite.config

        WHEN FIXING DEPLOYMENT ERRORS (improve requests):
        You MAY edit azure.yaml and infra/*.bicep. Critical rules:
        - Every Azure resource that hosts the app MUST have tag `'azd-service-name': 'web'`
        - Resource group MUST have tag `'azd-env-name': environmentName`
        - azure.yaml `dist:` is relative to `project:` path — for Vite use `dist: dist`, for static HTML use `dist: .`
        - NEVER use `shell: sh` in azure.yaml hooks — use `shell: pwsh`
        - NEVER add `resourceName:` to azure.yaml — it breaks azd resource lookup
        - NEVER create infra/main.json — azd uses main.bicep + main.bicepparam

        MANDATORY SELF-VALIDATION:
        After creating or modifying ALL files, you MUST build and test before finishing:
        - .NET (has .csproj): run `dotnet build <path> --nologo -v q` in the terminal
        - Node.js (has package.json): run `cd <site-dir> && npm install && npm run build`
        - Static HTML: no build needed
        If the build fails, fix the errors and re-run until it passes. Do NOT finish with broken code.
        """;

    public static string IaCGenerationPrompt(string workingDirectory) =>
        IaCCustomizationPrompt(workingDirectory, null);

    public static string IaCCustomizationPrompt(string workingDirectory, string? scaffoldedBicep) =>
        $$"""
        IaC files have been auto-generated at {{workingDirectory}}/infra/ based on detected resources.
        Review the Bicep templates below and customize ONLY if the application needs additional
        Azure resources not already included (e.g. storage account, database, cache, search service).

        {{(scaffoldedBicep is not null ? $"Current resources.bicep:\n```bicep\n{scaffoldedBicep}\n```" : "")}}

        Rules:
        - Do NOT change azure.yaml or azure-pipelines.yml — they are auto-generated and must not be modified
        - Do NOT create infra/main.json or any compiled ARM template — azd uses main.bicep directly
        - In resources.bicep, keep the existing structure (params, tags)
        - NEVER remove the `'azd-service-name': 'web'` tag from any resource — azd needs it to find the deployment target
        - NEVER remove the `'azd-env-name': environmentName` tag from the resource group
        - NEVER add `resourceName:` property to azure.yaml — it breaks azd tag-based resource lookup
        - Use managed identity (SystemAssigned) for service-to-service auth
        - Use free tier / minimal-cost SKUs
        - Consult Azure documentation for current API versions if adding resources
        - Use absolute paths when editing files

        If no additional resources are needed, respond: "IaC OK — no changes needed."
        """;

    public static string CombinedFixPrompt(string workingDirectory, List<string> issues) =>
        $$"""
        The generated project in {{workingDirectory}} has the following issues that MUST be fixed.
        Fix ALL of them. Use absolute paths when creating or editing files.

        {{string.Join("\n\n", issues.Select((issue, i) => $"### Issue {i + 1}\n{TruncateIssue(issue)}"))}}

        After fixing, do NOT re-create files that are already correct.
        """;

    private static string TruncateIssue(string issue, int maxLength = 1500)
    {
        if (issue.Length <= maxLength) return issue;
        // Keep head + tail so error origin and final exception line both reach the model.
        var head = issue[..(maxLength * 2 / 3)];
        var tail = issue[^(maxLength / 3)..];
        return $"{head}\n... ({issue.Length - maxLength} chars truncated) ...\n{tail}";
    }

    public static string SiteValidationPrompt(string workingDirectory, string? buildErrors) =>
        $$"""
        Perform a final quality review of the generated application in {{workingDirectory}}/site/.
        Check every item below and IMMEDIATELY FIX any failures. Use absolute paths.

        {{(buildErrors is not null ? $"## Build Errors (MUST FIX FIRST)\nThe project failed to build with these errors:\n```\n{buildErrors}\n```\nFix all build errors before proceeding to the checklist.\n" : "")}}

        ## Quality Checklist

        ### 1. Entry Point & Structure
        - An entry point exists (index.html for static sites, Program.cs/.csproj for .NET, package.json with start script for Node)
        - No orphaned files or empty placeholder files

        ### 2. Code Hygiene
        - No TODO, FIXME, PLACEHOLDER, or "your-api-key" strings anywhere in the code
        - No hardcoded `localhost` URLs (use relative paths or env vars)
        - No `console.log` debugging statements left in production code
        - No commented-out code blocks

        ### 3. Responsive & Visual
        - `<meta name="viewport">` tag present in all HTML pages
        - No fixed-width containers that break on mobile (no `width: 1200px` style without max-width)
        - A favicon is defined

        ### 4. SEO
        - `<title>` and `<meta name="description">` on every page
        - At least one Open Graph meta tag (og:title)

        ### 5. Accessibility
        - All `<img>` tags have `alt` attributes
        - All form inputs have `<label>` elements or `aria-label`

        ### 6. Monetization (skip if the project explicitly has no monetization)
        - At least one revenue mechanism is wired (affiliate link placeholders, email capture form, or paid product CTA)
        - Email capture form has a valid action endpoint placeholder (not `#`)

        If everything passes, respond: "Site validation OK."
        Otherwise, fix each failing item and briefly say what you changed.
        """;

    private static string Load(string name)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(name, out var cached))
                return cached;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ImagineWeb.Infrastructure.Execution.Prompts.{name}.txt";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        lock (CacheLock) { Cache[name] = content; }
        return content;
    }
}
