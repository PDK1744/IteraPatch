namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Executes atomic search-and-replace snippet modifications in the repository working directory.
/// Ensures all-or-nothing modification semantics with automatic rollback on failure.
/// </summary>
public sealed class FilePatchApplier : IPatchApplier
{
    private readonly ILogger<FilePatchApplier>? _logger;

    public FilePatchApplier(ILogger<FilePatchApplier>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PatchApplyResult> ApplyPatchAsync(
        string repositoryPath,
        LlmPatchResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(response);

        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path '{repositoryPath}' does not exist.");
        }

        if (response.Patches == null || response.Patches.Count == 0)
        {
            _logger?.LogWarning("ApplyPatchAsync: No patch suggestions provided to apply.");
            return new PatchApplyResult(
                Success: false,
                ModifiedFiles: Array.Empty<string>(),
                ErrorMessage: "No patch suggestions provided to apply.");
        }

        var fullRepoPath = Path.GetFullPath(repositoryPath);
        var repoPrefix = fullRepoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // Group patches by target file full path to apply sequential modifications in order
        var patchesByFile = new Dictionary<string, (string RelativePath, List<FilePatchSuggestion> Patches)>(StringComparer.OrdinalIgnoreCase);

        foreach (var patch in response.Patches)
        {
            if (string.IsNullOrWhiteSpace(patch.FilePath))
            {
                _logger?.LogWarning("ApplyPatchAsync: Patch suggestion contains empty file path.");
                return new PatchApplyResult(
                    Success: false,
                    ModifiedFiles: Array.Empty<string>(),
                    ErrorMessage: "Patch suggestion contains an empty or whitespace file path.");
            }

            var fullFilePath = Path.GetFullPath(Path.Combine(fullRepoPath, patch.FilePath));
            if (!fullFilePath.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullFilePath, fullRepoPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Security violation: Path '{FilePath}' attempts to escape repository root '{RepoPath}'", patch.FilePath, fullRepoPath);
                return new PatchApplyResult(
                    Success: false,
                    ModifiedFiles: Array.Empty<string>(),
                    ErrorMessage: $"Security violation: File path '{patch.FilePath}' attempts to escape repository root.");
            }

            if (string.IsNullOrEmpty(patch.SearchSnippet))
            {
                _logger?.LogWarning("ApplyPatchAsync: Patch suggestion for '{FilePath}' contains empty search snippet.", patch.FilePath);
                return new PatchApplyResult(
                    Success: false,
                    ModifiedFiles: Array.Empty<string>(),
                    ErrorMessage: $"Patch suggestion for '{patch.FilePath}' contains an empty search snippet.");
            }

            if (!patchesByFile.TryGetValue(fullFilePath, out var fileEntry))
            {
                var normalizedRelPath = patch.FilePath.Replace('\\', '/');
                fileEntry = (normalizedRelPath, new List<FilePatchSuggestion>());
                patchesByFile[fullFilePath] = fileEntry;
            }

            fileEntry.Patches.Add(patch);
        }

        // Phase 1: In-memory atomic staging and verification
        var stagedWrites = new Dictionary<string, (string OriginalContent, string UpdatedContent, string RelativePath)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fullFilePath, fileEntry) in patchesByFile)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(fullFilePath))
            {
                _logger?.LogWarning("ApplyPatchAsync: Target file '{FilePath}' does not exist on disk.", fileEntry.RelativePath);
                return new PatchApplyResult(
                    Success: false,
                    ModifiedFiles: Array.Empty<string>(),
                    ErrorMessage: $"Target file '{fileEntry.RelativePath}' does not exist on disk.");
            }

            var originalContent = await File.ReadAllTextAsync(fullFilePath, cancellationToken);
            var currentContent = originalContent;

            foreach (var patch in fileEntry.Patches)
            {
                var (success, updatedContent, error) = ApplySnippetReplacement(currentContent, patch.SearchSnippet, patch.ReplaceSnippet);
                if (!success)
                {
                    _logger?.LogWarning("ApplyPatchAsync: Snippet replacement failed for '{FilePath}': {Error}", fileEntry.RelativePath, error);
                    return new PatchApplyResult(
                        Success: false,
                        ModifiedFiles: Array.Empty<string>(),
                        ErrorMessage: $"Failed to apply patch to '{fileEntry.RelativePath}': {error}");
                }

                currentContent = updatedContent;
            }

            stagedWrites[fullFilePath] = (OriginalContent: originalContent, UpdatedContent: currentContent, RelativePath: fileEntry.RelativePath);
        }

        // Phase 2: Atomic write commit with rollback on unexpected failure
        var writtenFiles = new List<string>();
        var modifiedRelativePaths = new List<string>();

        try
        {
            foreach (var (fullPath, staged) in stagedWrites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(fullPath, staged.UpdatedContent, cancellationToken);
                writtenFiles.Add(fullPath);
                modifiedRelativePaths.Add(staged.RelativePath);
                _logger?.LogInformation("Successfully wrote patch to '{FilePath}'", staged.RelativePath);
            }

            return new PatchApplyResult(
                Success: true,
                ModifiedFiles: modifiedRelativePaths,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception while writing patched files to disk. Executing atomic rollback.");

            foreach (var writtenPath in writtenFiles)
            {
                try
                {
                    if (stagedWrites.TryGetValue(writtenPath, out var staged))
                    {
                        File.WriteAllText(writtenPath, staged.OriginalContent);
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger?.LogError(rollbackEx, "Failed to rollback '{FilePath}' during atomic abort.", writtenPath);
                }
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new PatchApplyResult(
                Success: false,
                ModifiedFiles: Array.Empty<string>(),
                ErrorMessage: $"Atomic patch apply failed: {ex.Message}");
        }
    }

    private static (bool Success, string Result, string? Error) ApplySnippetReplacement(
        string content,
        string searchSnippet,
        string? replaceSnippet)
    {
        var replace = replaceSnippet ?? string.Empty;
        bool isCrlf = content.Contains("\r\n");

        var normalizedSearch = isCrlf
            ? searchSnippet.Replace("\r\n", "\n").Replace("\n", "\r\n")
            : searchSnippet.Replace("\r\n", "\n");

        var normalizedReplace = isCrlf
            ? replace.Replace("\r\n", "\n").Replace("\n", "\r\n")
            : replace.Replace("\r\n", "\n");

        int matchIndex = content.IndexOf(normalizedSearch, StringComparison.Ordinal);
        if (matchIndex >= 0)
        {
            var updated = content.Substring(0, matchIndex) + normalizedReplace + content.Substring(matchIndex + normalizedSearch.Length);
            return (true, updated, null);
        }

        // Fallback for mixed or non-standard newline conventions
        var lfContent = content.Replace("\r\n", "\n");
        var lfSearch = searchSnippet.Replace("\r\n", "\n");
        var lfReplace = replace.Replace("\r\n", "\n");

        int lfMatchIndex = lfContent.IndexOf(lfSearch, StringComparison.Ordinal);
        if (lfMatchIndex < 0)
        {
            return (false, content, "Search snippet was not found in target file.");
        }

        var lfUpdated = lfContent.Substring(0, lfMatchIndex) + lfReplace + lfContent.Substring(lfMatchIndex + lfSearch.Length);
        var finalContent = isCrlf ? lfUpdated.Replace("\n", "\r\n") : lfUpdated;

        return (true, finalContent, null);
    }
}
