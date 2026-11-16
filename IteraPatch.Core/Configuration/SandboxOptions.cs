namespace IteraPatch.Core.Configuration;

public sealed class SandboxOptions
{
    public string Image { get; set; } = "python:3.11-slim";
    public int TimeoutSeconds { get; set; } = 60;
    public long MemoryLimitBytes { get; set; } = 512 * 1024 * 1024; // 512MB
    public string WorkingDirectory { get; set; } = "/workspace";
}
