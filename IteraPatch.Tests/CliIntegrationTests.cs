namespace IteraPatch.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IteraPatch.Cli;
using IteraPatch.Cli.Commands;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Extensions;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public sealed class CliIntegrationTests
{
    #region 1. Dependency Injection Registration Tests

    [Fact]
    public void AddIteraPatchCore_RegistersAllRequiredInterfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIteraPatchCore();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var sandboxService = sp.GetRequiredService<ISandboxService>();
        var gitDeltaService = sp.GetRequiredService<IGitDeltaService>();
        var traceParser = sp.GetRequiredService<ITraceParser>();
        var llmClient = sp.GetRequiredService<ILlmClient>();
        var patchValidator = sp.GetRequiredService<IPatchValidator>();
        var patchApplier = sp.GetRequiredService<IPatchApplier>();
        var orchestrator = sp.GetRequiredService<IOrchestratorService>();

        Assert.NotNull(sandboxService);
        Assert.IsType<DockerSandboxService>(sandboxService);

        Assert.NotNull(gitDeltaService);
        Assert.IsType<LibGit2SharpGitDeltaService>(gitDeltaService);

        Assert.NotNull(traceParser);
        Assert.IsType<RegexTraceParser>(traceParser);

        Assert.NotNull(llmClient);
        Assert.IsType<OpenAiCompatibleLlmClient>(llmClient);

        Assert.NotNull(patchValidator);
        Assert.IsType<BlastRadiusPatchValidator>(patchValidator);

        Assert.NotNull(patchApplier);
        Assert.IsType<FilePatchApplier>(patchApplier);

        Assert.NotNull(orchestrator);
        Assert.IsType<RecursiveVerificationOrchestrator>(orchestrator);
    }

    [Fact]
    public void AddIteraPatchCore_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddIteraPatchCore());
    }

    [Fact]
    public void AddIteraPatchCore_ConfiguresOptionsCorrectly()
    {
        var services = new ServiceCollection();
        services.AddIteraPatchCore(
            configureSandbox: opt =>
            {
                opt.Image = "custom-runner:3.12";
                opt.TimeoutSeconds = 120;
                opt.MemoryLimitBytes = 1024 * 1024 * 1024;
                opt.WorkingDirectory = "/custom";
            },
            configureLlm: opt =>
            {
                opt.BaseUrl = "https://custom-llm.ai/v1";
                opt.ApiKey = "secret-token-xyz";
                opt.Model = "gpt-4o";
                opt.Temperature = 0.2;
                opt.TimeoutSeconds = 45;
            },
            configureOrchestrator: opt =>
            {
                opt.MaxAttempts = 5;
                opt.TestCommand = "python -m unittest";
                opt.RepositoryPath = "/workspace/repo";
            });

        using var serviceProvider = services.BuildServiceProvider();

        var sandboxOptions = serviceProvider.GetRequiredService<IOptions<SandboxOptions>>().Value;
        Assert.Equal("custom-runner:3.12", sandboxOptions.Image);
        Assert.Equal(120, sandboxOptions.TimeoutSeconds);
        Assert.Equal(1024 * 1024 * 1024, sandboxOptions.MemoryLimitBytes);
        Assert.Equal("/custom", sandboxOptions.WorkingDirectory);

        var llmOptions = serviceProvider.GetRequiredService<IOptions<LlmOptions>>().Value;
        Assert.Equal("https://custom-llm.ai/v1", llmOptions.BaseUrl);
        Assert.Equal("secret-token-xyz", llmOptions.ApiKey);
        Assert.Equal("gpt-4o", llmOptions.Model);
        Assert.Equal(0.2, llmOptions.Temperature);
        Assert.Equal(45, llmOptions.TimeoutSeconds);

        var orchestratorOptions = serviceProvider.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
        Assert.Equal(5, orchestratorOptions.MaxAttempts);
        Assert.Equal("python -m unittest", orchestratorOptions.TestCommand);
        Assert.Equal("/workspace/repo", orchestratorOptions.RepositoryPath);
    }

    [Fact]
    public void AddIteraPatchCore_LifetimeVerification_SingletonsAndScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIteraPatchCore();

        using var rootProvider = services.BuildServiceProvider();

        ITraceParser parser1;
        ITraceParser parser2;
        IPatchValidator validator1;
        IPatchValidator validator2;

        ISandboxService sandbox1;
        ISandboxService sandbox2;
        IOrchestratorService orch1;
        IOrchestratorService orch2;

        using (var scope1 = rootProvider.CreateScope())
        {
            parser1 = scope1.ServiceProvider.GetRequiredService<ITraceParser>();
            validator1 = scope1.ServiceProvider.GetRequiredService<IPatchValidator>();
            sandbox1 = scope1.ServiceProvider.GetRequiredService<ISandboxService>();
            orch1 = scope1.ServiceProvider.GetRequiredService<IOrchestratorService>();
        }

        using (var scope2 = rootProvider.CreateScope())
        {
            parser2 = scope2.ServiceProvider.GetRequiredService<ITraceParser>();
            validator2 = scope2.ServiceProvider.GetRequiredService<IPatchValidator>();
            sandbox2 = scope2.ServiceProvider.GetRequiredService<ISandboxService>();
            orch2 = scope2.ServiceProvider.GetRequiredService<IOrchestratorService>();
        }

        // Singletons must be identical across scopes
        Assert.Same(parser1, parser2);
        Assert.Same(validator1, validator2);

        // Scoped services must be different instances across scopes
        Assert.NotSame(sandbox1, sandbox2);
        Assert.NotSame(orch1, orch2);
    }

    #endregion

    #region 2. Command Line Flag Parsing Tests

    [Fact]
    public void RunCommand_DefaultOptionValues_MatchSpecification()
    {
        var runCommand = new RunCommand();
        var parseResult = runCommand.Parse("run");

        Assert.Equal(".", parseResult.GetValue(runCommand.RepoPathOption));
        Assert.Equal("pytest", parseResult.GetValue(runCommand.TestCmdOption));
        Assert.Equal("python:3.11-slim", parseResult.GetValue(runCommand.ImageOption));
        Assert.Equal(3, parseResult.GetValue(runCommand.MaxRetriesOption));
        Assert.Equal("http://localhost:11434/v1", parseResult.GetValue(runCommand.LlmUrlOption));
        Assert.Equal("dummy-key", parseResult.GetValue(runCommand.LlmKeyOption));
        Assert.Equal("gpt-4o-mini", parseResult.GetValue(runCommand.LlmModelOption));
        Assert.False(parseResult.GetValue(runCommand.VerboseOption));
    }

    [Fact]
    public void RunCommand_ShortFlags_ParseCorrectly()
    {
        var runCommand = new RunCommand();
        var parseResult = runCommand.Parse("run -r /target/path -t nosetests -i custom-python:3.12 -m 4 -v");

        Assert.Equal("/target/path", parseResult.GetValue(runCommand.RepoPathOption));
        Assert.Equal("nosetests", parseResult.GetValue(runCommand.TestCmdOption));
        Assert.Equal("custom-python:3.12", parseResult.GetValue(runCommand.ImageOption));
        Assert.Equal(4, parseResult.GetValue(runCommand.MaxRetriesOption));
        Assert.True(parseResult.GetValue(runCommand.VerboseOption));
    }

    [Fact]
    public void RunCommand_LongFlags_ParseCorrectly()
    {
        var runCommand = new RunCommand();
        var parseResult = runCommand.Parse(
            "run --repo-path /my/repo --test-cmd \"dotnet test\" --image mcr.microsoft.com/dotnet/sdk:10.0 " +
            "--max-retries 5 --llm-url https://api.openai.com/v1 --llm-key sk-abc12345 --llm-model gpt-4o --verbose");

        Assert.Equal("/my/repo", parseResult.GetValue(runCommand.RepoPathOption));
        Assert.Equal("dotnet test", parseResult.GetValue(runCommand.TestCmdOption));
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", parseResult.GetValue(runCommand.ImageOption));
        Assert.Equal(5, parseResult.GetValue(runCommand.MaxRetriesOption));
        Assert.Equal("https://api.openai.com/v1", parseResult.GetValue(runCommand.LlmUrlOption));
        Assert.Equal("sk-abc12345", parseResult.GetValue(runCommand.LlmKeyOption));
        Assert.Equal("gpt-4o", parseResult.GetValue(runCommand.LlmModelOption));
        Assert.True(parseResult.GetValue(runCommand.VerboseOption));
    }

    [Theory]
    [InlineData(new string[0], new[] { "--help" })]
    [InlineData(new[] { "--help" }, new[] { "--help" })]
    [InlineData(new[] { "-h" }, new[] { "-h" })]
    [InlineData(new[] { "-?" }, new[] { "-?" })]
    [InlineData(new[] { "--version" }, new[] { "--version" })]
    [InlineData(new[] { "run", "-r", "." }, new[] { "run", "-r", "." })]
    [InlineData(new[] { "-r", "." }, new[] { "run", "-r", "." })]
    [InlineData(new[] { "-m", "2", "-v" }, new[] { "run", "-m", "2", "-v" })]
    public void Program_NormalizeArgs_CorrectlyFormatsArgs(string[] input, string[] expected)
    {
        var normalized = Program.NormalizeArgs(input);
        Assert.Equal(expected, normalized);
    }

    #endregion

    #region 3. Execution & Exit Code Tests (Unit / Invocation Level)

    [Fact]
    public async Task RunCommand_VerificationSuccess_ReturnsExitCode0()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_success_").FullName;
        try
        {
            var mockOrchestrator = new MockOrchestrator(
                expectedResult: new OrchestratorResult(
                    Success: true,
                    TotalAttempts: 1,
                    History: Array.Empty<IterationStep>(),
                    Message: "Verification succeeded! All tests passing."
                )
            );

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            var runCommand = new RunCommand(
                orchestratorService: mockOrchestrator,
                output: outWriter,
                error: errWriter);

            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(0, exitCode);
            Assert.Contains("[SUCCESS]", outWriter.ToString());
            Assert.NotNull(mockOrchestrator.LastOptionsPassed);
            Assert.Equal(Path.GetFullPath(tempDir), mockOrchestrator.LastOptionsPassed.RepositoryPath);
            Assert.Equal(3, mockOrchestrator.LastOptionsPassed.MaxAttempts);
            Assert.Equal("pytest", mockOrchestrator.LastOptionsPassed.TestCommand);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_CircuitBreakerTripped_ReturnsExitCode1()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_failure_").FullName;
        try
        {
            var mockOrchestrator = new MockOrchestrator(
                expectedResult: new OrchestratorResult(
                    Success: false,
                    TotalAttempts: 3,
                    History: Array.Empty<IterationStep>(),
                    Message: "Circuit breaker tripped: verification failed after 3 attempts."
                )
            );

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            var runCommand = new RunCommand(
                orchestratorService: mockOrchestrator,
                output: outWriter,
                error: errWriter);

            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(1, exitCode);
            Assert.Contains("[FAILURE]", errWriter.ToString());
            Assert.Contains("Circuit breaker tripped", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_NonExistentRepository_ReturnsExitCode2()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_repo_" + Guid.NewGuid());
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var runCommand = new RunCommand(output: outWriter, error: errWriter);
        var rootCommand = Program.BuildCommandLine(runCommand);
        var parseResult = rootCommand.Parse($"run -r \"{nonExistentPath}\"");

        var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

        Assert.Equal(2, exitCode);
        Assert.Contains("Pre-flight error: Repository path does not exist", errWriter.ToString());
    }

    [Fact]
    public async Task RunCommand_InvalidMaxRetries_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_retries_").FullName;
        try
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            var runCommand = new RunCommand(output: outWriter, error: errWriter);
            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\" -m 0");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: Max retries must be greater than 0", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_InvalidLlmUrl_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_url_").FullName;
        try
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            var runCommand = new RunCommand(output: outWriter, error: errWriter);
            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\" --llm-url not_a_valid_url");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: Invalid LLM Base URL", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_EmptyRepoPath_ReturnsExitCode2()
    {
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        var runCommand = new RunCommand(output: outWriter, error: errWriter);
        var rootCommand = Program.BuildCommandLine(runCommand);
        var parseResult = rootCommand.Parse("run -r \"\"");

        var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

        Assert.Equal(2, exitCode);
        Assert.Contains("Pre-flight error: Repository path cannot be empty or whitespace.", errWriter.ToString());
    }

    [Fact]
    public async Task RunCommand_EmptyTestCommand_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_cmd_").FullName;
        try
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            var runCommand = new RunCommand(output: outWriter, error: errWriter);
            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\" -t \"\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: Test command cannot be empty or whitespace.", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_EmptyImage_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_img_").FullName;
        try
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            var runCommand = new RunCommand(output: outWriter, error: errWriter);
            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\" -i \"\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: Docker image cannot be empty or whitespace.", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_EmptyLlmModel_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_model_").FullName;
        try
        {
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();

            var runCommand = new RunCommand(output: outWriter, error: errWriter);
            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\" --llm-model \"\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: LLM model cannot be empty or whitespace.", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_OrchestratorException_ReturnsExitCode2()
    {
        var tempDir = Directory.CreateTempSubdirectory("iterapatch_test_exception_").FullName;
        try
        {
            var mockOrchestrator = new MockOrchestrator(
                expectedResult: null,
                throwException: new InvalidOperationException("Docker daemon connection failed abruptly.")
            );

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            var runCommand = new RunCommand(
                orchestratorService: mockOrchestrator,
                output: outWriter,
                error: errWriter);

            var rootCommand = Program.BuildCommandLine(runCommand);
            var parseResult = rootCommand.Parse($"run -r \"{tempDir}\"");

            var exitCode = await parseResult.InvokeAsync(new System.CommandLine.InvocationConfiguration());

            Assert.Equal(2, exitCode);
            Assert.Contains("Fatal execution error", errWriter.ToString());
            Assert.Contains("Docker daemon connection failed abruptly.", errWriter.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Program_Main_InvalidOptionArgument_ReturnsExitCode2()
    {
        // When invalid argument type is passed, e.g. -m notanumber, Main returns 2
        var exitCode = await Program.Main(["run", "-m", "not_a_number"]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Program_Main_UnknownArgument_ReturnsExitCode2()
    {
        // When unrecognized argument is passed, Main returns 2
        var exitCode = await Program.Main(["run", "--totally-unknown-flag"]);
        Assert.Equal(2, exitCode);
    }

    #endregion

    #region 4. End-to-End CLI Subprocess Execution Tests

    [Fact]
    public async Task CliProcess_HelpFlag_PrintsHelpAndExitsWith0()
    {
        var cliDllPath = GetCliDllPath();
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", $"\"{cliDllPath}\" --help");

        Assert.Equal(0, exitCode);
        Assert.Contains("IteraPatch - Git-native verification orchestrator", stdout);
        Assert.Contains("run", stdout);
    }

    [Fact]
    public async Task CliProcess_RunHelp_PrintsOptionsAndExitsWith0()
    {
        var cliDllPath = GetCliDllPath();
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", $"\"{cliDllPath}\" run --help");

        Assert.Equal(0, exitCode);
        Assert.Contains("--repo-path", stdout);
        Assert.Contains("--test-cmd", stdout);
        Assert.Contains("--image", stdout);
        Assert.Contains("--max-retries", stdout);
        Assert.Contains("--llm-url", stdout);
        Assert.Contains("--llm-key", stdout);
        Assert.Contains("--llm-model", stdout);
        Assert.Contains("--verbose", stdout);
    }

    [Fact]
    public async Task CliProcess_InvalidRepoPath_ExitsWith2()
    {
        var cliDllPath = GetCliDllPath();
        var fakePath = Path.Combine(Path.GetTempPath(), "cli_proc_nonexistent_" + Guid.NewGuid());
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", $"\"{cliDllPath}\" run -r \"{fakePath}\"");

        Assert.Equal(2, exitCode);
        Assert.Contains("Pre-flight error: Repository path does not exist", stderr);
    }

    [Fact]
    public async Task CliProcess_InvalidMaxRetries_ExitsWith2()
    {
        var cliDllPath = GetCliDllPath();
        var tempDir = Directory.CreateTempSubdirectory("cli_proc_test_").FullName;
        try
        {
            var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", $"\"{cliDllPath}\" run -r \"{tempDir}\" -m -1");

            Assert.Equal(2, exitCode);
            Assert.Contains("Pre-flight error: Max retries must be greater than 0", stderr);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CliProcess_UnknownArgument_ExitsWith2()
    {
        var cliDllPath = GetCliDllPath();
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", $"\"{cliDllPath}\" run --invalid-option-flag");

        Assert.Equal(2, exitCode);
    }

    private static string GetCliDllPath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "itera.dll"),
            Path.Combine(baseDir, "IteraPatch.Cli.dll"),
            Path.GetFullPath(Path.Combine(baseDir, "../../../../IteraPatch.Cli/bin/Debug/net10.0/itera.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "../../../../IteraPatch.Cli/bin/Debug/net10.0/IteraPatch.Cli.dll"))
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Cannot locate itera.dll or IteraPatch.Cli.dll from {baseDir}");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string command,
        string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    #endregion

    #region Mock Helpers

    private sealed class MockOrchestrator : IOrchestratorService
    {
        private readonly OrchestratorResult? _expectedResult;
        private readonly Exception? _throwException;

        public OrchestratorOptions? LastOptionsPassed { get; private set; }

        public MockOrchestrator(OrchestratorResult? expectedResult, Exception? throwException = null)
        {
            _expectedResult = expectedResult;
            _throwException = throwException;
        }

        public Task<OrchestratorResult> RunVerificationLoopAsync(
            OrchestratorOptions options,
            CancellationToken cancellationToken = default)
        {
            LastOptionsPassed = options;
            if (_throwException != null)
            {
                throw _throwException;
            }

            return Task.FromResult(_expectedResult ?? new OrchestratorResult(true, 1, Array.Empty<IterationStep>(), "Success"));
        }
    }

    #endregion
}
