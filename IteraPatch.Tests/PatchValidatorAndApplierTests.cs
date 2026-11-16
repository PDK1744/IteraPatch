namespace IteraPatch.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class PatchValidatorAndApplierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BlastRadiusPatchValidator _validator;
    private readonly FilePatchApplier _applier;

    public PatchValidatorAndApplierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "iterapatch_patch_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _validator = new BlastRadiusPatchValidator(_tempDir);
        _applier = new FilePatchApplier();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region IPatchValidator Tests

    [Fact]
    public void Validator_NullArguments_ThrowsArgumentNullException()
    {
        var response = new LlmPatchResponse("Fix", Array.Empty<FilePatchSuggestion>());
        var delta = new GitDeltaReport(Array.Empty<GitFileDelta>(), "");

        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null!, delta));
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(response, null!));
    }

    [Fact]
    public void Validator_EmptyPatches_ReturnsInvalid()
    {
        var response = new LlmPatchResponse("No patches", Array.Empty<FilePatchSuggestion>());
        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("src/math.py", GitChangeType.Modified, new[] { new LineRange(1, 5) }, "") },
            "");

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("No patch suggestions", result.RejectionReason);
        Assert.Empty(result.ValidatedFiles);
    }

    [Fact]
    public void Validator_EmptyGitDelta_ReturnsInvalid()
    {
        var response = new LlmPatchResponse("Fix", new[]
        {
            new FilePatchSuggestion("src/math.py", "return 1", "return 2")
        });
        var delta = new GitDeltaReport(Array.Empty<GitFileDelta>(), "");

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("no modified files", result.RejectionReason);
    }

    [Fact]
    public void Validator_UnauthorizedFile_NotInGitDelta_RejectsPatch()
    {
        var filePath = CreateFile("src/clean_file.py", "def clean():\n    return 42\n");

        var response = new LlmPatchResponse("Attempt to edit clean file", new[]
        {
            new FilePatchSuggestion("src/clean_file.py", "return 42", "return 100")
        });

        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("src/other_modified.py", GitChangeType.Modified, new[] { new LineRange(1, 3) }, "") },
            "");

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("Unauthorized file modification", result.RejectionReason);
        Assert.Contains("src/clean_file.py", result.RejectionReason);
        Assert.Empty(result.ValidatedFiles);
    }

    [Fact]
    public void Validator_PathTraversal_RejectsSecurityViolation()
    {
        var response = new LlmPatchResponse("Malicious traversal", new[]
        {
            new FilePatchSuggestion("../outside.py", "secret", "exposed")
        });

        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("../outside.py", GitChangeType.Modified, new[] { new LineRange(1, 2) }, "") },
            "");

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("Security violation", result.RejectionReason);
    }

    [Fact]
    public void Validator_DeletedFileInDelta_RejectsPatch()
    {
        var response = new LlmPatchResponse("Edit deleted", new[]
        {
            new FilePatchSuggestion("src/deleted.py", "code", "replacement")
        });

        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("src/deleted.py", GitChangeType.Deleted, Array.Empty<LineRange>(), "") },
            "");

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("deleted", result.RejectionReason);
    }

    [Fact]
    public void Validator_ValidPatchWithinChangedRanges_ReturnsValidResult()
    {
        var fileContent = """
        def add(a, b):
            return a + b + 1

        def sub(a, b):
            return a - b
        """;
        CreateFile("src/math.py", fileContent);

        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta(
                    "src/math.py",
                    GitChangeType.Modified,
                    new[] { new LineRange(1, 3) },
                    "patch diff")
            },
            "combined diff");

        var response = new LlmPatchResponse("Fix off-by-one", new[]
        {
            new FilePatchSuggestion(
                "src/math.py",
                "def add(a, b):\n    return a + b + 1",
                "def add(a, b):\n    return a + b")
        });

        var result = _validator.Validate(response, delta);

        Assert.True(result.IsValid);
        Assert.Null(result.RejectionReason);
        Assert.Single(result.ValidatedFiles);
        Assert.Equal("src/math.py", result.ValidatedFiles[0]);
    }

    [Fact]
    public void Validator_LineRangeOutsideGitDelta_RejectsPatch()
    {
        var fileContent = """
        def add(a, b):
            return a + b + 1

        def sub(a, b):
            return a - b

        def mul(a, b):
            return a * b
        """;
        CreateFile("src/math.py", fileContent);

        // Only lines 1-3 were modified in the Git delta
        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta(
                    "src/math.py",
                    GitChangeType.Modified,
                    new[] { new LineRange(1, 3) },
                    "patch diff")
            },
            "combined diff");

        // The LLM attempts to patch mul(a, b) at lines 7-8
        var response = new LlmPatchResponse("Rewrite mul instead of add", new[]
        {
            new FilePatchSuggestion(
                "src/math.py",
                "def mul(a, b):\n    return a * b",
                "def mul(a, b):\n    return a * b * 1")
        });

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("falls outside the allowed Git delta changed ranges", result.RejectionReason);
        Assert.Empty(result.ValidatedFiles);
    }

    [Fact]
    public void Validator_MultiplePatches_OneOutsideBlastRadius_RejectsAll()
    {
        CreateFile("src/math.py", "def add(a, b):\n    return a + b + 1\n");
        CreateFile("src/auth.py", "def check():\n    return False\n");

        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta(
                    "src/math.py",
                    GitChangeType.Modified,
                    new[] { new LineRange(1, 2) },
                    "patch diff")
            },
            "combined diff");

        var response = new LlmPatchResponse("Fix math and modify clean auth", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/auth.py", "return False", "return True")
        });

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("src/auth.py", result.RejectionReason);
        Assert.Empty(result.ValidatedFiles);
    }

    [Fact]
    public void Validator_MultiplePatches_AllValid_ReturnsValidResult()
    {
        CreateFile("src/math.py", "def add(a, b):\n    return a + b + 1\n");
        CreateFile("src/utils.py", "def fmt(x):\n    return str(x) + ' '\n");

        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta("src/math.py", GitChangeType.Modified, new[] { new LineRange(1, 2) }, ""),
                new GitFileDelta("src/utils.py", GitChangeType.Modified, new[] { new LineRange(1, 2) }, "")
            },
            "");

        var response = new LlmPatchResponse("Fix both files", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/utils.py", "return str(x) + ' '", "return str(x)")
        });

        var result = _validator.Validate(response, delta);

        Assert.True(result.IsValid);
        Assert.Null(result.RejectionReason);
        Assert.Equal(2, result.ValidatedFiles.Count);
        Assert.Contains("src/math.py", result.ValidatedFiles);
        Assert.Contains("src/utils.py", result.ValidatedFiles);
    }

    [Fact]
    public void Validator_SnippetNotFoundInFileOnDisk_RejectsPatch()
    {
        CreateFile("src/math.py", "def add(a, b):\n    return a + b\n");

        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("src/math.py", GitChangeType.Modified, new[] { new LineRange(1, 2) }, "") },
            "");

        var response = new LlmPatchResponse("Ghost snippet", new[]
        {
            new FilePatchSuggestion("src/math.py", "nonexistent_code()", "fixed_code()")
        });

        var result = _validator.Validate(response, delta);

        Assert.False(result.IsValid);
        Assert.Contains("was not found in the target file on disk", result.RejectionReason);
    }

    [Fact]
    public void Validator_FileNotOnDisk_UsesUnifiedDiffHunksInPatchContent()
    {
        var patchContent = """
        @@ -1,4 +1,4 @@
         def add(a, b):
        -    return a + b
        +    return a + b + 1
        """;

        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta(
                    "src/remote_file.py",
                    GitChangeType.Modified,
                    new[] { new LineRange(1, 3) },
                    patchContent)
            },
            "");

        var response = new LlmPatchResponse("Fix from hunk", new[]
        {
            new FilePatchSuggestion(
                "src/remote_file.py",
                "def add(a, b):\n    return a + b + 1",
                "def add(a, b):\n    return a + b")
        });

        var result = _validator.Validate(response, delta);

        Assert.True(result.IsValid);
        Assert.Single(result.ValidatedFiles);
        Assert.Equal("src/remote_file.py", result.ValidatedFiles[0]);
    }

    [Fact]
    public void Validator_PathNormalization_SupportsBackslashesAndPrefixes()
    {
        CreateFile("src/nested/math.py", "def add(a, b):\n    return a + b + 1\n");

        var delta = new GitDeltaReport(
            new[]
            {
                new GitFileDelta(
                    "src/nested/math.py",
                    GitChangeType.Modified,
                    new[] { new LineRange(1, 2) },
                    "")
            },
            "");

        // Uses Windows backslash and leading ./
        var response = new LlmPatchResponse("Fix backslash", new[]
        {
            new FilePatchSuggestion(
                ".\\src\\nested\\math.py",
                "return a + b + 1",
                "return a + b")
        });

        var result = _validator.Validate(response, delta);

        Assert.True(result.IsValid);
        Assert.Equal("src/nested/math.py", result.ValidatedFiles[0]);
    }

    [Fact]
    public void Validator_ResilientToCrlfAndLfDifferences()
    {
        // File created with CRLF line endings
        var crlfContent = "def add(a, b):\r\n    return a + b + 1\r\n";
        CreateFile("src/crlf.py", crlfContent);

        var delta = new GitDeltaReport(
            new[] { new GitFileDelta("src/crlf.py", GitChangeType.Modified, new[] { new LineRange(1, 2) }, "") },
            "");

        // Snippet uses LF line endings
        var response = new LlmPatchResponse("Fix CRLF", new[]
        {
            new FilePatchSuggestion(
                "src/crlf.py",
                "def add(a, b):\n    return a + b + 1",
                "def add(a, b):\n    return a + b")
        });

        var result = _validator.Validate(response, delta);

        Assert.True(result.IsValid);
    }

    #endregion

    #region IPatchApplier Tests

    [Fact]
    public async Task Applier_NullOrEmptyArguments_Throws()
    {
        var response = new LlmPatchResponse("Fix", Array.Empty<FilePatchSuggestion>());

        await Assert.ThrowsAsync<ArgumentException>(() => _applier.ApplyPatchAsync("", response));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _applier.ApplyPatchAsync(_tempDir, null!));
    }

    [Fact]
    public async Task Applier_NonExistentRepoPath_ThrowsDirectoryNotFoundException()
    {
        var nonExistentPath = Path.Combine(_tempDir, "non_existent_" + Guid.NewGuid().ToString("N"));
        var response = new LlmPatchResponse("Fix", new[]
        {
            new FilePatchSuggestion("file.py", "old", "new")
        });

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _applier.ApplyPatchAsync(nonExistentPath, response));
    }

    [Fact]
    public async Task Applier_EmptyPatches_ReturnsFailure()
    {
        var response = new LlmPatchResponse("No patches", Array.Empty<FilePatchSuggestion>());

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.False(result.Success);
        Assert.Contains("No patch suggestions", result.ErrorMessage);
        Assert.Empty(result.ModifiedFiles);
    }

    [Fact]
    public async Task Applier_ValidSingleFilePatch_ModifiesFileCorrectly()
    {
        var originalContent = "def add(a, b):\n    return a + b + 1\n";
        var filePath = CreateFile("src/math.py", originalContent);

        var response = new LlmPatchResponse("Fix addition", new[]
        {
            new FilePatchSuggestion(
                "src/math.py",
                "def add(a, b):\n    return a + b + 1",
                "def add(a, b):\n    return a + b")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Single(result.ModifiedFiles);
        Assert.Equal("src/math.py", result.ModifiedFiles[0]);

        var updatedContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal("def add(a, b):\n    return a + b\n", updatedContent);
    }

    [Fact]
    public async Task Applier_ValidMultiFilePatches_ModifiesAllFilesCorrectly()
    {
        var file1 = CreateFile("src/math.py", "def add(a, b):\n    return a + b + 1\n");
        var file2 = CreateFile("src/sub.py", "def sub(a, b):\n    return a - b - 1\n");

        var response = new LlmPatchResponse("Fix both", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/sub.py", "return a - b - 1", "return a - b")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.True(result.Success);
        Assert.Equal(2, result.ModifiedFiles.Count);

        Assert.Equal("def add(a, b):\n    return a + b\n", await File.ReadAllTextAsync(file1));
        Assert.Equal("def sub(a, b):\n    return a - b\n", await File.ReadAllTextAsync(file2));
    }

    [Fact]
    public async Task Applier_MultiplePatchesOnSameFile_AppliesInSequence()
    {
        var content = """
        def add(a, b):
            return a + b + 1

        def sub(a, b):
            return a - b - 1
        """;
        var filePath = CreateFile("src/math.py", content);

        var response = new LlmPatchResponse("Sequential fixes", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/math.py", "return a - b - 1", "return a - b")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.True(result.Success);
        Assert.Single(result.ModifiedFiles);

        var updated = await File.ReadAllTextAsync(filePath);
        Assert.Contains("return a + b", updated);
        Assert.Contains("return a - b", updated);
        Assert.DoesNotContain("+ 1", updated);
        Assert.DoesNotContain("- 1", updated);
    }

    [Fact]
    public async Task Applier_SearchSnippetNotFound_ReturnsFalseAndLeavesFileUnmodified()
    {
        var originalContent = "def add(a, b):\n    return a + b\n";
        var filePath = CreateFile("src/math.py", originalContent);

        var response = new LlmPatchResponse("Nonexistent snippet", new[]
        {
            new FilePatchSuggestion("src/math.py", "def missing():\n    pass", "def replaced():\n    pass")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.False(result.Success);
        Assert.Contains("Search snippet was not found", result.ErrorMessage);
        Assert.Empty(result.ModifiedFiles);

        var currentContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal(originalContent, currentContent);
    }

    [Fact]
    public async Task Applier_AtomicRollback_OneFailureLeavesAllFilesUnmodified()
    {
        var file1Content = "def add(a, b):\n    return a + b + 1\n";
        var file2Content = "def sub(a, b):\n    return a - b\n";

        var file1 = CreateFile("src/math.py", file1Content);
        var file2 = CreateFile("src/sub.py", file2Content);

        // Patch 1 targets valid code in file1, but Patch 2 targets nonexistent code in file2
        var response = new LlmPatchResponse("Partial failure", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/sub.py", "def nonexistent():\n    pass", "def fixed():\n    pass")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.False(result.Success);
        Assert.Contains("Search snippet was not found", result.ErrorMessage);
        Assert.Empty(result.ModifiedFiles);

        // Assert file1 was NEVER modified because atomicity checked in-memory before writing
        Assert.Equal(file1Content, await File.ReadAllTextAsync(file1));
        Assert.Equal(file2Content, await File.ReadAllTextAsync(file2));
    }

    [Fact]
    public async Task Applier_TargetFileDoesNotExist_ReturnsFalseAndLeavesOthersUntouched()
    {
        var file1Content = "def add(a, b):\n    return a + b + 1\n";
        var file1 = CreateFile("src/math.py", file1Content);

        var response = new LlmPatchResponse("Missing file", new[]
        {
            new FilePatchSuggestion("src/math.py", "return a + b + 1", "return a + b"),
            new FilePatchSuggestion("src/nonexistent.py", "foo", "bar")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.ErrorMessage);

        Assert.Equal(file1Content, await File.ReadAllTextAsync(file1));
    }

    [Fact]
    public async Task Applier_PathTraversal_ReturnsFalseWithSecurityViolation()
    {
        CreateFile("src/math.py", "code");

        var response = new LlmPatchResponse("Traversal", new[]
        {
            new FilePatchSuggestion("../../outside.py", "code", "hacked")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.False(result.Success);
        Assert.Contains("Security violation", result.ErrorMessage);
    }

    [Fact]
    public async Task Applier_CancellationRequested_ThrowsOperationCanceledException()
    {
        CreateFile("src/math.py", "def add():\n    return 1\n");

        var response = new LlmPatchResponse("Fix", new[]
        {
            new FilePatchSuggestion("src/math.py", "return 1", "return 2")
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _applier.ApplyPatchAsync(_tempDir, response, cts.Token));
    }

    [Fact]
    public async Task Applier_PreservesCrlfLineEndings()
    {
        var crlfContent = "def add(a, b):\r\n    return a + b + 1\r\n";
        var filePath = CreateFile("src/crlf.py", crlfContent);

        var response = new LlmPatchResponse("Fix CRLF", new[]
        {
            new FilePatchSuggestion("src/crlf.py", "return a + b + 1", "return a + b")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.True(result.Success);

        var updated = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\r\n", updated);
        Assert.Equal("def add(a, b):\r\n    return a + b\r\n", updated);
    }

    [Fact]
    public async Task Applier_PreservesLfLineEndings()
    {
        var lfContent = "def add(a, b):\n    return a + b + 1\n";
        var filePath = CreateFile("src/lf.py", lfContent);

        var response = new LlmPatchResponse("Fix LF", new[]
        {
            new FilePatchSuggestion("src/lf.py", "return a + b + 1", "return a + b")
        });

        var result = await _applier.ApplyPatchAsync(_tempDir, response);

        Assert.True(result.Success);

        var updated = await File.ReadAllTextAsync(filePath);
        Assert.DoesNotContain("\r\n", updated);
        Assert.Equal("def add(a, b):\n    return a + b\n", updated);
    }

    #endregion

    private string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }
}
