namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;
using IteraPatch.Core.Configuration;

public interface IOrchestratorService
{
    Task<OrchestratorResult> RunVerificationLoopAsync(
        OrchestratorOptions options,
        CancellationToken cancellationToken = default);
}
