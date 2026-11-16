namespace IteraPatch.Core.Configuration;

public sealed class OrchestratorOptions
{
    public int MaxAttempts { get; set; } = 3;
    public string TestCommand { get; set; } = "pytest";
    public string RepositoryPath { get; set; } = ".";
}
