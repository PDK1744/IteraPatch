namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface IGitDeltaService
{
    Task<GitDeltaReport> GetWorkingTreeChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
