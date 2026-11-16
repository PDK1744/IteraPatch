namespace IteraPatch.Tests;

using System.Text.Json;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Models;

public class ModelsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [Fact]
    public void SandboxExecutionRequest_DefaultValuesAndImmutability()
    {
        var request = new SandboxExecutionRequest(
            WorkingDirectory: "/app",
            Command: "pytest",
            Arguments: new[] { "-v", "tests/" }
        );

        Assert.Equal("/app", request.WorkingDirectory);
        Assert.Equal("pytest", request.Command);
        Assert.Equal(2, request.Arguments.Count);
        Assert.Null(request.EnvironmentVariables);
        Assert.Equal(60, request.TimeoutSeconds);

        var modified = request with { TimeoutSeconds = 120 };
        Assert.Equal(120, modified.TimeoutSeconds);
        Assert.Equal(60, request.TimeoutSeconds);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(137, true, false)]
    public void SandboxExecutionResult_IsSuccessLogic(int exitCode, bool timedOut, bool expectedSuccess)
    {
        var result = new SandboxExecutionResult(
            ExitCode: exitCode,
            StandardOutput: "out",
            StandardError: "err",
            Duration: TimeSpan.FromSeconds(2),
            TimedOut: timedOut
        );

        Assert.Equal(expectedSuccess, result.IsSuccess);
    }

    [Fact]
    public void SandboxModels_SerializationRoundtrip()
    {
        var request = new SandboxExecutionRequest(
            WorkingDirectory: "/app",
            Command: "pytest",
            Arguments: new[] { "-v" },
            EnvironmentVariables: new Dictionary<string, string> { ["ENV"] = "test" },
            TimeoutSeconds: 30
        );

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SandboxExecutionRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(request.WorkingDirectory, deserialized.WorkingDirectory);
        Assert.Equal(request.Command, deserialized.Command);
        Assert.Equal(request.Arguments, deserialized.Arguments);
        Assert.Equal("test", deserialized.EnvironmentVariables?["ENV"]);
        Assert.Equal(30, deserialized.TimeoutSeconds);
    }

    [Fact]
    public void GitDeltaReport_HasChangesLogic()
    {
        var emptyReport = new GitDeltaReport(
            FileDeltas: Array.Empty<GitFileDelta>(),
            CombinedDiff: string.Empty
        );
        Assert.False(emptyReport.HasChanges);

        var reportWithChanges = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/main.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 20) },
                    PatchContent: "@@ -10,5 +10,5 @@"
                )
            },
            CombinedDiff: "diff --git..."
        );
        Assert.True(reportWithChanges.HasChanges);
    }

    [Fact]
    public void GitModels_SerializationRoundtrip()
    {
        var delta = new GitFileDelta(
            FilePath: "src/foo.py",
            ChangeType: GitChangeType.Added,
            ChangedRanges: new[] { new LineRange(1, 15) },
            PatchContent: "new content"
        );
        var report = new GitDeltaReport(new[] { delta }, "diff header");

        var json = JsonSerializer.Serialize(report, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<GitDeltaReport>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.FileDeltas);
        Assert.Equal("src/foo.py", deserialized.FileDeltas[0].FilePath);
        Assert.Equal(GitChangeType.Added, deserialized.FileDeltas[0].ChangeType);
        Assert.Equal(1, deserialized.FileDeltas[0].ChangedRanges[0].StartLine);
        Assert.Equal(15, deserialized.FileDeltas[0].ChangedRanges[0].EndLine);
    }

    [Fact]
    public void TelemetryModels_SerializationRoundtrip()
    {
        var failure = new ParsedFailure(
            FailingFile: "tests/test_foo.py",
            FailingLine: 42,
            ErrorType: "AssertionError",
            ErrorMessage: "Expected 1 but got 2",
            StackTrace: "Traceback ...",
            IsWithinGitDelta: true
        );

        var traceResult = new TraceParseResult(
            HasFailure: true,
            Failures: new[] { failure },
            RawOutput: "test output"
        );

        var json = JsonSerializer.Serialize(traceResult, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TraceParseResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.HasFailure);
        Assert.Single(deserialized.Failures);
        Assert.Equal("tests/test_foo.py", deserialized.Failures[0].FailingFile);
        Assert.Equal(42, deserialized.Failures[0].FailingLine);
        Assert.Equal("AssertionError", deserialized.Failures[0].ErrorType);
        Assert.True(deserialized.Failures[0].IsWithinGitDelta);
    }

    [Fact]
    public void ErrorPayload_MatchesContractSchemaInSpec()
    {
        // JSON matching Section 2.4.1
        var rawJson = """
        {
          "errorType": "AssertionError",
          "errorMessage": "assert 4 == 5",
          "stackTrace": "Traceback (most recent call last):\n  File \"tests/test_math.py\", line 12, in test_add\n    assert add(2, 2) == 5\nAssertionError",
          "failingFile": "src/math.py",
          "failingLine": 4,
          "gitDiff": "diff --git a/src/math.py b/src/math.py\n--- a/src/math.py\n+++ b/src/math.py\n@@ -3,2 +3,2 @@\n def add(a, b):\n-    return a + b + 1\n+    return a + b",
          "testCommand": "pytest",
          "iteration": 1
        }
        """;

        var payload = JsonSerializer.Deserialize<ErrorPayload>(rawJson, JsonOptions);

        Assert.NotNull(payload);
        Assert.Equal("AssertionError", payload.ErrorType);
        Assert.Equal("assert 4 == 5", payload.ErrorMessage);
        Assert.Contains("test_add", payload.StackTrace);
        Assert.Equal("src/math.py", payload.FailingFile);
        Assert.Equal(4, payload.FailingLine);
        Assert.Contains("diff --git", payload.GitDiff);
        Assert.Equal("pytest", payload.TestCommand);
        Assert.Equal(1, payload.Iteration);
    }

    [Fact]
    public void LlmPatchResponse_MatchesContractSchemaInSpec()
    {
        // JSON matching Section 2.4.2
        var rawJson = """
        {
          "explanation": "Removed extraneous + 1 in add function to satisfy test assertions.",
          "patches": [
            {
              "filePath": "src/math.py",
              "searchSnippet": "def add(a, b):\n    return a + b + 1",
              "replaceSnippet": "def add(a, b):\n    return a + b"
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<LlmPatchResponse>(rawJson, JsonOptions);

        Assert.NotNull(response);
        Assert.Equal("Removed extraneous + 1 in add function to satisfy test assertions.", response.Explanation);
        Assert.Single(response.Patches);
        Assert.Equal("src/math.py", response.Patches[0].FilePath);
        Assert.Equal("def add(a, b):\n    return a + b + 1", response.Patches[0].SearchSnippet);
        Assert.Equal("def add(a, b):\n    return a + b", response.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public void PatchValidationAndApplyResult_PropertiesAndRoundtrip()
    {
        var valResult = new PatchValidationResult(
            IsValid: true,
            RejectionReason: null,
            ValidatedFiles: new[] { "src/math.py" }
        );
        Assert.True(valResult.IsValid);
        Assert.Null(valResult.RejectionReason);

        var jsonVal = JsonSerializer.Serialize(valResult, JsonOptions);
        var desVal = JsonSerializer.Deserialize<PatchValidationResult>(jsonVal, JsonOptions);
        Assert.NotNull(desVal);
        Assert.True(desVal.IsValid);
        Assert.Equal("src/math.py", desVal.ValidatedFiles[0]);

        var applyResult = new PatchApplyResult(
            Success: false,
            ModifiedFiles: Array.Empty<string>(),
            ErrorMessage: "Failed to locate search snippet."
        );
        Assert.False(applyResult.Success);
        Assert.Equal("Failed to locate search snippet.", applyResult.ErrorMessage);
    }

    [Fact]
    public void OrchestratorModels_Roundtrip()
    {
        var execResult = new SandboxExecutionResult(0, "ok", "", TimeSpan.FromMilliseconds(500), false);
        var traceResult = new TraceParseResult(false, Array.Empty<ParsedFailure>(), "ok");
        var step = new IterationStep(
            Attempt: 1,
            ExecutionResult: execResult,
            TraceResult: traceResult,
            PatchResponse: null,
            PatchApplied: false
        );

        var orchestratorResult = new OrchestratorResult(
            Success: true,
            TotalAttempts: 1,
            History: new[] { step },
            Message: "Verification succeeded."
        );

        var json = JsonSerializer.Serialize(orchestratorResult, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<OrchestratorResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal(1, deserialized.TotalAttempts);
        Assert.Single(deserialized.History);
        Assert.Equal(1, deserialized.History[0].Attempt);
        Assert.True(deserialized.History[0].ExecutionResult.IsSuccess);
    }

    [Fact]
    public void ConfigurationClasses_DefaultValues()
    {
        var sandbox = new SandboxOptions();
        Assert.Equal("python:3.11-slim", sandbox.Image);
        Assert.Equal(60, sandbox.TimeoutSeconds);
        Assert.Equal(512 * 1024 * 1024, sandbox.MemoryLimitBytes);
        Assert.Equal("/workspace", sandbox.WorkingDirectory);

        var llm = new LlmOptions();
        Assert.Equal("http://localhost:11434/v1", llm.BaseUrl);
        Assert.Equal("dummy-key", llm.ApiKey);
        Assert.Equal("gpt-4o-mini", llm.Model);
        Assert.Equal(0.0, llm.Temperature);
        Assert.Equal(60, llm.TimeoutSeconds);

        var orchestrator = new OrchestratorOptions();
        Assert.Equal(3, orchestrator.MaxAttempts);
        Assert.Equal("pytest", orchestrator.TestCommand);
        Assert.Equal(".", orchestrator.RepositoryPath);
    }
}
