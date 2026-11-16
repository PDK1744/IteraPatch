namespace IteraPatch.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class GitDeltaServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibGit2SharpGitDeltaService _service;

    public GitDeltaServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git_delta_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new LibGit2SharpGitDeltaService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // LibGit2 creates read-only files under .git
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private Repository InitRepositoryWithInitialCommit(Action<string, Repository>? setup = null)
    {
        Repository.Init(_tempDir);
        var repo = new Repository(_tempDir);
        var sig = new Signature("Tester", "tester@iterapatch.local", DateTimeOffset.Now);

        setup?.Invoke(_tempDir, repo);

        Commands.Stage(repo, "*");
        if (repo.RetrieveStatus().IsDirty)
        {
            repo.Commit("Initial commit", sig, sig);
        }

        return repo;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWorkingTreeChangesAsync_ThrowsOnNullOrEmptyPath(string? invalidPath)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.GetWorkingTreeChangesAsync(invalidPath!));
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_ThrowsWhenDirectoryDoesNotExist()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _service.GetWorkingTreeChangesAsync(nonExistentPath));
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_ThrowsWhenNotAGitRepository()
    {
        var regularDir = Path.Combine(Path.GetTempPath(), "regular_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(regularDir);
        try
        {
            await Assert.ThrowsAsync<RepositoryNotFoundException>(
                () => _service.GetWorkingTreeChangesAsync(regularDir));
        }
        finally
        {
            Directory.Delete(regularDir, true);
        }
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_ThrowsOnPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.GetWorkingTreeChangesAsync(_tempDir, cts.Token));
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_ReturnsNoChangesWhenClean()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            File.WriteAllText(Path.Combine(dir, "file.txt"), "clean line 1\nclean line 2\n");
        });

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.False(report.HasChanges);
        Assert.Empty(report.FileDeltas);
        Assert.Empty(report.CombinedDiff);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_CalculatesAddedLinesCorrectly()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            File.WriteAllText(Path.Combine(dir, "calc.py"), "def add(a, b):\n    return a + b\n");
        });

        // Append 3 lines (lines 3, 4, 5)
        File.AppendAllText(
            Path.Combine(_tempDir, "calc.py"),
            "\ndef sub(a, b):\n    return a - b\n");

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("calc.py", delta.FilePath);
        Assert.Equal(GitChangeType.Modified, delta.ChangeType);
        Assert.NotEmpty(delta.PatchContent);
        Assert.Contains("+def sub(a, b):", delta.PatchContent);
        Assert.Contains(delta.PatchContent, report.CombinedDiff);

        // Lines 3 to 5 should be the added interval
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(3, delta.ChangedRanges[0].StartLine);
        Assert.Equal(5, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_CalculatesModifiedLinesCorrectly()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            var lines = Enumerable.Range(1, 10).Select(i => $"line {i}\n");
            File.WriteAllText(Path.Combine(dir, "code.py"), string.Concat(lines));
        });

        // Modify lines 4, 5, 6
        var content = File.ReadAllLines(Path.Combine(_tempDir, "code.py"));
        content[3] = "line 4 modified";
        content[4] = "line 5 modified";
        content[5] = "line 6 modified";
        File.WriteAllLines(Path.Combine(_tempDir, "code.py"), content);

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("code.py", delta.FilePath);
        Assert.Equal(GitChangeType.Modified, delta.ChangeType);
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(4, delta.ChangedRanges[0].StartLine);
        Assert.Equal(6, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_CalculatesDeletedLinesCorrectly()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            var lines = Enumerable.Range(1, 10).Select(i => $"line {i}\n");
            File.WriteAllText(Path.Combine(dir, "code.py"), string.Concat(lines));
        });

        // Delete lines 4, 5, 6
        var content = File.ReadAllLines(Path.Combine(_tempDir, "code.py")).ToList();
        content.RemoveRange(3, 3); // removes index 3, 4, 5 (1-based lines 4, 5, 6)
        File.WriteAllLines(Path.Combine(_tempDir, "code.py"), content);

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("code.py", delta.FilePath);
        Assert.Equal(GitChangeType.Modified, delta.ChangeType);
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(4, delta.ChangedRanges[0].StartLine);
        Assert.Equal(6, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_DetectsNewlyAddedUntrackedFile()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            File.WriteAllText(Path.Combine(dir, "initial.txt"), "hello\n");
        });

        // Add brand new untracked file
        var newFile = Path.Combine(_tempDir, "new_module.py");
        File.WriteAllText(newFile, "def foo():\n    return 42\n");

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("new_module.py", delta.FilePath);
        Assert.Equal(GitChangeType.Added, delta.ChangeType);
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(1, delta.ChangedRanges[0].StartLine);
        Assert.Equal(2, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_DetectsDeletedTrackedFile()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep\n");
            File.WriteAllText(Path.Combine(dir, "remove_me.py"), "line 1\nline 2\nline 3\n");
        });

        // Delete remove_me.py from disk
        File.Delete(Path.Combine(_tempDir, "remove_me.py"));

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("remove_me.py", delta.FilePath);
        Assert.Equal(GitChangeType.Deleted, delta.ChangeType);
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(1, delta.ChangedRanges[0].StartLine);
        Assert.Equal(3, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_DetectsStagedChangesRelativeToHead()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            File.WriteAllText(Path.Combine(dir, "staged.txt"), "old staged line\n");
        });

        File.WriteAllText(Path.Combine(_tempDir, "staged.txt"), "new staged line\n");
        Commands.Stage(repo, "staged.txt");

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("staged.txt", delta.FilePath);
        Assert.Equal(GitChangeType.Modified, delta.ChangeType);
        Assert.Single(delta.ChangedRanges);
        Assert.Equal(1, delta.ChangedRanges[0].StartLine);
        Assert.Equal(1, delta.ChangedRanges[0].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_HandlesMultipleHunksInSingleFile()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            var lines = Enumerable.Range(1, 30).Select(i => $"line {i}\n");
            File.WriteAllText(Path.Combine(dir, "large.py"), string.Concat(lines));
        });

        var content = File.ReadAllLines(Path.Combine(_tempDir, "large.py")).ToList();
        // Hunk 1: modify line 2
        content[1] = "line 2 modified";
        // Hunk 2: delete lines 28 and 29
        content.RemoveAt(27);
        content.RemoveAt(27);
        File.WriteAllLines(Path.Combine(_tempDir, "large.py"), content);

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);

        var delta = report.FileDeltas[0];
        Assert.Equal("large.py", delta.FilePath);
        Assert.Equal(GitChangeType.Modified, delta.ChangeType);
        Assert.Equal(2, delta.ChangedRanges.Count);

        Assert.Equal(2, delta.ChangedRanges[0].StartLine);
        Assert.Equal(2, delta.ChangedRanges[0].EndLine);

        Assert.Equal(28, delta.ChangedRanges[1].StartLine);
        Assert.Equal(29, delta.ChangedRanges[1].EndLine);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_HandlesMultipleFilesAndSubdirectories()
    {
        using var repo = InitRepositoryWithInitialCommit((dir, r) =>
        {
            var srcDir = Path.Combine(dir, "src", "sub");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "module.py"), "def run():\n    pass\n");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Title\nInitial\n");
        });

        // Modify file in subdirectory
        var subFile = Path.Combine(_tempDir, "src", "sub", "module.py");
        File.WriteAllText(subFile, "def run():\n    return 'updated'\n");

        // Delete README.md
        File.Delete(Path.Combine(_tempDir, "README.md"));

        // Add new file in root
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), "{\"env\": \"test\"}\n");

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Equal(3, report.FileDeltas.Count);

        var paths = report.FileDeltas.Select(d => d.FilePath).OrderBy(p => p).ToList();
        Assert.Contains("README.md", paths);
        Assert.Contains("config.json", paths);
        Assert.Contains("src/sub/module.py", paths);

        var moduleDelta = report.FileDeltas.Single(d => d.FilePath == "src/sub/module.py");
        Assert.Equal(GitChangeType.Modified, moduleDelta.ChangeType);

        var readmeDelta = report.FileDeltas.Single(d => d.FilePath == "README.md");
        Assert.Equal(GitChangeType.Deleted, readmeDelta.ChangeType);

        var configDelta = report.FileDeltas.Single(d => d.FilePath == "config.json");
        Assert.Equal(GitChangeType.Added, configDelta.ChangeType);

        Assert.Contains("README.md", report.CombinedDiff);
        Assert.Contains("src/sub/module.py", report.CombinedDiff);
        Assert.Contains("config.json", report.CombinedDiff);
    }

    [Fact]
    public async Task GetWorkingTreeChangesAsync_HandlesEmptyRepoWithoutCommits()
    {
        Repository.Init(_tempDir);
        var file = Path.Combine(_tempDir, "initial.txt");
        File.WriteAllText(file, "line 1\nline 2\n");

        var report = await _service.GetWorkingTreeChangesAsync(_tempDir);

        Assert.NotNull(report);
        Assert.True(report.HasChanges);
        Assert.Single(report.FileDeltas);
        Assert.Equal("initial.txt", report.FileDeltas[0].FilePath);
        Assert.Equal(GitChangeType.Added, report.FileDeltas[0].ChangeType);
        Assert.Single(report.FileDeltas[0].ChangedRanges);
        Assert.Equal(1, report.FileDeltas[0].ChangedRanges[0].StartLine);
        Assert.Equal(2, report.FileDeltas[0].ChangedRanges[0].EndLine);
    }
}
