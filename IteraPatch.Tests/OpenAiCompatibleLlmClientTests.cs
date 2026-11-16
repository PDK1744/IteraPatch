namespace IteraPatch.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IteraPatch.Core.Configuration;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class OpenAiCompatibleLlmClientTests
{
    private static readonly ErrorPayload SamplePayload = new(
        ErrorType: "AssertionError",
        ErrorMessage: "assert 4 == 5",
        StackTrace: "Traceback (most recent call last):\n  File \"tests/test_math.py\", line 12, in test_add\n    assert add(2, 2) == 5\nAssertionError",
        FailingFile: "src/math.py",
        FailingLine: 4,
        GitDiff: "diff --git a/src/math.py b/src/math.py\n--- a/src/math.py\n+++ b/src/math.py\n@@ -3,2 +3,2 @@\n def add(a, b):\n-    return a + b + 1\n+    return a + b",
        TestCommand: "pytest",
        Iteration: 1
    );

    [Fact]
    public void Constructor_ThrowsOnNullHttpClient()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenAiCompatibleLlmClient(null!));
    }

    [Fact]
    public async Task RequestPatchAsync_ThrowsOnNullPayload()
    {
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.RequestPatchAsync(null!));
    }

    [Fact]
    public async Task RequestPatchAsync_SendsCorrectRequestStructureAndHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var mockResponseContent = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "{\"explanation\": \"Fixed bug\", \"patches\": [{\"filePath\": \"src/math.py\", \"searchSnippet\": \"return a + b + 1\", \"replaceSnippet\": \"return a + b\"}]}"
              },
              "finish_reason": "stop"
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            capturedRequest = req;
            capturedBody = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponseContent, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var options = new LlmOptions
        {
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = "test-secret-key",
            Model = "gpt-4o-mini",
            Temperature = 0.0
        };

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient, options);

        var result = await client.RequestPatchAsync(SamplePayload);

        // Verify HTTP request
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("http://localhost:11434/v1/chat/completions", capturedRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-secret-key", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Contains(capturedRequest.Headers.Accept, a => a.MediaType == "application/json");

        // Verify JSON payload structure
        Assert.NotNull(capturedBody);
        using var jsonDoc = JsonDocument.Parse(capturedBody);
        var root = jsonDoc.RootElement;

        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal(0.0, root.GetProperty("temperature").GetDouble());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        var systemMsg = messages[0];
        Assert.Equal("system", systemMsg.GetProperty("role").GetString());
        Assert.Contains("You are a code verification engineer.", systemMsg.GetProperty("content").GetString());

        var userMsg = messages[1];
        Assert.Equal("user", userMsg.GetProperty("role").GetString());
        var userContent = userMsg.GetProperty("content").GetString();
        Assert.NotNull(userContent);

        // Deserialize inner payload in user content
        using var userDoc = JsonDocument.Parse(userContent);
        var userRoot = userDoc.RootElement;
        Assert.Equal("AssertionError", userRoot.GetProperty("errorType").GetString());
        Assert.Equal("assert 4 == 5", userRoot.GetProperty("errorMessage").GetString());
        Assert.Equal("src/math.py", userRoot.GetProperty("failingFile").GetString());
        Assert.Equal(4, userRoot.GetProperty("failingLine").GetInt32());
        Assert.Contains("diff --git", userRoot.GetProperty("gitDiff").GetString());
        Assert.Equal("pytest", userRoot.GetProperty("testCommand").GetString());
        Assert.Equal(1, userRoot.GetProperty("iteration").GetInt32());

        // Verify parsed result
        Assert.Equal("Fixed bug", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/math.py", result.Patches[0].FilePath);
        Assert.Equal("return a + b + 1", result.Patches[0].SearchSnippet);
        Assert.Equal("return a + b", result.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public async Task RequestPatchAsync_NoApiKey_DoesNotSetAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "content": "{\"explanation\": \"ok\", \"patches\": []}"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var options = new LlmOptions
        {
            ApiKey = "" // Empty key
        };

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient, options);

        await client.RequestPatchAsync(SamplePayload);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Headers.Authorization);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:8000", "http://localhost:8000/v1/chat/completions")]
    [InlineData("http://localhost:8000/", "http://localhost:8000/v1/chat/completions")]
    public async Task RequestPatchAsync_EndpointNormalization_ConstructsExpectedUri(string configuredBaseUrl, string expectedUri)
    {
        Uri? requestedUri = null;
        var mockResponse = """{"choices":[{"message":{"content":"{\"explanation\":\"ok\",\"patches\":[]}"}}]}""";

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            requestedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var options = new LlmOptions { BaseUrl = configuredBaseUrl };
        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient, options);

        await client.RequestPatchAsync(SamplePayload);

        Assert.Equal(expectedUri, requestedUri?.ToString());
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_ExtractsFromMarkdownCodeFence()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "```json\n{\n  \"explanation\": \"Extracted from markdown fence\",\n  \"patches\": [\n    {\n      \"filePath\": \"src/app.py\",\n      \"searchSnippet\": \"old\",\n      \"replaceSnippet\": \"new\"\n    }\n  ]\n}\n```"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Extracted from markdown fence", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/app.py", result.Patches[0].FilePath);
        Assert.Equal("old", result.Patches[0].SearchSnippet);
        Assert.Equal("new", result.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_ExtractsFromMarkdownFenceWithoutLanguage()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "```\n{\n  \"explanation\": \"No language fence\",\n  \"patches\": []\n}\n```"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("No language fence", result.Explanation);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_ExtractsWithSurroundingCommentary()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "Here is the patch you asked for:\n\n```json\n{\n  \"explanation\": \"Surrounded by text\",\n  \"patches\": [\n    {\n      \"filePath\": \"src/calc.py\",\n      \"searchSnippet\": \"x - 1\",\n      \"replaceSnippet\": \"x + 1\"\n    }\n  ]\n}\n```\n\nPlease let me know if tests pass!"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Surrounded by text", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/calc.py", result.Patches[0].FilePath);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_SupportsDirectUnwrappedPatchResponse()
    {
        var directJson = """
        {
          "explanation": "Direct response without choices envelope",
          "patches": [
            {
              "filePath": "src/direct.py",
              "searchSnippet": "alpha",
              "replaceSnippet": "beta"
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Direct response without choices envelope", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/direct.py", result.Patches[0].FilePath);
        Assert.Equal("alpha", result.Patches[0].SearchSnippet);
        Assert.Equal("beta", result.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_SupportsSnakeCaseFieldNames()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "content": "{\"explanation\": \"Snake case test\", \"patches\": [{\"file_path\": \"src/snake.py\", \"search_snippet\": \"a\", \"replace_snippet\": \"b\"}]}"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Snake case test", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/snake.py", result.Patches[0].FilePath);
        Assert.Equal("a", result.Patches[0].SearchSnippet);
        Assert.Equal("b", result.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_SupportsPascalCaseFieldNames()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "content": "{\"Explanation\": \"Pascal case test\", \"Patches\": [{\"FilePath\": \"src/pascal.py\", \"SearchSnippet\": \"foo\", \"ReplaceSnippet\": \"bar\"}]}"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Pascal case test", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/pascal.py", result.Patches[0].FilePath);
        Assert.Equal("foo", result.Patches[0].SearchSnippet);
        Assert.Equal("bar", result.Patches[0].ReplaceSnippet);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_HandlesNullPatchesAndExplanation()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "content": "{}"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal(string.Empty, result.Explanation);
        Assert.NotNull(result.Patches);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_HandlesContentAsJsonObject()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": {
                  "explanation": "Direct JSON object content",
                  "patches": [
                    {
                      "filePath": "src/obj.py",
                      "searchSnippet": "1",
                      "replaceSnippet": "2"
                    }
                  ]
                }
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Direct JSON object content", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/obj.py", result.Patches[0].FilePath);
    }

    [Fact]
    public async Task RequestPatchAsync_Resilient_ToleratesTrailingCommasAndComments()
    {
        var mockResponse = """
        {
          "choices": [
            {
              "message": {
                "content": "{\n  // This is a comment\n  \"explanation\": \"Trailing comma test\",\n  \"patches\": [\n    {\n      \"filePath\": \"src/test.py\",\n      \"searchSnippet\": \"x\",\n      \"replaceSnippet\": \"y\",\n    },\n  ],\n}"
              }
            }
          ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var result = await client.RequestPatchAsync(SamplePayload);

        Assert.Equal("Trailing comma test", result.Explanation);
        Assert.Single(result.Patches);
        Assert.Equal("src/test.py", result.Patches[0].FilePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Bad request: invalid parameter")]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized: invalid api key")]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limit exceeded")]
    [InlineData(HttpStatusCode.InternalServerError, "Internal server error")]
    public async Task RequestPatchAsync_HttpErrorStatusCodes_ThrowsHttpRequestException(HttpStatusCode statusCode, string errorMessage)
    {
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(errorMessage, System.Text.Encoding.UTF8, "text/plain")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestPatchAsync(SamplePayload));
        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Contains(errorMessage, ex.Message);
    }

    [Fact]
    public async Task RequestPatchAsync_EmptyResponseBody_ThrowsInvalidOperationException()
    {
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestPatchAsync(SamplePayload));
    }

    [Fact]
    public async Task RequestPatchAsync_NonJsonResponseBody_ThrowsJsonException()
    {
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>Gateway Error</html>", System.Text.Encoding.UTF8, "text/html")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        await Assert.ThrowsAnyAsync<JsonException>(() => client.RequestPatchAsync(SamplePayload));
    }

    [Fact]
    public async Task RequestPatchAsync_EmptyChoicesArray_ThrowsInvalidOperationException()
    {
        var mockResponse = """{"choices": []}""";
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestPatchAsync(SamplePayload));
        Assert.Contains("contained no choices", ex.Message);
    }

    [Fact]
    public async Task RequestPatchAsync_ApiErrorObjectIn200_ThrowsInvalidOperationException()
    {
        var mockResponse = """
        {
          "error": {
            "message": "Model 'gpt-4o-mini' does not exist",
            "type": "invalid_request_error"
          }
        }
        """;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponse, System.Text.Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestPatchAsync(SamplePayload));
        Assert.Contains("Model 'gpt-4o-mini' does not exist", ex.Message);
    }

    [Fact]
    public async Task RequestPatchAsync_PreCancelledToken_ThrowsOperationCanceledExceptionImmediately()
    {
        var handlerInvoked = false;
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            handlerInvoked = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestPatchAsync(SamplePayload, cts.Token));
        Assert.False(handlerInvoked);
    }

    [Fact]
    public async Task RequestPatchAsync_TimeoutConfigured_ThrowsTimeoutException()
    {
        var mockHandler = new MockHttpMessageHandler(async (req, ct) =>
        {
            await Task.Delay(2000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LlmOptions
        {
            TimeoutSeconds = 1
        };

        using var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleLlmClient(httpClient, options);

        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestPatchAsync(SamplePayload));
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
