using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class ClarificationPipeline : IClarificationPipeline
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly IPageAnalyzer _analyzer;
    private readonly CopilotSdkCodeGenerator _copilotSdk;
    private readonly CodeGeneratorFactory _codeGeneratorFactory;
    private readonly ILlmProviderResolver _providerResolver;
    private readonly ExecutorConfig _executorConfig;
    private readonly ILogger<ClarificationPipeline> _logger;

    public ClarificationPipeline(
        IPageAnalyzer analyzer,
        CopilotSdkCodeGenerator copilotSdk,
        CodeGeneratorFactory codeGeneratorFactory,
        ILlmProviderResolver providerResolver,
        IOptions<ExecutorConfig> executorConfig,
        ILogger<ClarificationPipeline> logger)
    {
        _analyzer = analyzer;
        _copilotSdk = copilotSdk;
        _codeGeneratorFactory = codeGeneratorFactory;
        _providerResolver = providerResolver;
        _executorConfig = executorConfig.Value;
        _logger = logger;
    }

    public SpecificationDraft NormalizeToDraft(PipelineInput input)
    {
        return input.Draft;
    }

    public async Task<ClarificationResult> ClarifyAsync(
        SpecificationDraft draft,
        ClarificationModel model,
        string? workingDirectory,
        CancellationToken ct,
        string? clarificationModelId = null,
        string? providerOverride = null)
    {
        var prompt = BuildClarificationPrompt(draft);
        var jsonSystemPrompt = "You MUST respond with ONLY a single JSON object. No markdown, no prose, no code fences.";

        string rawResponse;
        if (!string.IsNullOrWhiteSpace(providerOverride) &&
            !providerOverride.Equals("copilotsdk", StringComparison.OrdinalIgnoreCase) &&
            !providerOverride.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            // OpenAI / Anthropic route through ILlmProviderResolver.
            var llm = _providerResolver.Resolve(providerOverride);
            var modelId = string.IsNullOrWhiteSpace(clarificationModelId) ? llm.DefaultModel : clarificationModelId!;
            rawResponse = await llm.GenerateAsync($"{jsonSystemPrompt}\n\n{prompt}", modelId, ct);
        }
        else if (!string.IsNullOrEmpty(clarificationModelId))
        {
            rawResponse = await _copilotSdk.SendAndWaitForResponseAsync(prompt, jsonSystemPrompt, ct, model: clarificationModelId);
        }
        else
        {
            rawResponse = await _analyzer.RawPromptAsync(prompt, ct);
        }

        var response = ParseClarificationResponse(rawResponse);
        _logger.LogInformation(
            "Clarification complete (provider={Provider}, model={Model}): confidence={Confidence}, questions={Count}",
            providerOverride ?? "default", clarificationModelId ?? "default-analyzer",
            response.Confidence, response.ClarifyingQuestions.Count);
        return new ClarificationResult(response, SdkSessionId: null);
    }

    public ClarificationQualityWarning? AssessQuality(ClarificationResponse response)
    {
        if (response.Confidence == "low")
        {
            return new ClarificationQualityWarning
            {
                Reason = "The local model produced a low-confidence analysis. Questions may be vague or miss important aspects.",
                UsedModel = ClarificationModel.Local
            };
        }

        if (response.ClarifyingQuestions.Count == 0 && response.Confidence != "high")
        {
            return new ClarificationQualityWarning
            {
                Reason = "No clarifying questions were generated, but confidence is not high. The model may have missed important details.",
                UsedModel = ClarificationModel.Local
            };
        }

        var vagueQuestions = response.ClarifyingQuestions
            .Where(q => string.IsNullOrWhiteSpace(q.Reason) || q.Question.Length < 15)
            .ToList();

        if (vagueQuestions.Count > response.ClarifyingQuestions.Count / 2 && vagueQuestions.Count > 0)
        {
            return new ClarificationQualityWarning
            {
                Reason = $"{vagueQuestions.Count} of {response.ClarifyingQuestions.Count} questions appear vague or lack reasoning.",
                UsedModel = ClarificationModel.Local
            };
        }

        return null;
    }

    public FinalSpecification BuildFinalSpec(
        SpecificationDraft draft,
        ClarificationResponse clarification,
        ClarificationAnswers answers)
    {
        var collectedEnvVars = new Dictionary<string, string>(answers.CollectedEnvVars);

        foreach (var envVar in clarification.RequiredEnvVars)
        {
            if (envVar.Source == "user_input"
                && answers.CollectedEnvVars.TryGetValue(envVar.Key, out var val))
            {
                collectedEnvVars[envVar.Key] = val;
            }
        }

        return new FinalSpecification
        {
            Draft = draft,
            Clarification = clarification,
            UserAnswers = answers,
            CollectedEnvVars = collectedEnvVars
        };
    }

    public async Task<CodeGenerationHandle> GenerateCodeAsync(
        FinalSpecification spec,
        string solutionDirectory,
        CancellationToken ct,
        string? model = null,
        string? providerOverride = null,
        string? reasoningEffort = null,
        string? fixModel = null)
    {
        Directory.CreateDirectory(solutionDirectory);
        Directory.CreateDirectory(Path.Combine(solutionDirectory, "site"));

        var promptContent = BuildCodeGenerationPrompt(spec);
        var promptPath = Path.Combine(solutionDirectory, "prompt.md");
        await File.WriteAllTextAsync(promptPath, promptContent, ct);
        _logger.LogInformation("Code generation prompt written ({Len} chars) to {Path}", promptContent.Length, promptPath);

        var envExamplePath = Path.Combine(solutionDirectory, ".env.example");
        await File.WriteAllTextAsync(envExamplePath, BuildEnvExample(spec), ct);

        var clarifyJson = Path.Combine(solutionDirectory, "clarification.json");
        if (File.Exists(clarifyJson))
        {
            var metaDir = Path.Combine(solutionDirectory, ".meta");
            Directory.CreateDirectory(metaDir);
            File.Move(clarifyJson, Path.Combine(metaDir, "clarification.json"), overwrite: true);
        }

        // Route to the appropriate code generator based on per-request override or model prefix.
        ICodeGenerator generator;
        string? resolvedModel = model;
        if (!string.IsNullOrWhiteSpace(providerOverride))
        {
            generator = await _codeGeneratorFactory.GetGeneratorAsync(ct, providerOverride);
        }
        else if (model != null && model.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            resolvedModel = model["ollama:".Length..];
            generator = _codeGeneratorFactory.GetOllamaGenerator();
        }
        else
        {
            generator = model is null
                ? await _codeGeneratorFactory.GetGeneratorAsync(ct)
                : _copilotSdk;
        }

        _logger.LogInformation(
            "Starting {Generator} code generation in {Dir}. Prompt first 300 chars: {Preview}",
            generator.GetType().Name,
            solutionDirectory,
            promptContent.Length > 300 ? promptContent[..300] + "..." : promptContent);

        var handle = await generator.StartAsync(new CodeGenerationRequest
        {
            PromptFilePath = promptPath,
            WorkingDirectory = solutionDirectory,
            SystemMessageAppend = PromptSections.CopilotSdkDeploymentContext(solutionDirectory),
            Model = resolvedModel,
            Streaming = true,
            ReasoningEffort = reasoningEffort,
            FixModel = fixModel
        }, ct);

        _logger.LogInformation(
            "Code generation started: {GenerationId} at {Path}",
            handle.GenerationId, solutionDirectory);

        return handle;
    }

    public async Task<CodeGenerationHandle> ImproveAsync(
        string solutionDirectory,
        string instruction,
        CancellationToken ct,
        string? model = null,
        List<string>? attachmentPaths = null,
        string? providerOverride = null,
        string? reasoningEffort = null)
    {
        var siteDir = Path.Combine(solutionDirectory, "site");
        var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", "bin", "obj", ".git", ".vs", ".vscode", "__pycache__" };
        var fileList = Directory.Exists(siteDir)
            ? string.Join("\n", Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories)
                .Where(f => !excludedDirs.Any(d => f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar)
                    || f.Contains(Path.AltDirectorySeparatorChar + d + Path.AltDirectorySeparatorChar)))
                .Select(f => "  " + Path.GetRelativePath(solutionDirectory, f)))
            : "(no files yet)";

        var iacContext = BuildIaCContext(solutionDirectory);

        var promptContent = $"""
            # Improvement Request

            The working directory `{solutionDirectory}` contains an existing web application with these files:
            {fileList}

            {iacContext}

            ## User Instruction
            {instruction}

            ## Rules
            - Modify existing files in place. Do NOT delete or recreate files unless explicitly asked.
            - If the change requires infrastructure updates, update the Bicep files in `infra/` accordingly.
            - Keep all existing functionality working unless the user asks to remove something.
            - Follow the same coding style and patterns already in the project.
            - Use absolute paths for every file you create or edit.
            {PromptSections.StrictCodeRules()}
            {PromptSections.SelfValidation()}

            {PromptSections.IaCFixRules()}
            """;

        var promptPath = Path.Combine(solutionDirectory, "improve-prompt.md");
        await File.WriteAllTextAsync(promptPath, promptContent, ct);

        var sendPrompt =
            $"Read the attached improve-prompt.md. The `{solutionDirectory}` directory contains an existing project. " +
            $"Apply the requested changes to the existing files. Use absolute paths for every file you create or edit.";

        ICodeGenerator generator;
        string? resolvedModel = model;
        if (!string.IsNullOrWhiteSpace(providerOverride))
        {
            generator = await _codeGeneratorFactory.GetGeneratorAsync(ct, providerOverride);
        }
        else if (model != null && model.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
        {
            resolvedModel = model["ollama:".Length..];
            generator = _codeGeneratorFactory.GetOllamaGenerator();
        }
        else
        {
            generator = model is null
                ? await _codeGeneratorFactory.GetGeneratorAsync(ct)
                : _copilotSdk;
        }

        var handle = await generator.StartAsync(new CodeGenerationRequest
        {
            PromptFilePath = promptPath,
            WorkingDirectory = solutionDirectory,
            SystemMessageAppend = PromptSections.CopilotSdkDeploymentContext(solutionDirectory),
            Model = resolvedModel,
            CustomSendPrompt = sendPrompt,
            Streaming = true,
            AttachmentPaths = attachmentPaths,
            IsImprovement = true,
            ReasoningEffort = reasoningEffort
        }, ct);

        _logger.LogInformation("Improvement started: {GenerationId} at {Path}", handle.GenerationId, solutionDirectory);
        return handle;
    }

    private static string BuildClarificationPrompt(SpecificationDraft draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are a senior product analyst with expertise in UX, business strategy, and web development.
            You will receive a specification draft for a web application that needs to be built and deployed to Azure.

            FIRST: Use web search to briefly research the topic before formulating questions.
            Understanding the domain helps you ask the right questions and make realistic assumptions.

            Your task is to respond with a SINGLE JSON object (no markdown, no prose, no extra text) that:
            1. Summarizes your understanding of what needs to be built
            2. Rates your confidence (high/medium/low)
            3. Asks clarifying questions needed for code generation
            4. Lists assumptions you'll make if not answered
            5. Identifies required environment variables

            IMPORTANT: ONLY ask questions about things that require HUMAN knowledge:
            - Business logic and domain rules that can't be inferred from the description
            - User intentions and preferences (e.g. "Should users need accounts?", "What pricing model?")
            - External service dependencies the user must provide (API keys, data sources)
            - Target audience details that affect UX decisions

            DO NOT ask about:
            - Technical architecture choices (YOU decide: framework, database, hosting type)
            - Azure resource configuration (auto-detected and auto-scaffolded)
            - Deployment details (handled automatically via azd)
            - Code structure, patterns, or libraries (YOU choose best practices)
            - Infrastructure or IaC details (auto-generated)
            - Performance/scaling settings (use sensible defaults)

            If you can make a reasonable technical assumption — make it and list it in "assumptions".
            Fewer, higher-quality questions are better than many technical ones.

            A GOOD clarifying question asks about business intent or domain knowledge:
            - "Do users need accounts to save their results?"
            - "What external data source provides the pricing data?"
            - "Should the tool be free or have a paid tier?"
            A BAD question asks about technical implementation:
            - "Should we use React or vanilla JS?" (YOU decide)
            - "What Azure region should we deploy to?" (auto-configured)
            - "Should we add caching?" (YOU decide based on the use case)

            REQUIRED JSON SCHEMA:
            {
              "summary": "string — your understanding of what needs to be built",
              "confidence": "high | medium | low",
              "clarifying_questions": [
                {
                  "id": "string — unique id like q1, q2",
                  "question": "string",
                  "reason": "string — why this is needed for code generation",
                  "input_type": "text | select | multiselect | boolean",
                  "options": ["string"] 
                }
              ],
              "assumptions": [
                "string — things you will assume if not answered"
              ],
              "required_env_vars": [
                {
                  "key": "string — exact env var name e.g. OPENAI_API_KEY",
                  "description": "string",
                  "source": "user_input | azd_output | azure_keyvault",
                  "required_before_deploy": true,
                  "example_value": "string — safe non-real example"
                }
              ]
            }

            Rules:
            - Output ONLY valid JSON. No markdown fences, no explanation text.
            - Ask 2-7 clarifying questions maximum — only what truly needs human input.
            - Each question with options must have 2-5 options.
            - input_type "select" = single choice, "multiselect" = multiple choices, "boolean" = yes/no, "text" = free text.
            - For env vars: use source "azd_output" for values that Azure infrastructure will provide (e.g. database connection strings, storage URLs).
            - Use source "azure_keyvault" for secrets that should be stored in Key Vault.
            - Use source "user_input" ONLY for values the user must provide (API keys for external services, etc.).
            - Always include AZURE_RESOURCE_GROUP and AZURE_LOCATION as azd_output vars.
            """);

        sb.AppendLine();
        sb.AppendLine("## Specification Draft");
        sb.AppendLine($"**Title:** {draft.Title}");
        sb.AppendLine($"**Description:** {draft.Description}");

        if (!string.IsNullOrWhiteSpace(draft.TargetAudience))
            sb.AppendLine($"**Target Audience:** {draft.TargetAudience}");

        if (!string.IsNullOrWhiteSpace(draft.ActionPlan))
            sb.AppendLine($"**Action Plan:** {draft.ActionPlan}");

        if (!string.IsNullOrWhiteSpace(draft.MonetizationHint))
            sb.AppendLine($"**Monetization Hint:** {draft.MonetizationHint}");

        if (draft.KeyFacts.Count > 0)
        {
            sb.AppendLine("**Key Facts:**");
            foreach (var fact in draft.KeyFacts)
                sb.AppendLine($"- {fact}");
        }

        if (draft.Metadata.Count > 0)
        {
            sb.AppendLine("**Additional Context:**");
            foreach (var (key, value) in draft.Metadata)
                sb.AppendLine($"- {key}: {value}");
        }

        return sb.ToString();
    }

    private static ClarificationResponse ParseClarificationResponse(string raw)
    {
        raw = raw.Trim();

        var jsonStart = raw.IndexOf('{');
        var jsonEnd = raw.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = raw[jsonStart..(jsonEnd + 1)];
            try
            {
                return JsonSerializer.Deserialize<ClarificationResponse>(jsonStr, JsonOpts)
                    ?? new ClarificationResponse { Summary = raw, Confidence = "low" };
            }
            catch { }
        }

        return new ClarificationResponse
        {
            Summary = raw,
            Confidence = "low",
            ClarifyingQuestions = [],
            Assumptions = ["Unable to parse structured response — proceeding with available information"]
        };
    }

    private static string BuildCodeGenerationPrompt(FinalSpecification spec)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {spec.Draft.Title}");
        sb.AppendLine();
        sb.AppendLine(spec.Draft.Description);
        sb.AppendLine();

        sb.AppendLine(PromptSections.StrictCodeRules());
        sb.AppendLine();
        sb.AppendLine(PromptSections.ProductionQualityRules());
        sb.AppendLine();

        sb.AppendLine("""
            DEPLOYMENT CONTEXT:
            - Deploy with `azd up` / `azd deploy` (Azure Developer CLI)
            - IaC goes in `infra/` as Bicep; parameters file: `infra/main.bicepparam`
            - Choose the right Azure architecture for the project type:
                * Static website → Azure Static Web Apps (host: staticwebapp)
                * Full-stack app → Azure App Service or Azure Container Apps + Azure Database
                * API-only → Azure Functions or Azure Container Apps
            - All env vars with source `azd_output` MUST be wired as Bicep outputs
              and bound via `azure.yaml` service bindings — never hardcode them

            """);

        if (spec.CollectedEnvVars.Count > 0)
        {
            sb.AppendLine("ENVIRONMENT VARIABLES (already collected from user — use directly, do not re-ask):");
            foreach (var (key, value) in spec.CollectedEnvVars)
                sb.AppendLine($"  {key}={value}");
            sb.AppendLine();
        }

        var azdVars = spec.Clarification.RequiredEnvVars
            .Where(v => v.Source == "azd_output").ToList();
        if (azdVars.Count > 0)
        {
            sb.AppendLine("AZD OUTPUT VARIABLES (must be wired as Bicep outputs + azure.yaml bindings):");
            foreach (var v in azdVars)
                sb.AppendLine($"  {v.Key} — {v.Description}");
            sb.AppendLine();
        }

        var kvVars = spec.Clarification.RequiredEnvVars
            .Where(v => v.Source == "azure_keyvault").ToList();
        if (kvVars.Count > 0)
        {
            sb.AppendLine("KEY VAULT SECRETS (must be referenced via Key Vault secret references in app config):");
            foreach (var v in kvVars)
                sb.AppendLine($"  {v.Key} — {v.Description}");
            sb.AppendLine();
        }



        if (!string.IsNullOrWhiteSpace(spec.Draft.TargetAudience))
            sb.AppendLine($"## Target Audience\n{spec.Draft.TargetAudience}\n");

        if (!string.IsNullOrWhiteSpace(spec.Draft.ActionPlan))
            sb.AppendLine($"## Action Plan\n{spec.Draft.ActionPlan}\n");

        if (!string.IsNullOrWhiteSpace(spec.Draft.MonetizationHint))
            sb.AppendLine($"## Monetization\n{spec.Draft.MonetizationHint}\n");

        if (spec.Clarification.Assumptions.Count > 0)
        {
            sb.AppendLine("## Assumptions");
            foreach (var a in spec.Clarification.Assumptions)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        if (spec.UserAnswers.Answers.Count > 0)
        {
            sb.AppendLine("## Clarification Answers");
            foreach (var (questionId, answer) in spec.UserAnswers.Answers)
            {
                var question = spec.Clarification.ClarifyingQuestions
                    .FirstOrDefault(q => q.Id == questionId);
                sb.AppendLine($"**Q:** {question?.Question ?? questionId}");
                sb.AppendLine($"**A:** {answer}");
                sb.AppendLine();
            }
        }

        if (spec.Draft.KeyFacts.Count > 0)
        {
            sb.AppendLine("## Key Facts");
            foreach (var fact in spec.Draft.KeyFacts)
                sb.AppendLine($"- {fact}");
            sb.AppendLine();
        }

        // Deployment / IaC / SelfValidation rules are appended via Copilot SDK SystemMessage
        // (CopilotSdkDeploymentContext) on every turn — duplicating them here is wasted tokens.

        return sb.ToString();
    }

    private static string BuildEnvExample(FinalSpecification spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Environment Variables");
        sb.AppendLine("# Copy this file to .env and fill in real values");
        sb.AppendLine();

        foreach (var envVar in spec.Clarification.RequiredEnvVars)
        {
            sb.AppendLine($"# {envVar.Description}");
            sb.AppendLine($"# Source: {envVar.Source}");
            if (envVar.RequiredBeforeDeploy)
                sb.AppendLine("# Required before deployment");
            var displayValue = envVar.Source switch
            {
                "azd_output" => "<set-by-azd-provision>",
                "azure_keyvault" => "<stored-in-keyvault>",
                _ => ""
            };
            sb.AppendLine($"{envVar.Key}={displayValue}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildIaCContext(string solutionDirectory)
    {
        // Inline only files <= 4 KB. Larger files are listed by path so the model can
        // open them via the file tool on demand instead of bloating every improve prompt.
        const int InlineSizeThresholdBytes = 4096;

        var sb = new StringBuilder();
        var infraDir = Path.Combine(solutionDirectory, "infra");
        var azureYamlPath = Path.Combine(solutionDirectory, "azure.yaml");

        sb.AppendLine("## Current Infrastructure-as-Code Files");

        void AppendFile(string path)
        {
            var rel = Path.GetRelativePath(solutionDirectory, path);
            var info = new FileInfo(path);
            var lang = path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ? "yaml" : "bicep";
            if (info.Length <= InlineSizeThresholdBytes)
                sb.AppendLine($"\n### {rel}\n```{lang}\n{File.ReadAllText(path).Trim()}\n```");
            else
                sb.AppendLine($"\n### {rel} ({info.Length} bytes — open via file tool to read)");
        }

        if (File.Exists(azureYamlPath)) AppendFile(azureYamlPath);

        if (Directory.Exists(infraDir))
        {
            foreach (var f in Directory.EnumerateFiles(infraDir, "*.bicep")) AppendFile(f);
            foreach (var f in Directory.EnumerateFiles(infraDir, "*.bicepparam")) AppendFile(f);
        }

        if (sb.Length < 60)
            sb.AppendLine("(no IaC files found)");

        return sb.ToString();
    }
}
