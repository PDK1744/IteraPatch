namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Model-agnostic LLM client communicating with OpenAI-compatible chat completion endpoints.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private const string SystemPrompt =
        "You are a code verification engineer. Fix failing tests by generating targeted patches. " +
        "You MUST ONLY modify files that appear in the provided Git diff. Return valid JSON matching the schema.";

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;
    private readonly ILogger<OpenAiCompatibleLlmClient>? _logger;

    public OpenAiCompatibleLlmClient(HttpClient httpClient)
        : this(httpClient, null, null)
    {
    }

    public OpenAiCompatibleLlmClient(HttpClient httpClient, LlmOptions? options)
        : this(httpClient, options, null)
    {
    }

    public OpenAiCompatibleLlmClient(
        HttpClient httpClient,
        LlmOptions? options = null,
        ILogger<OpenAiCompatibleLlmClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new LlmOptions();
        _logger = logger;
    }

    public async Task<LlmPatchResponse> RequestPatchAsync(
        ErrorPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.TimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        }

        var endpointUri = BuildEndpointUri();
        _logger?.LogDebug("Requesting patch from {Endpoint} with model {Model} (iteration {Iteration})",
            endpointUri, _options.Model, payload.Iteration);

        var requestBody = BuildRequestBody(payload);
        var requestJson = JsonSerializer.Serialize(requestBody, PayloadSerializerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM API request timed out after {_options.TimeoutSeconds} seconds.", ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("LLM endpoint returned HTTP {StatusCode}: {ErrorBody}",
                    (int)response.StatusCode, responseBody);

                throw new HttpRequestException(
                    $"OpenAI-compatible LLM endpoint failed with HTTP {(int)response.StatusCode} ({response.StatusCode}): {responseBody}",
                    null,
                    response.StatusCode);
            }

            var patchResponse = ParseLlmResponse(responseBody);
            _logger?.LogInformation("Successfully received and parsed patch response: {Count} patch(es)",
                patchResponse.Patches.Count);

            return patchResponse;
        }
    }

    private OpenAiChatCompletionRequest BuildRequestBody(ErrorPayload payload)
    {
        var userContent = JsonSerializer.Serialize(payload, PayloadSerializerOptions);

        return new OpenAiChatCompletionRequest
        {
            Model = _options.Model,
            Temperature = _options.Temperature,
            ResponseFormat = new OpenAiResponseFormat { Type = "json_object" },
            Messages = new[]
            {
                new OpenAiChatMessage { Role = "system", Content = SystemPrompt },
                new OpenAiChatMessage { Role = "user", Content = userContent }
            }
        };
    }

    private Uri BuildEndpointUri()
    {
        var rawUrl = !string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? _options.BaseUrl.Trim()
            : _httpClient.BaseAddress?.ToString().Trim() ?? "http://localhost:11434/v1";

        var trimmed = rawUrl.TrimEnd('/');
        string finalUrl;
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            finalUrl = trimmed;
        }
        else if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            finalUrl = $"{trimmed}/chat/completions";
        }
        else
        {
            finalUrl = $"{trimmed}/v1/chat/completions";
        }

        return new Uri(finalUrl, UriKind.RelativeOrAbsolute);
    }

    private static LlmPatchResponse ParseLlmResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new InvalidOperationException("LLM endpoint returned an empty response body.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // Check for error payload from endpoint: { "error": { "message": "..." } }
        if (root.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.ValueKind == JsonValueKind.Object &&
                               errorElement.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : errorElement.GetRawText();

            throw new InvalidOperationException($"LLM service returned error: {errorMessage}");
        }

        // Standard OpenAI chat completion format
        if (root.TryGetProperty("choices", out var choicesElement))
        {
            if (choicesElement.ValueKind != JsonValueKind.Array || choicesElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("LLM chat completion response contained no choices.");
            }

            var firstChoice = choicesElement[0];
            if (!firstChoice.TryGetProperty("message", out var messageElement))
            {
                throw new InvalidOperationException("LLM choice does not contain a 'message' property.");
            }

            if (!messageElement.TryGetProperty("content", out var contentElement))
            {
                throw new InvalidOperationException("LLM message does not contain a 'content' property.");
            }

            string rawContent;
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                rawContent = contentElement.GetString() ?? string.Empty;
            }
            else if (contentElement.ValueKind == JsonValueKind.Object)
            {
                rawContent = contentElement.GetRawText();
            }
            else
            {
                throw new InvalidOperationException($"Unexpected content value kind: {contentElement.ValueKind}");
            }

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                throw new InvalidOperationException("LLM message content is empty.");
            }

            var extractedJson = ExtractJsonPayload(rawContent);
            return DeserializePatchResponse(extractedJson);
        }

        // Direct response format (or unwrapped payload)
        var directExtracted = ExtractJsonPayload(responseBody);
        return DeserializePatchResponse(directExtracted);
    }

    private static string ExtractJsonPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var trimmed = content.Trim();

        // 1. Look for markdown code fences (```json ... ``` or ``` ... ```)
        int firstFence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (firstFence >= 0)
        {
            int endOfFirstLine = trimmed.IndexOf('\n', firstFence);
            if (endOfFirstLine >= 0)
            {
                int closingFence = trimmed.IndexOf("```", endOfFirstLine, StringComparison.Ordinal);
                if (closingFence >= 0)
                {
                    var snippet = trimmed.Substring(endOfFirstLine + 1, closingFence - endOfFirstLine - 1).Trim();
                    int sBrace = snippet.IndexOf('{');
                    int eBrace = snippet.LastIndexOf('}');
                    if (sBrace >= 0 && eBrace > sBrace)
                    {
                        return snippet.Substring(sBrace, eBrace - sBrace + 1);
                    }
                }
            }
        }

        // 2. Look for outermost JSON object { ... }
        int startBrace = trimmed.IndexOf('{');
        int endBrace = trimmed.LastIndexOf('}');
        if (startBrace >= 0 && endBrace > startBrace)
        {
            return trimmed.Substring(startBrace, endBrace - startBrace + 1);
        }

        return trimmed;
    }

    private static LlmPatchResponse DeserializePatchResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Failed to locate valid JSON payload in LLM output.");
        }

        var raw = JsonSerializer.Deserialize<RawPatchResponse>(json, DeserializerOptions)
            ?? throw new JsonException("Deserialized patch response was null.");

        var patches = raw.Patches != null
            ? (IReadOnlyList<FilePatchSuggestion>)raw.Patches
                .Where(p => p != null)
                .Select(p => p!.ToModel())
                .ToList()
            : Array.Empty<FilePatchSuggestion>();

        return new LlmPatchResponse(
            raw.Explanation ?? string.Empty,
            patches
        );
    }

    private sealed class OpenAiChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("response_format")]
        public OpenAiResponseFormat ResponseFormat { get; init; } = new();

        [JsonPropertyName("messages")]
        public IReadOnlyList<OpenAiChatMessage> Messages { get; init; } = Array.Empty<OpenAiChatMessage>();
    }

    private sealed class OpenAiResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "json_object";
    }

    private sealed class OpenAiChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }

    private sealed class RawPatchResponse
    {
        [JsonPropertyName("explanation")]
        public string? Explanation { get; set; }

        [JsonPropertyName("patches")]
        public List<RawPatchSuggestion?>? Patches { get; set; }
    }

    private sealed class RawPatchSuggestion
    {
        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("file_path")]
        public string? FilePathSnake { get; set; }

        [JsonPropertyName("file")]
        public string? FileShort { get; set; }

        [JsonPropertyName("path")]
        public string? PathShort { get; set; }

        [JsonPropertyName("searchSnippet")]
        public string? SearchSnippet { get; set; }

        [JsonPropertyName("search_snippet")]
        public string? SearchSnippetSnake { get; set; }

        [JsonPropertyName("search")]
        public string? SearchShort { get; set; }

        [JsonPropertyName("replaceSnippet")]
        public string? ReplaceSnippet { get; set; }

        [JsonPropertyName("replace_snippet")]
        public string? ReplaceSnippetSnake { get; set; }

        [JsonPropertyName("replace")]
        public string? ReplaceShort { get; set; }

        public FilePatchSuggestion ToModel() => new(
            FilePath ?? FilePathSnake ?? FileShort ?? PathShort ?? string.Empty,
            SearchSnippet ?? SearchSnippetSnake ?? SearchShort ?? string.Empty,
            ReplaceSnippet ?? ReplaceSnippetSnake ?? ReplaceShort ?? string.Empty
        );
    }
}
