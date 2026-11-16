namespace IteraPatch.Core.Models;

/// <summary>
/// Parameters for launching an isolated execution sandbox.
/// </summary>
public sealed record SandboxExecutionRequest(
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    int TimeoutSeconds = 60
);

/// <summary>
/// Standard execution outcome returned by the sandbox runner.
/// </summary>
public sealed record SandboxExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut
)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
