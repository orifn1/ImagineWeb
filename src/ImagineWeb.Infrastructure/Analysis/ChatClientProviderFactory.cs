using System.ClientModel;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Analysis;

/// <summary>
/// Constructs Microsoft.Extensions.AI <see cref="IChatClient"/> instances for OpenAI-compatible
/// and Anthropic providers, plus the matching <see cref="ILlmClient"/> and
/// <see cref="ICodeGenerator"/> adapters. Centralises provider-specific construction so the
/// DI wire-up in Program.cs stays declarative.
/// </summary>
public sealed class ChatClientProviderFactory
{
    private readonly OpenAiConfig _openAi;
    private readonly AnthropicConfig _anthropic;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Lazy<IChatClient> _openAiChat;
    private readonly Lazy<IChatClient> _anthropicChat;

    public ChatClientProviderFactory(
        IOptions<OpenAiConfig> openAi,
        IOptions<AnthropicConfig> anthropic,
        ILoggerFactory loggerFactory)
    {
        _openAi = openAi.Value;
        _anthropic = anthropic.Value;
        _loggerFactory = loggerFactory;

        _openAiChat = new Lazy<IChatClient>(BuildOpenAi);
        _anthropicChat = new Lazy<IChatClient>(BuildAnthropic);
    }

    public IChatClient OpenAiChatClient => _openAiChat.Value;
    public IChatClient AnthropicChatClient => _anthropicChat.Value;

    public ILlmClient CreateOpenAiLlm() => new ChatClientLlmAdapter(OpenAiChatClient, new ChatClientLlmOptions
    {
        ProviderName = "OpenAi",
        DefaultModel = _openAi.Model,
        SecondaryModel = _openAi.SecondaryModel,
        ApiKey = _openAi.ApiKey,
        Temperature = _openAi.Temperature,
        TimeoutSeconds = _openAi.TimeoutSeconds,
        MaxOutputTokens = _openAi.MaxOutputTokens,
        MaxConcurrentRequests = _openAi.MaxConcurrentRequests,
        ContextWindowTokens = _openAi.ContextWindowTokens
    });

    public ILlmClient CreateAnthropicLlm() => new ChatClientLlmAdapter(AnthropicChatClient, new ChatClientLlmOptions
    {
        ProviderName = "Anthropic",
        DefaultModel = _anthropic.Model,
        SecondaryModel = _anthropic.SecondaryModel,
        ApiKey = _anthropic.ApiKey,
        Temperature = _anthropic.Temperature,
        TimeoutSeconds = _anthropic.TimeoutSeconds,
        MaxOutputTokens = _anthropic.MaxOutputTokens,
        MaxConcurrentRequests = _anthropic.MaxConcurrentRequests,
        ContextWindowTokens = _anthropic.ContextWindowTokens
    });

    public ICodeGenerator CreateOpenAiCodeGenerator(CodeGeneratorConfig codeGenConfig) =>
        new ChatClientCodeGenerator(OpenAiChatClient, new ChatClientCodeGeneratorOptions
        {
            ProviderName = "OpenAi",
            DefaultModel = string.IsNullOrWhiteSpace(codeGenConfig.Model) ? _openAi.Model : codeGenConfig.Model!,
            ApiKey = _openAi.ApiKey,
            Temperature = _openAi.Temperature,
            TimeoutSeconds = codeGenConfig.TimeoutSeconds,
            MaxOutputTokens = _openAi.MaxOutputTokens
        }, _loggerFactory.CreateLogger<ChatClientCodeGenerator>());

    public ICodeGenerator CreateAnthropicCodeGenerator(CodeGeneratorConfig codeGenConfig) =>
        new ChatClientCodeGenerator(AnthropicChatClient, new ChatClientCodeGeneratorOptions
        {
            ProviderName = "Anthropic",
            DefaultModel = string.IsNullOrWhiteSpace(codeGenConfig.Model) ? _anthropic.Model : codeGenConfig.Model!,
            ApiKey = _anthropic.ApiKey,
            Temperature = _anthropic.Temperature,
            TimeoutSeconds = codeGenConfig.TimeoutSeconds,
            MaxOutputTokens = _anthropic.MaxOutputTokens
        }, _loggerFactory.CreateLogger<ChatClientCodeGenerator>());

    private IChatClient BuildOpenAi()
    {
        var apiKey = string.IsNullOrEmpty(_openAi.ApiKey) ? "missing-key" : _openAi.ApiKey;
        var clientOptions = new global::OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(_openAi.BaseUrl))
            clientOptions.Endpoint = new Uri(_openAi.BaseUrl);

        var openAi = new global::OpenAI.OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return openAi.GetChatClient(_openAi.Model).AsIChatClient();
    }

    private IChatClient BuildAnthropic()
    {
        var apiKey = string.IsNullOrEmpty(_anthropic.ApiKey) ? "missing-key" : _anthropic.ApiKey;
        var anthropic = new global::Anthropic.SDK.AnthropicClient(apiKey);
        return anthropic.Messages;
    }
}
