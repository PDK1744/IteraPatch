namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface IPatchValidator
{
    PatchValidationResult Validate(LlmPatchResponse response, GitDeltaReport gitDelta);
}
