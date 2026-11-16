namespace IteraPatch.Core.Models;

/// <summary>
/// Standardized diagnostic payload transmitted to the model-agnostic LLM endpoint.
/// </summary>
public sealed record ErrorPayload(
    string ErrorType,
    string ErrorMessage,
    string StackTrace,
    string FailingFile,
    int FailingLine,
    string GitDiff,
    string TestCommand,
    int Iteration
);

/// <summary>
/// Atomic search-and-replace snippet patch suggested by the LLM.
/// </summary>
public sealed record FilePatchSuggestion(
    string FilePath,
    string SearchSnippet,
    string ReplaceSnippet
);

/// <summary>
/// Structured response returned by the LLM patch generator.
/// </summary>
public sealed record LlmPatchResponse(
    string Explanation,
    IReadOnlyList<FilePatchSuggestion> Patches
);

/// <summary>
/// Result of evaluating a patch against blast-radius safety boundaries.
/// </summary>
public sealed record PatchValidationResult(
    bool IsValid,
    string? RejectionReason,
    IReadOnlyList<string> ValidatedFiles
);

/// <summary>
/// Result of writing patch modifications to the working directory.
/// </summary>
public sealed record PatchApplyResult(
    bool Success,
    IReadOnlyList<string> ModifiedFiles,
    string? ErrorMessage
);
