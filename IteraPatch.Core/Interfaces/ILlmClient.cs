namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface ILlmClient
{
    Task<LlmPatchResponse> RequestPatchAsync(
        ErrorPayload payload,
        CancellationToken cancellationToken = default);
}
