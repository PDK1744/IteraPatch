namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Strictly enforces blast-radius safety by validating that LLM patch suggestions
/// touch ONLY files and line ranges identified in the Git delta report.
/// </summary>
public sealed class BlastRadiusPatchValidator : IPatchValidator
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(?<oldStart>\d+)(?:,\d+)?\s+\+(?<newStart>\d+)(?:,\d+)?\s+@@",
        RegexOptions.Compiled);

    private readonly string? _repositoryPath;
    private readonly ILogger<BlastRadiusPatchValidator>? _logger;

    public BlastRadiusPatchValidator(ILogger<BlastRadiusPatchValidator>? logger = null)
        : this(repositoryPath: (string?)null, logger: logger)
    {
    }

    public BlastRadiusPatchValidator(
        OrchestratorOptions? options,
        ILogger<BlastRadiusPatchValidator>? logger = null)
        : this(options?.RepositoryPath, logger)
    {
    }

    public BlastRadiusPatchValidator(
        string? repositoryPath,
        ILogger<BlastRadiusPatchValidator>? logger = null)
    {
        _repositoryPath = repositoryPath;
        _logger = logger;
    }

    /// <inheritdoc />
    public PatchValidationResult Validate(LlmPatchResponse response, GitDeltaReport gitDelta)
    {
        return Validate(response, gitDelta, _repositoryPath);
    }

    /// <summary>
    /// Validates patch suggestions against Git delta changes with an explicit repository path.
    /// </summary>
    public PatchValidationResult Validate(
        LlmPatchResponse response,
        GitDeltaReport gitDelta,
        string? repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(gitDelta);

        if (response.Patches == null || response.Patches.Count == 0)
        {
            _logger?.LogWarning("Patch validation rejected: No patch suggestions provided in LLM response.");
            return new PatchValidationResult(
                IsValid: false,
                RejectionReason: "No patch suggestions provided in LLM response.",
                ValidatedFiles: Array.Empty<string>());
        }

        if (gitDelta.FileDeltas == null || gitDelta.FileDeltas.Count == 0)
        {
            _logger?.LogWarning("Patch validation rejected: Git delta report contains no modified files.");
            return new PatchValidationResult(
                IsValid: false,
                RejectionReason: "Git delta report contains no modified files.",
                ValidatedFiles: Array.Empty<string>());
        }

        var deltaLookup = new Dictionary<string, GitFileDelta>(StringComparer.OrdinalIgnoreCase);
        foreach (var delta in gitDelta.FileDeltas)
        {
            var norm = NormalizePath(delta.FilePath);
            if (!string.IsNullOrEmpty(norm))
            {
                deltaLookup[norm] = delta;
            }
        }

        var validatedFiles = new List<string>();

        foreach (var patch in response.Patches)
        {
            if (string.IsNullOrWhiteSpace(patch.FilePath))
            {
                _logger?.LogWarning("Patch validation rejected: empty file path in suggestion.");
                return new PatchValidationResult(
                    IsValid: false,
                    RejectionReason: "Patch suggestion contains an empty or whitespace file path.",
                    ValidatedFiles: Array.Empty<string>());
            }

            var normalizedPatchPath = NormalizePath(patch.FilePath);
            if (IsPathTraversal(patch.FilePath) || IsPathTraversal(normalizedPatchPath))
            {
                _logger?.LogWarning("Patch validation rejected: path traversal detected in '{FilePath}'", patch.FilePath);
                return new PatchValidationResult(
                    IsValid: false,
                    RejectionReason: $"Security violation: File path '{patch.FilePath}' attempts path traversal or is an absolute path.",
                    ValidatedFiles: Array.Empty<string>());
            }

            if (string.IsNullOrEmpty(patch.SearchSnippet))
            {
                _logger?.LogWarning("Patch validation rejected: empty search snippet for '{FilePath}'", patch.FilePath);
                return new PatchValidationResult(
                    IsValid: false,
                    RejectionReason: $"Patch suggestion for '{patch.FilePath}' contains an empty search snippet.",
                    ValidatedFiles: Array.Empty<string>());
            }

            if (!deltaLookup.TryGetValue(normalizedPatchPath, out var matchingDelta))
            {
                _logger?.LogWarning(
                    "Patch validation rejected: unauthorized file modification '{FilePath}'. Allowed files: [{AllowedFiles}]",
                    patch.FilePath,
                    string.Join(", ", deltaLookup.Keys));

                return new PatchValidationResult(
                    IsValid: false,
                    RejectionReason: $"Unauthorized file modification: '{patch.FilePath}' is not part of the current Git delta.",
                    ValidatedFiles: Array.Empty<string>());
            }

            if (matchingDelta.ChangeType == GitChangeType.Deleted)
            {
                _logger?.LogWarning("Patch validation rejected: file '{FilePath}' is marked as deleted.", patch.FilePath);
                return new PatchValidationResult(
                    IsValid: false,
                    RejectionReason: $"Cannot patch file '{patch.FilePath}' because it is marked as deleted in the Git delta.",
                    ValidatedFiles: Array.Empty<string>());
            }

            // Line range validation
            var effectiveRepoPath = !string.IsNullOrWhiteSpace(repositoryPath) ? repositoryPath : _repositoryPath;
            var resolvedFilePath = ResolveFileOnDisk(patch.FilePath, normalizedPatchPath, effectiveRepoPath);

            if (resolvedFilePath != null)
            {
                var fileContent = File.ReadAllText(resolvedFilePath);
                var lineRange = FindSnippetLineRange(fileContent, patch.SearchSnippet);
                if (lineRange == null)
                {
                    _logger?.LogWarning("Patch validation rejected: search snippet not found in target file on disk for '{FilePath}'", patch.FilePath);
                    return new PatchValidationResult(
                        IsValid: false,
                        RejectionReason: $"Search snippet for '{patch.FilePath}' was not found in the target file on disk.",
                        ValidatedFiles: Array.Empty<string>());
                }

                if (!IntersectsAnyRange(lineRange, matchingDelta.ChangedRanges))
                {
                    var allowedRanges = FormatRanges(matchingDelta.ChangedRanges);
                    _logger?.LogWarning(
                        "Patch validation rejected: target line range {StartLine}-{EndLine} in '{FilePath}' is outside allowed delta ranges ({AllowedRanges})",
                        lineRange.StartLine,
                        lineRange.EndLine,
                        patch.FilePath,
                        allowedRanges);

                    return new PatchValidationResult(
                        IsValid: false,
                        RejectionReason: $"Patch for '{patch.FilePath}' targets lines {lineRange.StartLine}-{lineRange.EndLine}, which falls outside the allowed Git delta changed ranges ({allowedRanges}).",
                        ValidatedFiles: Array.Empty<string>());
                }
            }
            else
            {
                // File does not exist on disk (e.g. in-memory test or remote delta)
                if (!string.IsNullOrWhiteSpace(matchingDelta.PatchContent))
                {
                    var lineRange = TryExtractLineRangeFromPatchContent(matchingDelta.PatchContent, patch.SearchSnippet);
                    if (lineRange != null)
                    {
                        if (!IntersectsAnyRange(lineRange, matchingDelta.ChangedRanges))
                        {
                            var allowedRanges = FormatRanges(matchingDelta.ChangedRanges);
                            _logger?.LogWarning(
                                "Patch validation rejected from patch content: target line range {StartLine}-{EndLine} in '{FilePath}' is outside allowed delta ranges ({AllowedRanges})",
                                lineRange.StartLine,
                                lineRange.EndLine,
                                patch.FilePath,
                                allowedRanges);

                            return new PatchValidationResult(
                                IsValid: false,
                                RejectionReason: $"Patch for '{patch.FilePath}' targets lines {lineRange.StartLine}-{lineRange.EndLine}, which falls outside the allowed Git delta changed ranges ({allowedRanges}).",
                                ValidatedFiles: Array.Empty<string>());
                        }
                    }
                    else if (!PatchContentContainsSnippet(matchingDelta.PatchContent, patch.SearchSnippet))
                    {
                        _logger?.LogWarning(
                            "Patch validation rejected: snippet for '{FilePath}' was not found in Git delta patch content.",
                            patch.FilePath);

                        return new PatchValidationResult(
                            IsValid: false,
                            RejectionReason: $"Patch for '{patch.FilePath}' targets code not found in the Git delta patch content.",
                            ValidatedFiles: Array.Empty<string>());
                    }
                }
                else
                {
                    _logger?.LogWarning(
                        "Patch validation rejected: cannot verify line range for '{FilePath}' because file is not on disk and patch content is unavailable.",
                        patch.FilePath);

                    return new PatchValidationResult(
                        IsValid: false,
                        RejectionReason: $"Cannot verify line range for '{patch.FilePath}': file does not exist on disk and unified diff content is unavailable.",
                        ValidatedFiles: Array.Empty<string>());
                }
            }

            if (!validatedFiles.Contains(matchingDelta.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                validatedFiles.Add(matchingDelta.FilePath);
            }
        }

        return new PatchValidationResult(
            IsValid: true,
            RejectionReason: null,
            ValidatedFiles: validatedFiles);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
    }

    private static bool IsPathTraversal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var norm = path.Replace('\\', '/');
        return norm.StartsWith("../", StringComparison.Ordinal) ||
               norm.Contains("/../", StringComparison.Ordinal) ||
               norm.EndsWith("/..", StringComparison.Ordinal) ||
               norm.Equals("..", StringComparison.Ordinal) ||
               Path.IsPathRooted(path);
    }

    private static string? ResolveFileOnDisk(string rawPath, string normalizedPath, string? repositoryPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            candidates.Add(Path.Combine(repositoryPath, rawPath));
            candidates.Add(Path.Combine(repositoryPath, normalizedPath));
        }

        candidates.Add(rawPath);
        candidates.Add(normalizedPath);

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore invalid path syntax
            }
        }

        return null;
    }

    private static LineRange? FindSnippetLineRange(string fileContent, string snippet)
    {
        if (string.IsNullOrEmpty(fileContent) || string.IsNullOrEmpty(snippet))
        {
            return null;
        }

        var normalizedFile = fileContent.Replace("\r\n", "\n");
        var normalizedSnippet = snippet.Replace("\r\n", "\n");

        int index = normalizedFile.IndexOf(normalizedSnippet, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        int startLine = 1 + CountNewlines(normalizedFile.AsSpan(0, index));
        int newlineCountInSnippet = CountNewlines(normalizedSnippet);
        int endLine = startLine + newlineCountInSnippet;

        return new LineRange(startLine, endLine);
    }

    private static int CountNewlines(ReadOnlySpan<char> span)
    {
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static bool IntersectsAnyRange(LineRange target, IReadOnlyList<LineRange>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
        {
            return false;
        }

        foreach (var range in ranges)
        {
            if (target.StartLine <= range.EndLine && target.EndLine >= range.StartLine)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatRanges(IReadOnlyList<LineRange>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", ranges.Select(r => $"{r.StartLine}-{r.EndLine}"));
    }

    private static LineRange? TryExtractLineRangeFromPatchContent(string patchContent, string snippet)
    {
        if (string.IsNullOrWhiteSpace(patchContent) || string.IsNullOrWhiteSpace(snippet))
        {
            return null;
        }

        var normalizedSnippet = snippet.Replace("\r\n", "\n").Trim();
        var lines = patchContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var workingCopyLines = new List<(int LineNumber, string LineText)>();
        var deletedLines = new List<(int LineNumber, string LineText)>();

        int currentNewLine = 0;
        int currentOldLine = 0;
        bool inHunk = false;

        foreach (var rawLine in lines)
        {
            var match = HunkHeaderRegex.Match(rawLine);
            if (match.Success)
            {
                inHunk = true;
                currentOldLine = int.Parse(match.Groups["oldStart"].Value);
                currentNewLine = int.Parse(match.Groups["newStart"].Value);
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            if (rawLine.StartsWith('+') && !rawLine.StartsWith("+++"))
            {
                workingCopyLines.Add((currentNewLine, rawLine.Substring(1)));
                currentNewLine++;
            }
            else if (rawLine.StartsWith('-') && !rawLine.StartsWith("---"))
            {
                deletedLines.Add((currentOldLine, rawLine.Substring(1)));
                currentOldLine++;
            }
            else if (rawLine.StartsWith(' ') || (rawLine.Length > 0 && rawLine[0] != '\\'))
            {
                var lineText = rawLine.StartsWith(' ') ? rawLine.Substring(1) : rawLine;
                workingCopyLines.Add((currentNewLine, lineText));
                deletedLines.Add((currentOldLine, lineText));
                currentNewLine++;
                currentOldLine++;
            }
        }

        var range = FindSnippetInHunkLines(workingCopyLines, normalizedSnippet);
        if (range != null)
        {
            return range;
        }

        return FindSnippetInHunkLines(deletedLines, normalizedSnippet);
    }

    private static LineRange? FindSnippetInHunkLines(List<(int LineNumber, string LineText)> hunkLines, string normalizedSnippet)
    {
        if (hunkLines.Count == 0)
        {
            return null;
        }

        var snippetLines = normalizedSnippet.Split('\n');
        for (int i = 0; i <= hunkLines.Count - snippetLines.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < snippetLines.Length; j++)
            {
                if (!string.Equals(hunkLines[i + j].LineText.TrimEnd('\r'), snippetLines[j].TrimEnd('\r'), StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                int start = hunkLines[i].LineNumber;
                int end = hunkLines[i + snippetLines.Length - 1].LineNumber;
                return new LineRange(start, end);
            }
        }

        return null;
    }

    private static bool PatchContentContainsSnippet(string patchContent, string snippet)
    {
        var normalizedPatch = patchContent.Replace("\r\n", "\n");
        var normalizedSnippet = snippet.Replace("\r\n", "\n").Trim();

        if (normalizedPatch.Contains(normalizedSnippet, StringComparison.Ordinal))
        {
            return true;
        }

        var snippetLines = normalizedSnippet.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return snippetLines.Length > 0 && snippetLines.All(l => normalizedPatch.Contains(l.Trim(), StringComparison.Ordinal));
    }
}
