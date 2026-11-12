using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeSystem.Ai.Providers.Abstractions;
using KnowledgeSystem.Ai.Providers.Transport;

namespace KnowledgeSystem.Ai.Providers.Protocols.Mistral;

public sealed class MistralChatCompletionsProtocol : IChatProtocol
{
    public const string ProtocolIdValue = "mistral-chat-completions";

    private const int DefaultNetworkTimeoutSeconds = 600;
    private const int MaxRawErrorBodyLength = 4096;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProtocolId => ProtocolIdValue;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatProtocolRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var timeoutCts = CreateTimeoutTokenSource(request.NetworkTimeoutSeconds, cancellationToken);
        var token = timeoutCts.Token;

        using var httpRequest = CreateHttpRequest(request);
        HttpResponseMessage? httpResponse = null;
        ChatError? requestError = null;

        try
        {
            httpResponse = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                token
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            requestError = new ChatError
            {
                Message = "The request was cancelled.",
                Code = "cancelled",
                IsRetryable = false
            };
        }
        catch (OperationCanceledException)
        {
            requestError = new ChatError
            {
                Message = "The request timed out.",
                Code = "timeout",
                IsRetryable = true
            };
        }
        catch (HttpRequestException exception)
        {
            requestError = new ChatError
            {
                Message = $"The request failed: {exception.Message}",
                Code = "http_request_failed",
                IsRetryable = true
            };
        }

        if (requestError != null)
        {
            yield return new StreamErrorEvent(requestError);
            yield break;
        }

        var response = httpResponse!;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rawBody = await response.Content.ReadAsStringAsync(token);
                yield return new StreamErrorEvent(CreateHttpError(response, rawBody));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var state = new MistralStreamState();
            var started = false;
            string? responseId = null;
            string? modelId = null;

            await foreach (var data in SseReader.ReadDataAsync(stream, token))
            {
                MistralCompletionChunk? chunk = null;
                ChatError? parseError = null;

                try
                {
                    chunk = JsonSerializer.Deserialize<MistralCompletionChunk>(data, JsonOptions);
                }
                catch (JsonException exception)
                {
                    parseError = new ChatError
                    {
                        Message = $"Failed to parse the Mistral stream: {exception.Message}",
                        Code = "invalid_stream",
                        IsRetryable = false
                    };
                }

                if (parseError != null)
                {
                    yield return new StreamErrorEvent(parseError);
                    yield break;
                }

                if (chunk == null)
                {
                    continue;
                }

                responseId ??= chunk.Id;
                modelId ??= chunk.Model;

                if (!started)
                {
                    started = true;
                    yield return new StreamStartedEvent(chunk.Id, chunk.Model);
                }

                foreach (var streamEvent in state.Process(chunk))
                {
                    yield return streamEvent;
                }

                if (chunk.Usage != null)
                {
                    yield return new UsageReceivedEvent(MistralStreamState.MapUsage(chunk.Usage));
                }
            }

            var finalEvents = new List<ChatStreamEvent>();
            state.FinalizeRemaining(finalEvents);

            foreach (var finalEvent in finalEvents)
            {
                yield return finalEvent;
            }

            yield return new StreamCompletedEvent(
                state.FinishReason,
                state.BuildMessage(responseId, modelId)
            );
        }
    }

    private static CancellationTokenSource CreateTimeoutTokenSource(
        int? networkTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = networkTimeoutSeconds is > 0
            ? networkTimeoutSeconds.Value
            : DefaultNetworkTimeoutSeconds;

        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        return cts;
    }

    private static HttpRequestMessage CreateHttpRequest(ChatProtocolRequest request)
    {
        var url = request.Endpoint.ToString().TrimEnd('/') + "/chat/completions";
        var body = MistralRequestMapper.Map(request.Request);
        var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(json)
        };

        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        foreach (var header in request.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return httpRequest;
    }

    private static ChatError CreateHttpError(HttpResponseMessage response, string rawBody)
    {
        var statusCode = (int)response.StatusCode;

        var message = statusCode switch
        {
            401 => "The provider rejected the API key.",
            403 => "The provider denied access to the model.",
            404 => "The provider did not find the requested model or endpoint.",
            429 => "The provider rate limit was exceeded.",
            >= 500 => "The provider returned a server error.",
            _ => $"The provider returned HTTP {statusCode}."
        };

        return new ChatError
        {
            Message = message,
            Code = response.StatusCode.ToString(),
            StatusCode = statusCode,
            IsRetryable = statusCode == 429 || statusCode >= 500,
            RawBody = Truncate(rawBody, MaxRawErrorBodyLength)
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
