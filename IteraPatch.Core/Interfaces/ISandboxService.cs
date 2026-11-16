namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface ISandboxService
{
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default);
}
