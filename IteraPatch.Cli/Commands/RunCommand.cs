namespace IteraPatch.Cli.Commands;

using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Extensions;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;

/// <summary>
/// CLI command that parses flags and executes the recursive verification orchestrator loop.
/// </summary>
public sealed class RunCommand : Command
{
    public Option<string> RepoPathOption { get; } = new(
        name: "--repo-path",
        aliases: ["-r"])
    {
        Description = "Target repository path",
        DefaultValueFactory = _ => "."
    };

    public Option<string> TestCmdOption { get; } = new(
        name: "--test-cmd",
        aliases: ["-t"])
    {
        Description = "Test runner command",
        DefaultValueFactory = _ => "pytest"
    };

    public Option<string> ImageOption { get; } = new(
        name: "--image",
        aliases: ["-i"])
    {
        Description = "Docker runner image",
        DefaultValueFactory = _ => "python:3.11-slim"
    };

    public Option<int> MaxRetriesOption { get; } = new(
        name: "--max-retries",
        aliases: ["-m"])
    {
        Description = "Maximum verification attempts before circuit breaker",
        DefaultValueFactory = _ => 3
    };

    public Option<string> LlmUrlOption { get; } = new(
        name: "--llm-url",
        aliases: [])
    {
        Description = "OpenAI-compatible Base URL",
        DefaultValueFactory = _ => "http://localhost:11434/v1"
    };

    public Option<string> LlmKeyOption { get; } = new(
        name: "--llm-key",
        aliases: [])
    {
        Description = "API key for LLM endpoint",
        DefaultValueFactory = _ => "dummy-key"
    };

    public Option<string> LlmModelOption { get; } = new(
        name: "--llm-model",
        aliases: [])
    {
        Description = "LLM model name",
        DefaultValueFactory = _ => "gpt-4o-mini"
    };

    public Option<bool> VerboseOption { get; } = new(
        name: "--verbose",
        aliases: ["-v"])
    {
        Description = "Enable debug logging",
        DefaultValueFactory = _ => false
    };

    private readonly IOrchestratorService? _orchestratorService;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly TextWriter? _output;
    private readonly TextWriter? _error;

    public RunCommand(
        IOrchestratorService? orchestratorService = null,
        Action<IServiceCollection>? configureServices = null,
        TextWriter? output = null,
        TextWriter? error = null)
        : base("run", "Run the verification orchestrator loop.")
    {
        _orchestratorService = orchestratorService;
        _configureServices = configureServices;
        _output = output;
        _error = error;

        Add(RepoPathOption);
        Add(TestCmdOption);
        Add(ImageOption);
        Add(MaxRetriesOption);
        Add(LlmUrlOption);
        Add(LlmKeyOption);
        Add(LlmModelOption);
        Add(VerboseOption);

        this.SetAction(ExecuteAsync);
    }

    private async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var outWriter = _output ?? Console.Out;
        var errWriter = _error ?? Console.Error;

        var repoPath = parseResult.GetValue(RepoPathOption) ?? ".";
        var testCmd = parseResult.GetValue(TestCmdOption) ?? "pytest";
        var image = parseResult.GetValue(ImageOption) ?? "python:3.11-slim";
        var maxRetries = parseResult.GetValue(MaxRetriesOption);
        var llmUrl = parseResult.GetValue(LlmUrlOption) ?? "http://localhost:11434/v1";
        var llmKey = parseResult.GetValue(LlmKeyOption) ?? "dummy-key";
        var llmModel = parseResult.GetValue(LlmModelOption) ?? "gpt-4o-mini";
        var verbose = parseResult.GetValue(VerboseOption);

        // Pre-flight validation
        var fullRepoPath = Path.GetFullPath(repoPath);
        if (!Directory.Exists(fullRepoPath))
        {
            await errWriter.WriteLineAsync($"Pre-flight error: Repository path does not exist: '{repoPath}'");
            return 2;
        }

        if (maxRetries <= 0)
        {
            await errWriter.WriteLineAsync($"Pre-flight error: Max retries must be greater than 0. Got: {maxRetries}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(testCmd))
        {
            await errWriter.WriteLineAsync("Pre-flight error: Test command cannot be empty or whitespace.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            await errWriter.WriteLineAsync("Pre-flight error: Docker image cannot be empty or whitespace.");
            return 2;
        }

        if (!Uri.TryCreate(llmUrl, UriKind.Absolute, out _))
        {
            await errWriter.WriteLineAsync($"Pre-flight error: Invalid LLM Base URL: '{llmUrl}'");
            return 2;
        }

        try
        {
            var orchestratorOptions = new OrchestratorOptions
            {
                MaxAttempts = maxRetries,
                TestCommand = testCmd,
                RepositoryPath = fullRepoPath
            };

            if (_orchestratorService != null)
            {
                var directResult = await _orchestratorService.RunVerificationLoopAsync(orchestratorOptions, cancellationToken);
                return HandleResult(directResult, outWriter, errWriter);
            }

            var services = new ServiceCollection();
            services.AddLogging(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            services.AddIteraPatchCore(
                configureSandbox: opt =>
                {
                    opt.Image = image;
                    opt.WorkingDirectory = "/workspace";
                },
                configureLlm: opt =>
                {
                    opt.BaseUrl = llmUrl;
                    opt.ApiKey = llmKey;
                    opt.Model = llmModel;
                },
                configureOrchestrator: opt =>
                {
                    opt.MaxAttempts = maxRetries;
                    opt.TestCommand = testCmd;
                    opt.RepositoryPath = fullRepoPath;
                });

            _configureServices?.Invoke(services);

            using var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IOrchestratorService>();

            var result = await orchestrator.RunVerificationLoopAsync(orchestratorOptions, cancellationToken);
            return HandleResult(result, outWriter, errWriter);
        }
        catch (OperationCanceledException)
        {
            await errWriter.WriteLineAsync("Verification loop cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            await errWriter.WriteLineAsync($"Fatal execution error: {ex.Message}");
            return 2;
        }
    }

    private static int HandleResult(OrchestratorResult result, TextWriter outWriter, TextWriter errWriter)
    {
        if (result.Success)
        {
            outWriter.WriteLine($"[SUCCESS] {result.Message}");
            return 0;
        }
        else
        {
            errWriter.WriteLine($"[FAILURE] {result.Message}");
            return 1;
        }
    }
}
