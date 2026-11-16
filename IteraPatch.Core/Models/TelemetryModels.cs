namespace IteraPatch.Core.Models;

/// <summary>
/// Represents a structured test failure parsed from runner execution output.
/// </summary>
public sealed record ParsedFailure(
    string FailingFile,
    int FailingLine,
    string ErrorType,
    string ErrorMessage,
    string StackTrace,
    bool IsWithinGitDelta
);

/// <summary>
/// Complete parsed failure diagnostics extracted from sandbox stdout/stderr.
/// </summary>
public sealed record TraceParseResult(
    bool HasFailure,
    IReadOnlyList<ParsedFailure> Failures,
    string RawOutput
);
