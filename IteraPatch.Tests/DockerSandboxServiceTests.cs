namespace IteraPatch.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class DockerSandboxServiceTests
{
    private static byte[] CreateDockerMuxFrame(byte streamType, string content)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var frame = new byte[8 + contentBytes.Length];
        frame[0] = streamType; // 1 = stdout, 2 = stderr
        var len = contentBytes.Length;
        frame[4] = (byte)((len >> 24) & 0xFF);
        frame[5] = (byte)((len >> 16) & 0xFF);
        frame[6] = (byte)((len >> 8) & 0xFF);
        frame[7] = (byte)(len & 0xFF);
        Array.Copy(contentBytes, 0, frame, 8, len);
        return frame;
    }

    private static MultiplexedStream CreateFakeMultiplexedStream(string stdout, string stderr)
    {
        var ms = new MemoryStream();
        if (!string.IsNullOrEmpty(stdout))
        {
            var f1 = CreateDockerMuxFrame(1, stdout);
            ms.Write(f1, 0, f1.Length);
        }
        if (!string.IsNullOrEmpty(stderr))
        {
            var f2 = CreateDockerMuxFrame(2, stderr);
            ms.Write(f2, 0, f2.Length);
        }
        ms.Position = 0;
        return new MultiplexedStream(ms, true);
    }

    [Fact]
    public async Task SecurityConfiguration_EnforcesNetworkModeNoneAutoRemoveAndReadOnlyMount()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("test passed\n", string.Empty),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 }
        };

        var options = new SandboxOptions
        {
            Image = "python:3.11-slim",
            WorkingDirectory = "/workspace",
            MemoryLimitBytes = 256 * 1024 * 1024
        };

        using var service = new DockerSandboxService(options, fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: "/host/repo",
            Command: "pytest",
            Arguments: new[] { "-q" }
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(fakeClient.LastCreatedParameters);

        var hostConfig = fakeClient.LastCreatedParameters.HostConfig;
        Assert.NotNull(hostConfig);
        Assert.Equal("none", hostConfig.NetworkMode);
        Assert.True(hostConfig.AutoRemove);
        Assert.Equal(256 * 1024 * 1024, hostConfig.Memory);

        // Verify read-only bind mount for the working directory
        Assert.NotNull(hostConfig.Mounts);
        var bindMount = hostConfig.Mounts.FirstOrDefault(m => m.Type == "bind");
        Assert.NotNull(bindMount);
        Assert.True(bindMount.ReadOnly);
        Assert.Equal(Path.GetFullPath("/host/repo"), bindMount.Source);
        Assert.Equal("/workspace", bindMount.Target);

        // Verify tmpfs mount for transient writes
        var tmpfsMount = hostConfig.Mounts.FirstOrDefault(m => m.Type == "tmpfs");
        Assert.NotNull(tmpfsMount);
        Assert.Equal("/tmp", tmpfsMount.Target);
    }

    [Fact]
    public async Task ParameterPassing_PassesCommandArgumentsAndEnvironmentVariablesCleanly()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("ok\n", string.Empty),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 }
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var env = new Dictionary<string, string>
        {
            ["TEST_VAR"] = "test_value",
            ["RUN_ID"] = "42"
        };

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "python",
            Arguments: new[] { "-m", "pytest", "tests/test_core.py" },
            EnvironmentVariables: env,
            TimeoutSeconds: 30
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        var parameters = fakeClient.LastCreatedParameters;
        Assert.NotNull(parameters);

        // Assert Cmd list preserves separate tokens (no string concat / shell injection)
        Assert.Equal(new[] { "python", "-m", "pytest", "tests/test_core.py" }, parameters.Cmd);

        // Assert Env includes supplied variables and PYTHONUNBUFFERED=1
        Assert.Contains("TEST_VAR=test_value", parameters.Env);
        Assert.Contains("RUN_ID=42", parameters.Env);
        Assert.Contains("PYTHONUNBUFFERED=1", parameters.Env);

        Assert.False(parameters.Tty);
        Assert.True(parameters.AttachStdout);
        Assert.True(parameters.AttachStderr);
    }

    [Fact]
    public async Task StreamCapture_CapturesStdoutAndStderrSeparately()
    {
        const string expectedStdout = "=== test session starts ===\ncollecting ...\ntest_math.py::test_add PASSED\n";
        const string expectedStderr = "UserWarning: deprecated feature used in test_math.py\n";

        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream(expectedStdout, expectedStderr),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 }
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedStdout, result.StandardOutput);
        Assert.Equal(expectedStderr, result.StandardError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Teardown_AlwaysForcesContainerRemovalInFinally()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("fail\n", "assertion error\n"),
            WaitResponse = new ContainerWaitResponse { StatusCode = 1 }
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var result = await service.ExecuteAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("fail\n", result.StandardOutput);
        Assert.Equal("assertion error\n", result.StandardError);

        // Assert container removal was invoked with Force = true
        Assert.True(fakeClient.ContainerRemoved);
        Assert.NotNull(fakeClient.LastRemoveParameters);
        Assert.True(fakeClient.LastRemoveParameters.Force);
    }

    [Fact]
    public async Task TimeoutCancellation_MarksResultAsTimedOutAndCleansUp()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream(string.Empty, string.Empty),
            WaitDelay = TimeSpan.FromSeconds(5) // exceeds request timeout of 1 second
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>(),
            TimeoutSeconds: 1
        );

        var result = await service.ExecuteAsync(request);

        Assert.False(result.IsSuccess);
        Assert.True(result.TimedOut);
        Assert.Equal(137, result.ExitCode);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);

        // Teardown must be called even on timeout
        Assert.True(fakeClient.ContainerRemoved);
        Assert.NotNull(fakeClient.LastRemoveParameters);
        Assert.True(fakeClient.LastRemoveParameters.Force);
    }

    [Fact]
    public async Task UserCancellation_ThrowsOperationCanceledExceptionAndCleansUp()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream(string.Empty, string.Empty),
            WaitDelay = TimeSpan.FromSeconds(10)
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);
        using var cts = new CancellationTokenSource();

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>(),
            TimeoutSeconds: 60
        );

        // Cancel after 100ms
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ExecuteAsync(request, cts.Token);
        });

        // Verify container was still removed in finally block
        Assert.True(fakeClient.ContainerRemoved);
        Assert.NotNull(fakeClient.LastRemoveParameters);
        Assert.True(fakeClient.LastRemoveParameters.Force);
    }

    [Fact]
    public async Task Teardown_ToleratesContainerNotFoundOrConflictInCleanup()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("output\n", string.Empty),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 },
            RemoveException = new DockerContainerNotFoundException(HttpStatusCode.NotFound, "already removed")
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        // Should complete successfully without throwing cleanup exception
        var result = await service.ExecuteAsync(request);
        Assert.True(result.IsSuccess);
        Assert.Equal("output\n", result.StandardOutput);
    }

    [Fact]
    public async Task ExecutionFailure_BubblesExceptionAndCleansUp()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream(string.Empty, string.Empty),
            StartException = new InvalidOperationException("Docker daemon rejected start")
        };

        using var service = new DockerSandboxService(dockerClient: fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(request));
        Assert.Equal("Docker daemon rejected start", ex.Message);

        // Teardown should still be attempted for the created container
        Assert.True(fakeClient.ContainerRemoved);
    }

    [Fact]
    public async Task Validation_ThrowsOnNullOrEmptyArguments()
    {
        using var service = new DockerSandboxService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ExecuteAsync(null!));

        var emptyCommandRequest = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "   ",
            Arguments: Array.Empty<string>()
        );

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(emptyCommandRequest));
    }

    [Fact]
    public async Task EnsureImageExists_PullsImageWhenNotCachedLocally()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("ok\n", string.Empty),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 },
            InspectThrowsNotFound = true
        };

        var options = new SandboxOptions { Image = "custom-repo/runner:v2.1" };
        using var service = new DockerSandboxService(options, fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: ".",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.True(fakeClient.ImageCreateCalled);
        Assert.Equal("custom-repo/runner", fakeClient.LastImageCreateParams?.FromImage);
        Assert.Equal("v2.1", fakeClient.LastImageCreateParams?.Tag);
    }

    [Fact]
    public async Task RealDocker_ExecutePythonCommandSuccessfully()
    {
        // Check if real Docker is reachable
        using var realClient = new DockerClientConfiguration().CreateClient();
        try
        {
            await realClient.System.PingAsync(CancellationToken.None);
        }
        catch
        {
            // Docker daemon not available, skip integration test
            return;
        }

        using var service = new DockerSandboxService(realClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: Directory.GetCurrentDirectory(),
            Command: "python",
            Arguments: new[] { "-c", "import sys; sys.stdout.write('integration stdout\\n'); sys.stderr.write('integration stderr\\n'); sys.exit(0)" },
            TimeoutSeconds: 15
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("integration stdout", result.StandardOutput);
        Assert.Contains("integration stderr", result.StandardError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task EnvironmentVariables_IncludesVenvPathsAndPythonPath()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream("ok\n", string.Empty),
            WaitResponse = new ContainerWaitResponse { StatusCode = 0 }
        };

        var options = new SandboxOptions { WorkingDirectory = "/workspace" };
        using var service = new DockerSandboxService(options, fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: "/test/repo",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess);
        var env = fakeClient.LastCreatedParameters?.Env;
        Assert.NotNull(env);
        Assert.Contains("PATH=/workspace/.venv/bin:/workspace/venv/bin:/usr/local/bin:/usr/local/sbin:/usr/bin:/usr/sbin:/bin:/sbin", env);
        Assert.Contains("PYTHONPATH=/workspace", env);
    }

    [Fact]
    public async Task StartContainer_ExecutableNotFound_ReturnsExitCode127WithFriendlyError()
    {
        var fakeClient = new FakeDockerClient
        {
            AttachStreamFactory = () => CreateFakeMultiplexedStream(string.Empty, string.Empty),
            StartException = new DockerApiException(
                HttpStatusCode.BadRequest,
                "{\"message\":\"failed to create task for container: failed to create shim task: OCI runtime create failed: runc create failed: unable to start container process: error during container init: exec: \\\"pytest\\\": executable file not found in $PATH\"}")
        };

        using var service = new DockerSandboxService(fakeClient);

        var request = new SandboxExecutionRequest(
            WorkingDirectory: "/test/repo",
            Command: "pytest",
            Arguments: Array.Empty<string>()
        );

        var result = await service.ExecuteAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(127, result.ExitCode);
        Assert.Contains("Executable 'pytest' was not found in $PATH", result.StandardError);
        Assert.False(result.TimedOut);
    }
}

#region Fake Docker Client Implementations for Unit Testing

internal sealed class FakeDockerClient : IDockerClient
{
    public FakeContainerOperations FakeContainers { get; } = new();
    public FakeImageOperations FakeImages { get; } = new();

    public IContainerOperations Containers => FakeContainers;
    public IImageOperations Images => FakeImages;

    public Func<MultiplexedStream>? AttachStreamFactory
    {
        get => FakeContainers.AttachStreamFactory;
        set => FakeContainers.AttachStreamFactory = value;
    }

    public ContainerWaitResponse WaitResponse
    {
        get => FakeContainers.WaitResponse;
        set => FakeContainers.WaitResponse = value;
    }

    public TimeSpan WaitDelay
    {
        get => FakeContainers.WaitDelay;
        set => FakeContainers.WaitDelay = value;
    }

    public Exception? StartException
    {
        get => FakeContainers.StartException;
        set => FakeContainers.StartException = value;
    }

    public Exception? RemoveException
    {
        get => FakeContainers.RemoveException;
        set => FakeContainers.RemoveException = value;
    }

    public CreateContainerParameters? LastCreatedParameters => FakeContainers.LastCreatedParameters;
    public ContainerRemoveParameters? LastRemoveParameters => FakeContainers.LastRemoveParameters;
    public bool ContainerRemoved => FakeContainers.ContainerRemoved;

    public bool InspectThrowsNotFound
    {
        get => FakeImages.InspectThrowsNotFound;
        set => FakeImages.InspectThrowsNotFound = value;
    }

    public bool ImageCreateCalled => FakeImages.ImageCreateCalled;
    public ImagesCreateParameters? LastImageCreateParams => FakeImages.LastImageCreateParams;

    public DockerClientConfiguration Configuration => throw new NotImplementedException();
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public INetworkOperations Networks => throw new NotImplementedException();
    public IVolumeOperations Volumes => throw new NotImplementedException();
    public ISecretsOperations Secrets => throw new NotImplementedException();
    public IConfigOperations Configs => throw new NotImplementedException();
    public ISwarmOperations Swarm => throw new NotImplementedException();
    public ITasksOperations Tasks => throw new NotImplementedException();
    public ISystemOperations System => throw new NotImplementedException();
    public IPluginOperations Plugin => throw new NotImplementedException();
    public IExecOperations Exec => throw new NotImplementedException();

    public void Dispose()
    {
    }
}

internal sealed class FakeContainerOperations : IContainerOperations
{
    public CreateContainerParameters? LastCreatedParameters { get; private set; }
    public ContainerRemoveParameters? LastRemoveParameters { get; private set; }
    public bool ContainerRemoved { get; private set; }

    public Func<MultiplexedStream>? AttachStreamFactory { get; set; }
    public ContainerWaitResponse WaitResponse { get; set; } = new() { StatusCode = 0 };
    public TimeSpan WaitDelay { get; set; } = TimeSpan.Zero;
    public Exception? StartException { get; set; }
    public Exception? RemoveException { get; set; }

    public Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken = default)
    {
        LastCreatedParameters = parameters;
        return Task.FromResult(new CreateContainerResponse { ID = "fake-container-12345" });
    }

    public Task<MultiplexedStream> AttachContainerAsync(string id, bool tty, ContainerAttachParameters parameters, CancellationToken cancellationToken = default)
    {
        var stream = AttachStreamFactory != null
            ? AttachStreamFactory()
            : new MultiplexedStream(new MemoryStream(), true);
        return Task.FromResult(stream);
    }

    public async Task<bool> StartContainerAsync(string id, ContainerStartParameters parameters, CancellationToken cancellationToken = default)
    {
        if (StartException != null)
        {
            throw StartException;
        }

        await Task.Yield();
        return true;
    }

    public async Task<ContainerWaitResponse> WaitContainerAsync(string id, CancellationToken cancellationToken = default)
    {
        if (WaitDelay > TimeSpan.Zero)
        {
            await Task.Delay(WaitDelay, cancellationToken);
        }

        return WaitResponse;
    }

    public async Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken = default)
    {
        ContainerRemoved = true;
        LastRemoveParameters = parameters;

        if (RemoveException != null)
        {
            throw RemoveException;
        }

        await Task.Yield();
    }

    // Remaining IContainerOperations stubs
    public Task<Stream> GetContainerLogsAsync(string id, ContainerLogsParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task GetContainerLogsAsync(string id, ContainerLogsParameters parameters, CancellationToken cancellationToken, IProgress<string> progress) => throw new NotImplementedException();
    public Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Stream> ExportContainerAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<GetArchiveFromContainerResponse> GetArchiveFromContainerAsync(string id, GetArchiveFromContainerParameters parameters, bool statOnly, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ExtractArchiveToContainerAsync(string id, ContainerPathStatParameters parameters, Stream stream, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Stream> GetContainerStatsAsync(string id, ContainerStatsParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task GetContainerStatsAsync(string id, ContainerStatsParameters parameters, IProgress<string> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task PauseContainerAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ContainersPruneResponse> PruneContainersAsync(ContainersPruneParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RenameContainerAsync(string id, ContainerRenameParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RestartContainerAsync(string id, ContainerRestartParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task UnpauseContainerAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ContainerUpdateResponse> UpdateContainerAsync(string id, ContainerUpdateParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ResizeContainerTtyAsync(string id, ContainerResizeParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ContainerProcessesResponse> ListProcessesAsync(string id, ContainerListProcessesParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<ContainerFileSystemChangeResponse>> InspectChangesAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task GetContainerStatsAsync(string id, ContainerStatsParameters parameters, IProgress<ContainerStatsResponse> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal sealed class FakeImageOperations : IImageOperations
{
    public bool InspectThrowsNotFound { get; set; }
    public bool ImageCreateCalled { get; private set; }
    public ImagesCreateParameters? LastImageCreateParams { get; private set; }

    public Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken = default)
    {
        if (InspectThrowsNotFound)
        {
            throw new DockerImageNotFoundException(HttpStatusCode.NotFound, $"Image {name} not found");
        }

        return Task.FromResult(new ImageInspectResponse { ID = "fake-image-id" });
    }

    public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default)
    {
        ImageCreateCalled = true;
        LastImageCreateParams = parameters;
        return Task.CompletedTask;
    }

    public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig authConfig, IDictionary<string, string> headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default)
    {
        ImageCreateCalled = true;
        LastImageCreateParams = parameters;
        return Task.CompletedTask;
    }

    // Remaining IImageOperations stubs
    public Task CreateImageAsync(ImagesCreateParameters parameters, Stream imageStream, AuthConfig authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task CreateImageAsync(ImagesCreateParameters parameters, Stream imageStream, AuthConfig authConfig, IDictionary<string, string> headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<ImagesListResponse>> ListImagesAsync(ImagesListParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Stream> BuildImageFromDockerfileAsync(Stream contents, ImageBuildParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task BuildImageFromDockerfileAsync(ImageBuildParameters parameters, Stream contents, IEnumerable<AuthConfig> authConfigs, IDictionary<string, string> headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<IDictionary<string, string>>> DeleteImageAsync(string name, ImageDeleteParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<ImageHistoryResponse>> GetImageHistoryAsync(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task PushImageAsync(string name, ImagePushParameters parameters, AuthConfig authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Stream> SaveImageAsync(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Stream> SaveImagesAsync(string[] names, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task LoadImageAsync(ImageLoadParameters parameters, Stream imageStream, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IList<ImageSearchResponse>> SearchImagesAsync(ImagesSearchParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ImagesPruneResponse> PruneImagesAsync(ImagesPruneParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task TagImageAsync(string name, ImageTagParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<CommitContainerChangesResponse> CommitContainerChangesAsync(CommitContainerChangesParameters parameters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

#endregion
