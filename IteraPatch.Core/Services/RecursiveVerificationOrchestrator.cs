namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates the recursive self-improvement verification loop
/// (Run -> Parse -> Request Patch -> Validate -> Apply -> Re-test)
/// with a strict circuit breaker terminating execution after maximum retries.
/// </summary>
public sealed class RecursiveVerificationOrchestrator : IOrchestratorService
{
    private readonly ISandboxService _sandboxService;
    private readonly IGitDeltaService _gitDeltaService;
    private readonly ITraceParser _traceParser;
    private readonly ILlmClient _llmClient;
    private readonly IPatchValidator _patchValidator;
    private readonly IPatchApplier _patchApplier;
    private readonly OrchestratorOptions? _defaultOptions;
    private readonly ILogger<RecursiveVerificationOrchestrator>? _logger;

    public RecursiveVerificationOrchestrator(
        ISandboxService sandboxService,
        IGitDeltaService gitDeltaService,
        ITraceParser traceParser,
        ILlmClient llmClient,
        IPatchValidator patchValidator,
        IPatchApplier patchApplier)
        : this(sandboxService, gitDeltaService, traceParser, llmClient, patchValidator, patchApplier, null, null)
    {
    }

    public RecursiveVerificationOrchestrator(
        ISandboxService sandboxService,
        IGitDeltaService gitDeltaService,
        ITraceParser traceParser,
        ILlmClient llmClient,
        IPatchValidator patchValidator,
        IPatchApplier patchApplier,
        ILogger<RecursiveVerificationOrchestrator>? logger)
        : this(sandboxService, gitDeltaService, traceParser, llmClient, patchValidator, patchApplier, null, logger)
    {
    }

    public RecursiveVerificationOrchestrator(
        ISandboxService sandboxService,
        IGitDeltaService gitDeltaService,
        ITraceParser traceParser,
        ILlmClient llmClient,
        IPatchValidator patchValidator,
        IPatchApplier patchApplier,
        OrchestratorOptions? defaultOptions,
        ILogger<RecursiveVerificationOrchestrator>? logger = null)
    {
        _sandboxService = sandboxService ?? throw new ArgumentNullException(nameof(sandboxService));
        _gitDeltaService = gitDeltaService ?? throw new ArgumentNullException(nameof(gitDeltaService));
        _traceParser = traceParser ?? throw new ArgumentNullException(nameof(traceParser));
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _patchValidator = patchValidator ?? throw new ArgumentNullException(nameof(patchValidator));
        _patchApplier = patchApplier ?? throw new ArgumentNullException(nameof(patchApplier));
        _defaultOptions = defaultOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OrchestratorResult> RunVerificationLoopAsync(
        OrchestratorOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveOptions = options ?? _defaultOptions ?? new OrchestratorOptions();

        if (effectiveOptions.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxAttempts),
                effectiveOptions.MaxAttempts,
                "MaxAttempts must be at least 1.");
        }

        var repositoryPath = string.IsNullOrWhiteSpace(effectiveOptions.RepositoryPath)
            ? "."
            : effectiveOptions.RepositoryPath;

        var testCommand = string.IsNullOrWhiteSpace(effectiveOptions.TestCommand)
            ? "pytest"
            : effectiveOptions.TestCommand;

        var maxAttempts = effectiveOptions.MaxAttempts;
        var history = new List<IterationStep>();

        _logger?.LogInformation(
            "Starting verification loop for repository '{RepositoryPath}' with test command '{TestCommand}' (MaxAttempts: {MaxAttempts}).",
            repositoryPath,
            testCommand,
            maxAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger?.LogInformation("--- Verification Iteration {Attempt}/{MaxAttempts} ---", attempt, maxAttempts);

            // 1. Run tests inside isolated sandbox
            var (command, args) = ParseCommand(testCommand);
            var executionRequest = new SandboxExecutionRequest(
                WorkingDirectory: repositoryPath,
                Command: command,
                Arguments: args
            );

            var executionResult = await _sandboxService.ExecuteAsync(executionRequest, cancellationToken);

            // 2. Extract Git delta and parse telemetry traces
            var deltaReport = await _gitDeltaService.GetWorkingTreeChangesAsync(repositoryPath, cancellationToken);
            var gitDelta = deltaReport ?? new GitDeltaReport(Array.Empty<GitFileDelta>(), string.Empty);
            var traceResult = _traceParser.Parse(
                executionResult.StandardOutput,
                executionResult.StandardError,
                gitDelta);

            bool isTestPassing = executionResult.IsSuccess && !traceResult.HasFailure;

            if (isTestPassing)
            {
                var successStep = new IterationStep(
                    Attempt: attempt,
                    ExecutionResult: executionResult,
                    TraceResult: traceResult,
                    PatchResponse: null,
                    PatchApplied: false
                );
                history.Add(successStep);

                _logger?.LogInformation("Verification SUCCESS: All tests passed on attempt {Attempt}.", attempt);

                return new OrchestratorResult(
                    Success: true,
                    TotalAttempts: attempt,
                    History: history,
                    Message: $"Verification succeeded on attempt {attempt}."
                );
            }

            _logger?.LogWarning(
                "Iteration {Attempt} test execution failed (ExitCode: {ExitCode}, TimedOut: {TimedOut}, HasFailure: {HasFailure}).",
                attempt,
                executionResult.ExitCode,
                executionResult.TimedOut,
                traceResult.HasFailure);

            // Hard circuit breaker check: stop if maximum attempts reached
            if (attempt >= maxAttempts)
            {
                var failureStep = new IterationStep(
                    Attempt: attempt,
                    ExecutionResult: executionResult,
                    TraceResult: traceResult,
                    PatchResponse: null,
                    PatchApplied: false
                );
                history.Add(failureStep);

                _logger?.LogError(
                    "Circuit breaker tripped: verification failed after {TotalAttempts} attempt(s). Halting loop.",
                    attempt);

                return new OrchestratorResult(
                    Success: false,
                    TotalAttempts: attempt,
                    History: history,
                    Message: $"Circuit breaker tripped: verification failed after {attempt} attempt(s)."
                );
            }

            // 3. Construct diagnostic ErrorPayload for LLM
            var primaryFailure = traceResult.Failures.FirstOrDefault(f => f.IsWithinGitDelta)
                ?? traceResult.Failures.FirstOrDefault();

            var errorType = primaryFailure?.ErrorType
                ?? (executionResult.TimedOut ? "Timeout" : "ExecutionFailure");
            var errorMessage = primaryFailure?.ErrorMessage
                ?? (executionResult.TimedOut ? "Execution timed out" : $"Test command failed with exit code {executionResult.ExitCode}");
            var stackTrace = primaryFailure?.StackTrace
                ?? (!string.IsNullOrWhiteSpace(executionResult.StandardError) ? executionResult.StandardError : executionResult.StandardOutput);
            var failingFile = primaryFailure?.FailingFile ?? string.Empty;
            var failingLine = primaryFailure?.FailingLine ?? 0;
            var gitDiff = gitDelta.CombinedDiff ?? string.Empty;

            var payload = new ErrorPayload(
                ErrorType: errorType,
                ErrorMessage: errorMessage,
                StackTrace: stackTrace,
                FailingFile: failingFile,
                FailingLine: failingLine,
                GitDiff: gitDiff,
                TestCommand: testCommand,
                Iteration: attempt
            );

            // 4. Request patch from LLM
            _logger?.LogInformation("Requesting patch suggestion from LLM for iteration {Attempt}...", attempt);
            var patchResponse = await _llmClient.RequestPatchAsync(payload, cancellationToken);

            bool patchApplied = false;

            if (patchResponse != null && patchResponse.Patches != null && patchResponse.Patches.Count > 0)
            {
                // 5. Validate patch against blast-radius safety boundaries
                var validation = _patchValidator.Validate(patchResponse, gitDelta);
                if (validation.IsValid)
                {
                    // 6. Apply patch
                    _logger?.LogInformation("Patch validated successfully. Applying patch to working tree...");
                    var applyResult = await _patchApplier.ApplyPatchAsync(repositoryPath, patchResponse, cancellationToken);
                    patchApplied = applyResult.Success;

                    if (patchApplied)
                    {
                        _logger?.LogInformation(
                            "Patch successfully applied to {FileCount} file(s): {Files}",
                            applyResult.ModifiedFiles.Count,
                            string.Join(", ", applyResult.ModifiedFiles));
                    }
                    else
                    {
                        _logger?.LogWarning("Failed to apply patch: {ErrorMessage}", applyResult.ErrorMessage);
                    }
                }
                else
                {
                    _logger?.LogWarning(
                        "Patch rejected by blast radius validator: {RejectionReason}",
                        validation.RejectionReason);
                }
            }
            else
            {
                _logger?.LogWarning("LLM returned no patch suggestions for iteration {Attempt}.", attempt);
            }

            history.Add(new IterationStep(
                Attempt: attempt,
                ExecutionResult: executionResult,
                TraceResult: traceResult,
                PatchResponse: patchResponse,
                PatchApplied: patchApplied
            ));
        }

        return new OrchestratorResult(
            Success: false,
            TotalAttempts: history.Count,
            History: history,
            Message: $"Verification terminated after {history.Count} attempt(s)."
        );
    }

    /// <summary>
    /// Parses a test command string into executable name and argument tokens.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Arguments) ParseCommand(string testCommand)
    {
        if (string.IsNullOrWhiteSpace(testCommand))
        {
            return ("pytest", Array.Empty<string>());
        }

        var tokens = TokenizeCommandLine(testCommand);
        if (tokens.Count == 0)
        {
            return ("pytest", Array.Empty<string>());
        }

        var command = tokens[0];
        var args = tokens.Skip(1).ToArray();
        return (command, args);
    }

    /// <summary>
    /// Tokenizes a command line string respecting single and double quotes.
    /// </summary>
    public static List<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return tokens;
        }

        var current = new StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
