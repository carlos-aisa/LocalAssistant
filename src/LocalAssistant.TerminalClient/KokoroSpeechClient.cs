using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAssistant.TerminalClient;

internal sealed record KokoroServiceOptions(Uri Endpoint, TimeSpan HealthTimeout, TimeSpan SynthesisTimeout)
{
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultSynthesisTimeout = TimeSpan.FromSeconds(18);

    public static KokoroServiceOptions Create(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
        {
            throw new ArgumentException("The Kokoro endpoint must use HTTP and target loopback.", nameof(endpoint));
        }

        return new KokoroServiceOptions(endpoint, DefaultHealthTimeout, DefaultSynthesisTimeout);
    }
}

internal sealed record KokoroVoice(string Id, string Language);

internal sealed record KokoroHealth(string Status, string ApiVersion, string? Code);

internal sealed record KokoroSpeechRequest(
    string Text,
    string Voice,
    string Language,
    double Speed,
    int Volume);

internal enum KokoroClientFailureKind
{
    NotConfigured,
    Unavailable,
    Unauthorized,
    Busy,
    Timeout,
    InvalidResponse,
    SynthesisFailed,
}

internal sealed record KokoroClientResult<T>(T? Value, KokoroClientFailureKind? Failure)
{
    public bool IsSuccess => Failure is null;

    public static KokoroClientResult<T> Success(T value) => new(value, null);

    public static KokoroClientResult<T> Failed(KokoroClientFailureKind failure) => new(default, failure);
}

/// <summary>
/// HTTP boundary for the loopback Kokoro process. It never retries a request after
/// dispatch because the service may have synthesized an otherwise unknown result.
/// </summary>
internal sealed class KokoroSpeechClient
{
    private const int MaximumWavBytes = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Func<byte[]?> _getSecret;
    private readonly KokoroServiceOptions _options;

    public KokoroSpeechClient(
        HttpClient httpClient,
        Func<byte[]?> getSecret,
        KokoroServiceOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _getSecret = getSecret ?? throw new ArgumentNullException(nameof(getSecret));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<KokoroClientResult<KokoroHealth>> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "health");
        if (request is null)
        {
            return KokoroClientResult<KokoroHealth>.Failed(KokoroClientFailureKind.NotConfigured);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HealthTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var failure = MapFailure(response.StatusCode);
            if (failure is not null)
            {
                return KokoroClientResult<KokoroHealth>.Failed(failure.Value);
            }

            var payloadJson = await response.Content.ReadAsStringAsync(timeout.Token);
            var payload = JsonSerializer.Deserialize<KokoroHealthResponse>(payloadJson, JsonOptions);
            if (payload is null || payload.Status is not ("loading" or "ready" or "busy" or "degraded") ||
                payload.ApiVersion != "v1")
            {
                return KokoroClientResult<KokoroHealth>.Failed(KokoroClientFailureKind.InvalidResponse);
            }

            return KokoroClientResult<KokoroHealth>.Success(
                new KokoroHealth(payload.Status, payload.ApiVersion, payload.Code));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KokoroClientResult<KokoroHealth>.Failed(KokoroClientFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            return KokoroClientResult<KokoroHealth>.Failed(KokoroClientFailureKind.Unavailable);
        }
        catch (JsonException)
        {
            return KokoroClientResult<KokoroHealth>.Failed(KokoroClientFailureKind.InvalidResponse);
        }
    }

    public async Task<KokoroClientResult<IReadOnlyList<KokoroVoice>>> GetVoicesAsync(
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/voices");
        if (request is null)
        {
            return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(KokoroClientFailureKind.NotConfigured);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var failure = MapFailure(response.StatusCode);
            if (failure is not null)
            {
                return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(failure.Value);
            }

            var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<KokoroVoiceResponse>(payloadJson, JsonOptions);
            if (payload?.Voices is null || payload.Voices.Any(voice =>
                string.IsNullOrWhiteSpace(voice.Id) || voice.Language is not ("es" or "en")))
            {
                return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(KokoroClientFailureKind.InvalidResponse);
            }

            var voices = payload.Voices
                .Select(voice => new KokoroVoice(voice.Id!, voice.Language!))
                .ToArray();
            return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Success(voices);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(KokoroClientFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(KokoroClientFailureKind.Unavailable);
        }
        catch (JsonException)
        {
            return KokoroClientResult<IReadOnlyList<KokoroVoice>>.Failed(KokoroClientFailureKind.InvalidResponse);
        }
    }

    public async Task<KokoroClientResult<SynthesizedSpeech>> SynthesizeAsync(
        KokoroSpeechRequest speechRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(speechRequest);
        using var request = CreateRequest(HttpMethod.Post, "v1/speech");
        if (request is null)
        {
            return KokoroClientResult<SynthesizedSpeech>.Failed(KokoroClientFailureKind.NotConfigured);
        }

        var requestJson = JsonSerializer.Serialize(speechRequest, JsonOptions);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SynthesisTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var failure = MapFailure(response.StatusCode);
            if (failure is not null)
            {
                return KokoroClientResult<SynthesizedSpeech>.Failed(failure.Value);
            }
            if (response.Content.Headers.ContentType?.MediaType is not "audio/wav" ||
                response.Content.Headers.ContentLength is > MaximumWavBytes)
            {
                return KokoroClientResult<SynthesizedSpeech>.Failed(KokoroClientFailureKind.InvalidResponse);
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            var wav = await CopyBoundedAsync(source, timeout.Token);
            if (wav is null || !KokoroWaveValidator.IsValid(wav, out _))
            {
                return KokoroClientResult<SynthesizedSpeech>.Failed(KokoroClientFailureKind.InvalidResponse);
            }

            return KokoroClientResult<SynthesizedSpeech>.Success(new SynthesizedSpeech(
                new SensitiveMemoryStream(wav),
                "audio/wav",
                speechRequest.Voice));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KokoroClientResult<SynthesizedSpeech>.Failed(KokoroClientFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            return KokoroClientResult<SynthesizedSpeech>.Failed(KokoroClientFailureKind.Unavailable);
        }
    }

    private HttpRequestMessage? CreateRequest(HttpMethod method, string relativeUri)
    {
        var secret = _getSecret();
        if (secret is null || secret.Length != 32)
        {
            return null;
        }

        try
        {
            var bearer = Base64Url.Encode(secret);
            var request = new HttpRequestMessage(method, new Uri(_options.Endpoint, relativeUri));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            return request;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static KokoroClientFailureKind? MapFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => null,
        HttpStatusCode.Unauthorized => KokoroClientFailureKind.Unauthorized,
        HttpStatusCode.ServiceUnavailable => KokoroClientFailureKind.Busy,
        HttpStatusCode.GatewayTimeout => KokoroClientFailureKind.Timeout,
        _ => KokoroClientFailureKind.SynthesisFailed,
    };

    private static async Task<byte[]?> CopyBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    return destination.ToArray();
                }
                if (destination.Length > MaximumWavBytes - bytesRead)
                {
                    return null;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private sealed record KokoroVoiceResponse(IReadOnlyList<KokoroVoiceResponseItem>? Voices);

    private sealed record KokoroVoiceResponseItem(string? Id, string? Language);

    private sealed record KokoroHealthResponse(string? Status, string? ApiVersion, string? Code);
}

internal static class KokoroWaveValidator
{
    public static bool IsValid(ReadOnlySpan<byte> wav, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (wav.Length < 44 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(4, 4)) != (uint)(wav.Length - 8))
        {
            return false;
        }

        var offset = 12;
        var hasFormat = false;
        var dataLength = -1;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            offset += 8;
            if (size > int.MaxValue || offset > wav.Length - (int)size)
            {
                return false;
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    return false;
                }

                var format = wav.Slice(offset, 16);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2, 2)) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4, 4)) != 24000 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(8, 4)) != 48000 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(12, 2)) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14, 2)) != 16)
                {
                    return false;
                }
                hasFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataLength = (int)size;
                offset += dataLength + (dataLength & 1);
                break;
            }

            offset += (int)size + ((int)size & 1);
        }

        if (!hasFormat || dataLength < 0 || dataLength % 2 != 0 || offset != wav.Length)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(dataLength / 48000d);
        return duration <= TimeSpan.FromSeconds(45);
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
