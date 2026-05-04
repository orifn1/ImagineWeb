namespace ImagineWeb.Core.Interfaces;

public enum LlmPriority { Pipeline, Executor }

public enum OllamaPriority { Pipeline, Executor }

public interface ILlmGate
{
    Task<IDisposable> AcquireAsync(LlmPriority priority, CancellationToken ct);
}

public interface IOllamaGate
{
    Task<IDisposable> AcquireAsync(OllamaPriority priority, CancellationToken ct);
}
