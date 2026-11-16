namespace IteraPatch.Core.Configuration;

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string ApiKey { get; set; } = "dummy-key";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.0;
    public int TimeoutSeconds { get; set; } = 60;
}
