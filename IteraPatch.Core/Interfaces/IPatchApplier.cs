namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface IPatchApplier
{
    Task<PatchApplyResult> ApplyPatchAsync(
        string repositoryPath,
        LlmPatchResponse response,
        CancellationToken cancellationToken = default);
}
