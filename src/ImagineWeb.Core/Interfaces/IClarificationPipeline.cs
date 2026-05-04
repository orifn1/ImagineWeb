using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IClarificationPipeline
{
    SpecificationDraft NormalizeToDraft(PipelineInput input);

    Task<ClarificationResult> ClarifyAsync(
        SpecificationDraft draft,
        ClarificationModel model,
        string? workingDirectory,
        CancellationToken ct,
        string? clarificationModelId = null,
        string? providerOverride = null);

    ClarificationQualityWarning? AssessQuality(ClarificationResponse response);

    FinalSpecification BuildFinalSpec(
        SpecificationDraft draft,
        ClarificationResponse clarification,
        ClarificationAnswers answers);

    Task<CodeGenerationHandle> GenerateCodeAsync(
        FinalSpecification spec,
        string solutionDirectory,
        CancellationToken ct,
        string? model = null,
        string? providerOverride = null,
        string? reasoningEffort = null,
        string? fixModel = null);

    Task<CodeGenerationHandle> ImproveAsync(
        string solutionDirectory,
        string instruction,
        CancellationToken ct,
        string? model = null,
        List<string>? attachmentPaths = null,
        string? providerOverride = null,
        string? reasoningEffort = null);
}
