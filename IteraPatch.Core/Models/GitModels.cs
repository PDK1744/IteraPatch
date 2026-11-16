namespace IteraPatch.Core.Models;

public enum GitChangeType
{
    Modified,
    Added,
    Deleted,
    Renamed
}

/// <summary>
/// 1-based inclusive line interval affected by changes.
/// </summary>
public sealed record LineRange(int StartLine, int EndLine);

/// <summary>
/// Detailed change delta for a single tracked file.
/// </summary>
public sealed record GitFileDelta(
    string FilePath,
    GitChangeType ChangeType,
    IReadOnlyList<LineRange> ChangedRanges,
    string PatchContent
);

/// <summary>
/// Aggregated working tree changes relative to HEAD.
/// </summary>
public sealed record GitDeltaReport(
    IReadOnlyList<GitFileDelta> FileDeltas,
    string CombinedDiff
)
{
    public bool HasChanges => FileDeltas.Count > 0;
}
