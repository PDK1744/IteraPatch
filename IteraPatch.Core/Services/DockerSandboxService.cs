namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Executes test runners inside ephemeral, network-isolated Docker containers with read-only bind mounts.
/// </summary>
public sealed class DockerSandboxService : ISandboxService, IDisposable
{
    private readonly SandboxOptions _options;
    private readonly IDockerClient _dockerClient;
    private readonly ILogger<DockerSandboxService>? _logger;
    private readonly bool _ownsDockerClient;

    public DockerSandboxService()
        : this(null, null, null)
    {
    }

    public DockerSandboxService(SandboxOptions? options)
        : this(options, null, null)
    {
    }

    public DockerSandboxService(IDockerClient? dockerClient)
        : this(null, dockerClient, null)
    {
    }

    public DockerSandboxService(
        SandboxOptions? options = null,
        IDockerClient? dockerClient = null,
        ILogger<DockerSandboxService>? logger = null)
    {
        _options = options ?? new SandboxOptions();
        _logger = logger;

        if (dockerClient != null)
        {
            _dockerClient = dockerClient;
            _ownsDockerClient = false;
        }
        else
        {
            _dockerClient = CreateDefaultDockerClient();
            _ownsDockerClient = true;
        }
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("Execution command cannot be empty or whitespace.", nameof(request));
        }

        var workingDir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? _options.WorkingDirectory
            : request.WorkingDirectory;
        var fullHostPath = Path.GetFullPath(workingDir);
        var containerWorkDir = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? "/workspace"
            : _options.WorkingDirectory;
        var image = string.IsNullOrWhiteSpace(_options.Image)
            ? "python:3.11-slim"
            : _options.Image;

        var timeoutSeconds = request.TimeoutSeconds > 0
            ? request.TimeoutSeconds
            : (_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 60);

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        await EnsureImageExistsAsync(image, linkedToken);

        var cmd = new List<string> { request.Command };
        if (request.Arguments != null && request.Arguments.Count > 0)
        {
            cmd.AddRange(request.Arguments);
        }

        var envList = new List<string>();
        if (request.EnvironmentVariables != null)
        {
            foreach (var (key, value) in request.EnvironmentVariables)
            {
                envList.Add($"{key}={value}");
            }
        }

        if (!envList.Any(e => e.StartsWith("PYTHONUNBUFFERED=", StringComparison.OrdinalIgnoreCase)))
        {
            envList.Add("PYTHONUNBUFFERED=1");
        }

        var mounts = new List<Mount>
        {
            new Mount
            {
                Type = "bind",
                Source = fullHostPath,
                Target = containerWorkDir,
                ReadOnly = true
            },
            new Mount
            {
                Type = "tmpfs",
                Target = "/tmp"
            }
        };

        var hostConfig = new HostConfig
        {
            NetworkMode = "none",
            AutoRemove = true,
            Memory = _options.MemoryLimitBytes > 0 ? _options.MemoryLimitBytes : 512 * 1024 * 1024,
            Mounts = mounts,
            Tmpfs = new Dictionary<string, string>
            {
                { "/tmp", "rw,noexec,nosuid,size=64m" }
            }
        };

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Cmd = cmd,
            Env = envList,
            WorkingDir = containerWorkDir,
            HostConfig = hostConfig,
            Tty = false,
            AttachStdout = true,
            AttachStderr = true
        };

        string? containerId = null;
        var stopwatch = Stopwatch.StartNew();
        Task<(string stdout, string stderr)>? readLogsTask = null;

        try
        {
            var createResponse = await _dockerClient.Containers.CreateContainerAsync(createParams, linkedToken);
            containerId = createResponse.ID;

            using var attachStream = await _dockerClient.Containers.AttachContainerAsync(
                containerId,
                tty: false,
                new ContainerAttachParameters
                {
                    Stream = true,
                    Stdout = true,
                    Stderr = true
                },
                linkedToken);

            readLogsTask = attachStream.ReadOutputToEndAsync(linkedToken);

            await _dockerClient.Containers.StartContainerAsync(
                containerId,
                new ContainerStartParameters(),
                linkedToken);

            var waitTask = _dockerClient.Containers.WaitContainerAsync(containerId, linkedToken);

            await Task.WhenAll(readLogsTask, waitTask);
            stopwatch.Stop();

            var (stdout, stderr) = await readLogsTask;
            var waitResponse = await waitTask;

            return new SandboxExecutionResult(
                ExitCode: (int)waitResponse.StatusCode,
                StandardOutput: stdout ?? string.Empty,
                StandardError: stderr ?? string.Empty,
                Duration: stopwatch.Elapsed,
                TimedOut: false
            );
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger?.LogWarning("Sandbox execution timed out after {TimeoutSeconds}s for command {Command}", timeoutSeconds, request.Command);

            var stdout = string.Empty;
            var stderr = $"Execution timed out after {timeoutSeconds} seconds.";

            if (readLogsTask is { IsCompletedSuccessfully: true })
            {
                stdout = readLogsTask.Result.stdout ?? string.Empty;
                if (!string.IsNullOrEmpty(readLogsTask.Result.stderr))
                {
                    stderr = $"{readLogsTask.Result.stderr}\n{stderr}";
                }
            }

            return new SandboxExecutionResult(
                ExitCode: 137,
                StandardOutput: stdout,
                StandardError: stderr,
                Duration: stopwatch.Elapsed,
                TimedOut: true
            );
        }
        finally
        {
            if (containerId != null)
            {
                try
                {
                    await _dockerClient.Containers.RemoveContainerAsync(
                        containerId,
                        new ContainerRemoveParameters { Force = true },
                        CancellationToken.None);
                }
                catch (DockerContainerNotFoundException)
                {
                    // Container already removed by AutoRemove
                }
                catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // Container already removed or removal in progress by AutoRemove
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsDockerClient)
        {
            _dockerClient.Dispose();
        }
    }

    private static IDockerClient CreateDefaultDockerClient()
    {
        return new DockerClientConfiguration().CreateClient();
    }

    private async Task EnsureImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        try
        {
            await _dockerClient.Images.InspectImageAsync(image, cancellationToken);
            return;
        }
        catch (DockerImageNotFoundException)
        {
            // Fall through to pull image
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Fall through to pull image
        }

        var (fromImage, tag) = ParseImageTag(image);
        await _dockerClient.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = fromImage,
                Tag = tag
            },
            authConfig: null,
            new Progress<JSONMessage>(),
            cancellationToken);
    }

    private static (string fromImage, string tag) ParseImageTag(string image)
    {
        var colonIndex = image.LastIndexOf(':');
        if (colonIndex > 0 && colonIndex < image.Length - 1 && !image[(colonIndex + 1)..].Contains('/'))
        {
            return (image[..colonIndex], image[(colonIndex + 1)..]);
        }

        return (image, "latest");
    }
}
