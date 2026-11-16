namespace IteraPatch.Core.Models;

/// <summary>
/// Detailed diagnostic record for a single verification iteration.
/// </summary>
public sealed record IterationStep(
    int Attempt,
    SandboxExecutionResult ExecutionResult,
    TraceParseResult TraceResult,
    LlmPatchResponse? PatchResponse,
    bool PatchApplied
);

/// <summary>
/// Final result of the recursive verification loop.
/// </summary>
public sealed record OrchestratorResult(
    bool Success,
    int TotalAttempts,
    IReadOnlyList<IterationStep> History,
    string Message
);
