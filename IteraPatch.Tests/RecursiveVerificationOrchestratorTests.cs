namespace IteraPatch.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class RecursiveVerificationOrchestratorTests
{
    #region Test Fakes

    private sealed class FakeSandboxService : ISandboxService
    {
        private readonly Queue<SandboxExecutionResult> _results = new();
        public List<SandboxExecutionRequest> ExecutedRequests { get; } = new();

        public FakeSandboxService(params SandboxExecutionResult[] results)
        {
            foreach (var r in results)
            {
                _results.Enqueue(r);
            }
        }

        public void Enqueue(SandboxExecutionResult result) => _results.Enqueue(result);

        public Task<SandboxExecutionResult> ExecuteAsync(
            SandboxExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedRequests.Add(request);

            if (_results.Count > 0)
            {
                return Task.FromResult(_results.Dequeue());
            }

            return Task.FromResult(new SandboxExecutionResult(0, "Pass", "", TimeSpan.FromMilliseconds(50), false));
        }
    }

    private sealed class FakeGitDeltaService : IGitDeltaService
    {
        public GitDeltaReport Report { get; set; } = new(Array.Empty<GitFileDelta>(), "diff --git a/test.py b/test.py");
        public int CallCount { get; private set; }

        public Task<GitDeltaReport> GetWorkingTreeChangesAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(Report);
        }
    }

    private sealed class FakeTraceParser : ITraceParser
    {
        private readonly Queue<TraceParseResult> _results = new();
        public List<(string Stdout, string Stderr, GitDeltaReport GitDelta)> ParseCalls { get; } = new();

        public FakeTraceParser(params TraceParseResult[] results)
        {
            foreach (var r in results)
            {
                _results.Enqueue(r);
            }
        }

        public void Enqueue(TraceParseResult result) => _results.Enqueue(result);

        public TraceParseResult Parse(string stdout, string stderr, GitDeltaReport gitDelta)
        {
            ParseCalls.Add((stdout, stderr, gitDelta));
            if (_results.Count > 0)
            {
                return _results.Dequeue();
            }

            return new TraceParseResult(false, Array.Empty<ParsedFailure>(), stdout);
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly Queue<LlmPatchResponse> _responses = new();
        public List<ErrorPayload> ReceivedPayloads { get; } = new();
        public int CallCount => ReceivedPayloads.Count;

        public FakeLlmClient(params LlmPatchResponse[] responses)
        {
            foreach (var resp in responses)
            {
                _responses.Enqueue(resp);
            }
        }

        public void Enqueue(LlmPatchResponse resp) => _responses.Enqueue(resp);

        public Task<LlmPatchResponse> RequestPatchAsync(
            ErrorPayload payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedPayloads.Add(payload);

            if (_responses.Count > 0)
            {
                return Task.FromResult(_responses.Dequeue());
            }

            return Task.FromResult(new LlmPatchResponse("Fix", Array.Empty<FilePatchSuggestion>()));
        }
    }

    private sealed class FakePatchValidator : IPatchValidator
    {
        private readonly Queue<PatchValidationResult> _results = new();
        public List<(LlmPatchResponse Response, GitDeltaReport Delta)> ValidateCalls { get; } = new();
        public int CallCount => ValidateCalls.Count;

        public FakePatchValidator(params PatchValidationResult[] results)
        {
            foreach (var r in results)
            {
                _results.Enqueue(r);
            }
        }

        public void Enqueue(PatchValidationResult result) => _results.Enqueue(result);

        public PatchValidationResult Validate(LlmPatchResponse response, GitDeltaReport gitDelta)
        {
            ValidateCalls.Add((response, gitDelta));
            if (_results.Count > 0)
            {
                return _results.Dequeue();
            }

            return new PatchValidationResult(true, null, new[] { "test.py" });
        }
    }

    private sealed class FakePatchApplier : IPatchApplier
    {
        private readonly Queue<PatchApplyResult> _results = new();
        public List<(string RepoPath, LlmPatchResponse Response)> ApplyCalls { get; } = new();
        public int CallCount => ApplyCalls.Count;

        public FakePatchApplier(params PatchApplyResult[] results)
        {
            foreach (var r in results)
            {
                _results.Enqueue(r);
            }
        }

        public void Enqueue(PatchApplyResult result) => _results.Enqueue(result);

        public Task<PatchApplyResult> ApplyPatchAsync(
            string repositoryPath,
            LlmPatchResponse response,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls.Add((repositoryPath, response));

            if (_results.Count > 0)
            {
                return Task.FromResult(_results.Dequeue());
            }

            return Task.FromResult(new PatchApplyResult(true, new[] { "test.py" }, null));
        }
    }

    #endregion

    #region Single-Pass Success Tests

    [Fact]
    public async Task SinglePassSuccess_WhenFirstRunSucceeds_ReturnsSuccessWithOneAttempt()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(0, "1 passed in 0.05s", "", TimeSpan.FromMilliseconds(50), false));
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "1 passed in 0.05s"));
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions
        {
            MaxAttempts = 3,
            TestCommand = "pytest",
            RepositoryPath = "/repo"
        };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.TotalAttempts);
        Assert.Single(result.History);
        Assert.Contains("succeeded on attempt 1", result.Message);

        var step = result.History[0];
        Assert.Equal(1, step.Attempt);
        Assert.True(step.ExecutionResult.IsSuccess);
        Assert.False(step.TraceResult.HasFailure);
        Assert.Null(step.PatchResponse);
        Assert.False(step.PatchApplied);

        // Verify downstream services were not invoked
        Assert.Single(sandbox.ExecutedRequests);
        Assert.Equal("pytest", sandbox.ExecutedRequests[0].Command);
        Assert.Equal("/repo", sandbox.ExecutedRequests[0].WorkingDirectory);
        Assert.Equal(0, llmClient.CallCount);
        Assert.Equal(0, validator.CallCount);
        Assert.Equal(0, applier.CallCount);
    }

    #endregion

    #region Multi-Pass Recovery Tests

    [Fact]
    public async Task MultiPassRecovery_WhenFirstRunFailsAndSecondSucceeds_ReturnsSuccessWithTwoAttempts()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAILED test_add - AssertionError: assert 4 == 5", "", TimeSpan.FromMilliseconds(80), false),
            new SandboxExecutionResult(0, "1 passed in 0.04s", "", TimeSpan.FromMilliseconds(40), false));

        var gitDelta = new FakeGitDeltaService
        {
            Report = new GitDeltaReport(
                new[] { new GitFileDelta("math.py", GitChangeType.Modified, new[] { new LineRange(4, 4) }, "+ return a + b") },
                "diff --git a/math.py b/math.py\n--- a/math.py\n+++ b/math.py\n@@ -4,1 +4,1 @@\n- return a + b + 1\n+ return a + b")
        };

        var traceParser = new FakeTraceParser(
            new TraceParseResult(
                true,
                new[]
                {
                    new ParsedFailure("math.py", 4, "AssertionError", "assert 4 == 5", "Traceback...", true)
                },
                "FAILED test_add"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "1 passed in 0.04s"));

        var patchSuggestion = new FilePatchSuggestion("math.py", "return a + b + 1", "return a + b");
        var patchResponse = new LlmPatchResponse("Fix off-by-one error", new[] { patchSuggestion });
        var llmClient = new FakeLlmClient(patchResponse);

        var validator = new FakePatchValidator(new PatchValidationResult(true, null, new[] { "math.py" }));
        var applier = new FakePatchApplier(new PatchApplyResult(true, new[] { "math.py" }, null));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions
        {
            MaxAttempts = 3,
            TestCommand = "pytest -v",
            RepositoryPath = "/repo"
        };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalAttempts);
        Assert.Equal(2, result.History.Count);

        // Step 1: failed, patch requested and applied
        var step1 = result.History[0];
        Assert.Equal(1, step1.Attempt);
        Assert.False(step1.ExecutionResult.IsSuccess);
        Assert.True(step1.TraceResult.HasFailure);
        Assert.NotNull(step1.PatchResponse);
        Assert.True(step1.PatchApplied);

        // Step 2: passed, verification succeeded
        var step2 = result.History[1];
        Assert.Equal(2, step2.Attempt);
        Assert.True(step2.ExecutionResult.IsSuccess);
        Assert.False(step2.TraceResult.HasFailure);
        Assert.Null(step2.PatchResponse);
        Assert.False(step2.PatchApplied);

        // Verify service interactions
        Assert.Equal(2, sandbox.ExecutedRequests.Count);
        Assert.Equal(1, llmClient.CallCount);
        Assert.Equal(1, validator.CallCount);
        Assert.Equal(1, applier.CallCount);

        // Check LLM ErrorPayload
        var payload = llmClient.ReceivedPayloads[0];
        Assert.Equal("AssertionError", payload.ErrorType);
        Assert.Equal("assert 4 == 5", payload.ErrorMessage);
        Assert.Equal("math.py", payload.FailingFile);
        Assert.Equal(4, payload.FailingLine);
        Assert.Equal(1, payload.Iteration);
        Assert.Equal("pytest -v", payload.TestCommand);
    }

    [Fact]
    public async Task MultiPassRecovery_WhenTwoRunsFailAndThirdSucceeds_ReturnsSuccessWithThreeAttempts()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL 1", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(1, "FAIL 2", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(0, "PASS 3", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();

        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("f.py", 10, "Error1", "msg1", "tb1", true) }, "FAIL 1"),
            new TraceParseResult(true, new[] { new ParsedFailure("f.py", 20, "Error2", "msg2", "tb2", true) }, "FAIL 2"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "PASS 3"));

        var patch1 = new LlmPatchResponse("Fix 1", new[] { new FilePatchSuggestion("f.py", "s1", "r1") });
        var patch2 = new LlmPatchResponse("Fix 2", new[] { new FilePatchSuggestion("f.py", "s2", "r2") });
        var llmClient = new FakeLlmClient(patch1, patch2);

        var validator = new FakePatchValidator(
            new PatchValidationResult(true, null, new[] { "f.py" }),
            new PatchValidationResult(true, null, new[] { "f.py" }));

        var applier = new FakePatchApplier(
            new PatchApplyResult(true, new[] { "f.py" }, null),
            new PatchApplyResult(true, new[] { "f.py" }, null));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalAttempts);
        Assert.Equal(3, result.History.Count);
        Assert.True(result.History[0].PatchApplied);
        Assert.True(result.History[1].PatchApplied);
        Assert.False(result.History[2].PatchApplied);

        Assert.Equal(3, sandbox.ExecutedRequests.Count);
        Assert.Equal(2, llmClient.CallCount);
        Assert.Equal(2, validator.CallCount);
        Assert.Equal(2, applier.CallCount);

        Assert.Equal(1, llmClient.ReceivedPayloads[0].Iteration);
        Assert.Equal(2, llmClient.ReceivedPayloads[1].Iteration);
    }

    #endregion

    #region Circuit Breaker Tests

    [Fact]
    public async Task CircuitBreaker_WhenThreeRunsFail_TerminatesAfterExactlyThreeAttempts()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL 1", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(1, "FAIL 2", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(1, "FAIL 3", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();

        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("f.py", 1, "E", "m", "tb", true) }, "FAIL 1"),
            new TraceParseResult(true, new[] { new ParsedFailure("f.py", 1, "E", "m", "tb", true) }, "FAIL 2"),
            new TraceParseResult(true, new[] { new ParsedFailure("f.py", 1, "E", "m", "tb", true) }, "FAIL 3"));

        var patch1 = new LlmPatchResponse("Fix 1", new[] { new FilePatchSuggestion("f.py", "s1", "r1") });
        var patch2 = new LlmPatchResponse("Fix 2", new[] { new FilePatchSuggestion("f.py", "s2", "r2") });
        var llmClient = new FakeLlmClient(patch1, patch2);

        var validator = new FakePatchValidator(
            new PatchValidationResult(true, null, new[] { "f.py" }),
            new PatchValidationResult(true, null, new[] { "f.py" }));

        var applier = new FakePatchApplier(
            new PatchApplyResult(true, new[] { "f.py" }, null),
            new PatchApplyResult(true, new[] { "f.py" }, null));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(3, result.TotalAttempts);
        Assert.Equal(3, result.History.Count);
        Assert.Contains("Circuit breaker tripped", result.Message);

        // Step 1: attempt 1, patch applied
        Assert.Equal(1, result.History[0].Attempt);
        Assert.True(result.History[0].PatchApplied);

        // Step 2: attempt 2, patch applied
        Assert.Equal(2, result.History[1].Attempt);
        Assert.True(result.History[1].PatchApplied);

        // Step 3: attempt 3, hard circuit breaker halted loop, NO LLM or patch on final failed attempt
        Assert.Equal(3, result.History[2].Attempt);
        Assert.Null(result.History[2].PatchResponse);
        Assert.False(result.History[2].PatchApplied);

        // Strictly verify that exactly 3 test executions and 2 LLM calls occurred (preventing infinite spend)
        Assert.Equal(3, sandbox.ExecutedRequests.Count);
        Assert.Equal(2, llmClient.CallCount);
        Assert.Equal(2, validator.CallCount);
        Assert.Equal(2, applier.CallCount);
    }

    [Fact]
    public async Task CircuitBreaker_CustomMaxAttemptsOne_TerminatesImmediatelyOnFirstFailure()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL", "", TimeSpan.FromMilliseconds(50), false));
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("a.py", 1, "E", "m", "tb", false) }, "FAIL"));
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 1 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(1, result.TotalAttempts);
        Assert.Single(result.History);
        Assert.Equal(0, llmClient.CallCount);
        Assert.Equal(0, applier.CallCount);
    }

    [Fact]
    public async Task CircuitBreaker_CustomMaxAttemptsFive_ExecutesUpToFiveAttempts()
    {
        // Arrange
        var sandbox = new FakeSandboxService();
        for (int i = 0; i < 5; i++)
        {
            sandbox.Enqueue(new SandboxExecutionResult(1, $"FAIL {i + 1}", "", TimeSpan.FromMilliseconds(50), false));
        }

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser();
        for (int i = 0; i < 5; i++)
        {
            traceParser.Enqueue(new TraceParseResult(true, new[] { new ParsedFailure("x.py", 1, "E", "m", "tb", false) }, "FAIL"));
        }

        var llmClient = new FakeLlmClient();
        for (int i = 0; i < 4; i++)
        {
            llmClient.Enqueue(new LlmPatchResponse($"Fix {i + 1}", new[] { new FilePatchSuggestion("x.py", "a", "b") }));
        }

        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 5 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(5, result.TotalAttempts);
        Assert.Equal(5, result.History.Count);
        Assert.Equal(5, sandbox.ExecutedRequests.Count);
        Assert.Equal(4, llmClient.CallCount);
    }

    #endregion

    #region Safety and Resilience Tests

    [Fact]
    public async Task BlastRadiusRejection_WhenValidatorRejectsPatch_DoesNotApplyPatchAndProceeds()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL 1", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(0, "PASS 2", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("clean_file.py", 5, "E", "m", "tb", false) }, "FAIL 1"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "PASS 2"));

        var patch = new LlmPatchResponse("Modify clean file", new[] { new FilePatchSuggestion("clean_file.py", "old", "new") });
        var llmClient = new FakeLlmClient(patch);

        var validator = new FakePatchValidator(new PatchValidationResult(false, "Cannot modify clean file", Array.Empty<string>()));
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalAttempts);
        Assert.False(result.History[0].PatchApplied);
        Assert.NotNull(result.History[0].PatchResponse);
        Assert.Equal(0, applier.CallCount); // Applier was NOT invoked due to validator rejection
    }

    [Fact]
    public async Task PatchApplierFailure_WhenApplierFails_RecordsPatchNotAppliedAndContinues()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL 1", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(0, "PASS 2", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("target.py", 5, "E", "m", "tb", true) }, "FAIL 1"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "PASS 2"));

        var patch = new LlmPatchResponse("Fix", new[] { new FilePatchSuggestion("target.py", "search", "replace") });
        var llmClient = new FakeLlmClient(patch);

        var validator = new FakePatchValidator(new PatchValidationResult(true, null, new[] { "target.py" }));
        var applier = new FakePatchApplier(new PatchApplyResult(false, Array.Empty<string>(), "Snippet not found"));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalAttempts);
        Assert.False(result.History[0].PatchApplied);
        Assert.Equal(1, applier.CallCount);
    }

    [Fact]
    public async Task LlmEmptyResponse_WhenNoPatchesReturned_DoesNotInvokeValidatorOrApplier()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL 1", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(0, "PASS 2", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(true, new[] { new ParsedFailure("x.py", 1, "E", "m", "tb", false) }, "FAIL 1"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "PASS 2"));

        var emptyPatchResponse = new LlmPatchResponse("Cannot determine fix", Array.Empty<FilePatchSuggestion>());
        var llmClient = new FakeLlmClient(emptyPatchResponse);

        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalAttempts);
        Assert.False(result.History[0].PatchApplied);
        Assert.Equal(0, validator.CallCount);
        Assert.Equal(0, applier.CallCount);
    }

    [Fact]
    public async Task TimeoutExecution_GeneratesTimeoutErrorPayload()
    {
        // Arrange
        var timeoutResult = new SandboxExecutionResult(137, "", "Process timed out after 60s", TimeSpan.FromSeconds(60), true);
        var sandbox = new FakeSandboxService(
            timeoutResult,
            new SandboxExecutionResult(0, "PASS", "", TimeSpan.FromSeconds(1), false));

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), ""),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), ""));

        var patch = new LlmPatchResponse("Fix timeout", new[] { new FilePatchSuggestion("code.py", "s", "r") });
        var llmClient = new FakeLlmClient(patch);
        var validator = new FakePatchValidator(new PatchValidationResult(true, null, new[] { "code.py" }));
        var applier = new FakePatchApplier(new PatchApplyResult(true, new[] { "code.py" }, null));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Single(llmClient.ReceivedPayloads);
        var payload = llmClient.ReceivedPayloads[0];
        Assert.Equal("Timeout", payload.ErrorType);
        Assert.Contains("timed out", payload.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitDeltaPrioritization_PrefersFailingFrameWithinGitDelta()
    {
        // Arrange
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(1, "FAIL", "", TimeSpan.FromMilliseconds(50), false),
            new SandboxExecutionResult(0, "PASS", "", TimeSpan.FromMilliseconds(50), false));

        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(
            new TraceParseResult(
                true,
                new[]
                {
                    new ParsedFailure("external_lib.py", 99, "ExternalError", "lib failed", "tb1", false),
                    new ParsedFailure("my_code.py", 42, "TargetError", "targeted error", "tb2", true)
                },
                "FAIL"),
            new TraceParseResult(false, Array.Empty<ParsedFailure>(), "PASS"));

        var patch = new LlmPatchResponse("Fix", new[] { new FilePatchSuggestion("my_code.py", "s", "r") });
        var llmClient = new FakeLlmClient(patch);
        var validator = new FakePatchValidator(new PatchValidationResult(true, null, new[] { "my_code.py" }));
        var applier = new FakePatchApplier(new PatchApplyResult(true, new[] { "my_code.py" }, null));

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = 3 };

        // Act
        var result = await orchestrator.RunVerificationLoopAsync(options);

        // Assert
        Assert.True(result.Success);
        Assert.Single(llmClient.ReceivedPayloads);
        var payload = llmClient.ReceivedPayloads[0];
        Assert.Equal("TargetError", payload.ErrorType);
        Assert.Equal("targeted error", payload.ErrorMessage);
        Assert.Equal("my_code.py", payload.FailingFile);
        Assert.Equal(42, payload.FailingLine);
    }

    #endregion

    #region Command Tokenization and Options Tests

    [Theory]
    [InlineData("pytest", "pytest", 0)]
    [InlineData("pytest -v", "pytest", 1)]
    [InlineData("python -m pytest --maxfail=1", "python", 3)]
    [InlineData("pytest \"tests/my test.py\"", "pytest", 1)]
    [InlineData("pytest 'tests/another test.py' -k math", "pytest", 3)]
    [InlineData("", "pytest", 0)]
    [InlineData("   ", "pytest", 0)]
    public void ParseCommand_CorrectlyExtractsExecutableAndArguments(
        string input,
        string expectedCommand,
        int expectedArgCount)
    {
        var (cmd, args) = RecursiveVerificationOrchestrator.ParseCommand(input);

        Assert.Equal(expectedCommand, cmd);
        Assert.Equal(expectedArgCount, args.Count);
    }

    [Fact]
    public void TokenizeCommandLine_HandlesQuotedSpacesCorrectly()
    {
        var tokens = RecursiveVerificationOrchestrator.TokenizeCommandLine("cmd --path \"foo bar/baz\" -v");

        Assert.Equal(4, tokens.Count);
        Assert.Equal("cmd", tokens[0]);
        Assert.Equal("--path", tokens[1]);
        Assert.Equal("foo bar/baz", tokens[2]);
        Assert.Equal("-v", tokens[3]);
    }

    [Fact]
    public async Task RunVerificationLoopAsync_NullOptions_UsesDefaults()
    {
        var sandbox = new FakeSandboxService(
            new SandboxExecutionResult(0, "OK", "", TimeSpan.FromMilliseconds(10), false));
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser(new TraceParseResult(false, Array.Empty<ParsedFailure>(), "OK"));
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var result = await orchestrator.RunVerificationLoopAsync(null!);

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalAttempts);
        Assert.Equal("pytest", sandbox.ExecutedRequests[0].Command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task RunVerificationLoopAsync_MaxAttemptsLessThanOne_ThrowsArgumentOutOfRangeException(int maxAttempts)
    {
        var sandbox = new FakeSandboxService();
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser();
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        var options = new OrchestratorOptions { MaxAttempts = maxAttempts };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.RunVerificationLoopAsync(options));
    }

    [Fact]
    public async Task RunVerificationLoopAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var sandbox = new FakeSandboxService();
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser();
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        var orchestrator = new RecursiveVerificationOrchestrator(
            sandbox, gitDelta, traceParser, llmClient, validator, applier);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.RunVerificationLoopAsync(new OrchestratorOptions(), cts.Token));
    }

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        var sandbox = new FakeSandboxService();
        var gitDelta = new FakeGitDeltaService();
        var traceParser = new FakeTraceParser();
        var llmClient = new FakeLlmClient();
        var validator = new FakePatchValidator();
        var applier = new FakePatchApplier();

        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(null!, gitDelta, traceParser, llmClient, validator, applier));
        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(sandbox, null!, traceParser, llmClient, validator, applier));
        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(sandbox, gitDelta, null!, llmClient, validator, applier));
        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(sandbox, gitDelta, traceParser, null!, validator, applier));
        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(sandbox, gitDelta, traceParser, llmClient, null!, applier));
        Assert.Throws<ArgumentNullException>(() => new RecursiveVerificationOrchestrator(sandbox, gitDelta, traceParser, llmClient, validator, null!));
    }

    #endregion
}
