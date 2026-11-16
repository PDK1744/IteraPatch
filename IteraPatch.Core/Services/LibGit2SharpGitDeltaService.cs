namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Inspects uncommitted working tree changes against HEAD using LibGit2Sharp.
/// </summary>
public sealed partial class LibGit2SharpGitDeltaService : IGitDeltaService
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(?<oldStart>\d+)(?:,\d+)?\s+\+(?<newStart>\d+)(?:,\d+)?\s+@@",
        RegexOptions.Compiled);

    private readonly ILogger<LibGit2SharpGitDeltaService>? _logger;

    public LibGit2SharpGitDeltaService(ILogger<LibGit2SharpGitDeltaService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<GitDeltaReport> GetWorkingTreeChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path '{repositoryPath}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger?.LogDebug("Opening Git repository at {RepositoryPath}", repositoryPath);
            using var repo = new Repository(repositoryPath);

            var headTree = repo.Head.Tip?.Tree;
            _logger?.LogDebug(
                "Comparing working tree against {HeadCommit}",
                repo.Head.Tip != null ? repo.Head.Tip.Sha : "<empty-tree>");

            using var patch = repo.Diff.Compare<Patch>(headTree, DiffTargets.WorkingDirectory);

            cancellationToken.ThrowIfCancellationRequested();

            var fileDeltas = new List<GitFileDelta>();

            foreach (var entry in patch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Status == ChangeKind.Unmodified || entry.Status == ChangeKind.Ignored)
                {
                    continue;
                }

                var filePath = !string.IsNullOrWhiteSpace(entry.Path) ? entry.Path : entry.OldPath;
                filePath = filePath?.Replace('\\', '/') ?? string.Empty;

                var changeType = entry.Status switch
                {
                    ChangeKind.Added => GitChangeType.Added,
                    ChangeKind.Untracked => GitChangeType.Added,
                    ChangeKind.Deleted => GitChangeType.Deleted,
                    ChangeKind.Renamed => GitChangeType.Renamed,
                    ChangeKind.Copied => GitChangeType.Added,
                    ChangeKind.TypeChanged => GitChangeType.Modified,
                    _ => GitChangeType.Modified
                };

                var changedRanges = ExtractChangedRanges(entry);
                var patchContent = entry.Patch ?? string.Empty;

                fileDeltas.Add(new GitFileDelta(
                    FilePath: filePath,
                    ChangeType: changeType,
                    ChangedRanges: changedRanges,
                    PatchContent: patchContent
                ));
            }

            var combinedDiff = patch.Content ?? string.Empty;

            _logger?.LogInformation(
                "Calculated Git delta report for {RepositoryPath}: {FileCount} changed files",
                repositoryPath,
                fileDeltas.Count);

            return new GitDeltaReport(
                FileDeltas: fileDeltas,
                CombinedDiff: combinedDiff
            );
        }, cancellationToken);
    }

    private static IReadOnlyList<LineRange> ExtractChangedRanges(PatchEntryChanges entry)
    {
        if (entry.IsBinaryComparison)
        {
            return Array.Empty<LineRange>();
        }

        if (string.IsNullOrWhiteSpace(entry.Patch))
        {
            if (entry.AddedLines.Count > 0)
            {
                return CollapseToRanges(entry.AddedLines.Select(l => l.LineNumber));
            }
            if (entry.DeletedLines.Count > 0)
            {
                return CollapseToRanges(entry.DeletedLines.Select(l => l.LineNumber));
            }
            return Array.Empty<LineRange>();
        }

        var lines = entry.Patch.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var allChangedLineNumbers = new List<int>();

        int currentOldLine = 0;
        int currentNewLine = 0;
        bool inHunk = false;
        var currentHunkAddedLines = new List<int>();
        var currentHunkDeletedLines = new List<int>();

        void FlushHunk()
        {
            if (currentHunkAddedLines.Count > 0)
            {
                allChangedLineNumbers.AddRange(currentHunkAddedLines);
            }
            else if (currentHunkDeletedLines.Count > 0)
            {
                allChangedLineNumbers.AddRange(currentHunkDeletedLines);
            }
            currentHunkAddedLines.Clear();
            currentHunkDeletedLines.Clear();
        }

        foreach (var line in lines)
        {
            var match = HunkHeaderRegex.Match(line);
            if (match.Success)
            {
                FlushHunk();
                inHunk = true;
                currentOldLine = int.Parse(match.Groups["oldStart"].Value);
                currentNewLine = int.Parse(match.Groups["newStart"].Value);
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            {
                currentHunkAddedLines.Add(currentNewLine);
                currentNewLine++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            {
                currentHunkDeletedLines.Add(currentOldLine);
                currentOldLine++;
            }
            else if (line.StartsWith(' '))
            {
                currentOldLine++;
                currentNewLine++;
            }
        }

        FlushHunk();

        if (allChangedLineNumbers.Count == 0)
        {
            if (entry.AddedLines.Count > 0)
            {
                return CollapseToRanges(entry.AddedLines.Select(l => l.LineNumber));
            }
            if (entry.DeletedLines.Count > 0)
            {
                return CollapseToRanges(entry.DeletedLines.Select(l => l.LineNumber));
            }
        }

        return CollapseToRanges(allChangedLineNumbers);
    }

    private static List<LineRange> CollapseToRanges(IEnumerable<int> lineNumbers)
    {
        var sorted = lineNumbers
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();

        var ranges = new List<LineRange>();
        if (sorted.Count == 0)
        {
            return ranges;
        }

        int start = sorted[0];
        int end = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == end + 1)
            {
                end = sorted[i];
            }
            else
            {
                ranges.Add(new LineRange(start, end));
                start = sorted[i];
                end = sorted[i];
            }
        }

        ranges.Add(new LineRange(start, end));
        return ranges;
    }
}
