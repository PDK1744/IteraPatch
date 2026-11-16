namespace IteraPatch.Core.Extensions;

using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Services;

/// <summary>
/// Extension methods for setting up IteraPatch Core services in an <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all IteraPatch Core services, configuration options, and typed HTTP clients into DI.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configureSandbox">Optional delegate to configure SandboxOptions.</param>
    /// <param name="configureLlm">Optional delegate to configure LlmOptions.</param>
    /// <param name="configureOrchestrator">Optional delegate to configure OrchestratorOptions.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddIteraPatchCore(
        this IServiceCollection services,
        Action<SandboxOptions>? configureSandbox = null,
        Action<LlmOptions>? configureLlm = null,
        Action<OrchestratorOptions>? configureOrchestrator = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Options registration
        services.Configure(configureSandbox ?? (_ => { }));
        services.Configure(configureLlm ?? (_ => { }));
        services.Configure(configureOrchestrator ?? (_ => { }));

        // Provide direct options resolution for service constructors
        services.AddTransient(sp => sp.GetRequiredService<IOptions<SandboxOptions>>().Value);
        services.AddTransient(sp => sp.GetRequiredService<IOptions<LlmOptions>>().Value);
        services.AddTransient(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);

        // Infrastructure and Domain Services
        services.AddScoped<ISandboxService, DockerSandboxService>();
        services.AddScoped<IGitDeltaService, LibGit2SharpGitDeltaService>();
        services.AddSingleton<ITraceParser, RegexTraceParser>();

        // Custom TypedHttpClientFactory avoids constructor ambiguity with ActivatorUtilities
        services.AddSingleton<ITypedHttpClientFactory<OpenAiCompatibleLlmClient>, OpenAiCompatibleLlmClientTypedFactory>();
        services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>();

        services.AddSingleton<IPatchValidator, BlastRadiusPatchValidator>();
        services.AddScoped<IPatchApplier, FilePatchApplier>();
        services.AddScoped<IOrchestratorService, RecursiveVerificationOrchestrator>();

        return services;
    }

    /// <summary>
    /// Factory for creating OpenAiCompatibleLlmClient with resolved LlmOptions and ILogger.
    /// </summary>
    private sealed class OpenAiCompatibleLlmClientTypedFactory : ITypedHttpClientFactory<OpenAiCompatibleLlmClient>
    {
        private readonly IOptions<LlmOptions> _options;
        private readonly ILogger<OpenAiCompatibleLlmClient>? _logger;

        public OpenAiCompatibleLlmClientTypedFactory(
            IOptions<LlmOptions> options,
            ILogger<OpenAiCompatibleLlmClient>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        public OpenAiCompatibleLlmClient CreateClient(HttpClient httpClient)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            return new OpenAiCompatibleLlmClient(httpClient, _options.Value, _logger);
        }
    }
}
