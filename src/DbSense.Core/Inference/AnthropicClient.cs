using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DbSense.Core.Inference;

public interface IAnthropicClient
{
    Task<AnthropicMessageResult> CreateMessageAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default);
}

public record AnthropicMessageResult(
    string Text,
    int InputTokens,
    int OutputTokens);

public class AnthropicClient : IAnthropicClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly IOptions<LlmOptions> _options;

    public AnthropicClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task<AnthropicMessageResult> CreateMessageAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.IsEnabled)
            throw new InvalidOperationException("LLM provider is not configured.");

        var body = new RequestBody(
            Model: opts.Model,
            MaxTokens: opts.MaxTokens,
            System: systemPrompt,
            Messages: new[] { new RequestMessage("user", userPrompt) });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", opts.ApiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

        var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException(
                $"Anthropic API returned {(int)resp.StatusCode}: {errorBody}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<ResponseBody>(cancellationToken: cts.Token)
            ?? throw new InvalidOperationException("Empty response from Anthropic.");

        var text = string.Concat(parsed.Content
            .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Text ?? string.Empty));

        return new AnthropicMessageResult(
            text,
            parsed.Usage?.InputTokens ?? 0,
            parsed.Usage?.OutputTokens ?? 0);
    }

    private record RequestBody(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] RequestMessage[] Messages);

    private record RequestMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ResponseBody(
        [property: JsonPropertyName("content")] List<ResponseContent> Content,
        [property: JsonPropertyName("usage")] ResponseUsage? Usage);

    private record ResponseContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    private record ResponseUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);
}
