namespace DbSense.Core.Inference;

public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>"anthropic" or empty/disabled.</summary>
    public string Provider { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "claude-sonnet-4-6";

    public int MaxTokens { get; set; } = 1024;

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Provider) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
