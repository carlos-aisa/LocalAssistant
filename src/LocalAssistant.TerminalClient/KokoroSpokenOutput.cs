namespace LocalAssistant.TerminalClient;

internal sealed class KokoroSpeechSynthesizer : ISpeechSynthesizer, ISpeechVoiceCatalog
{
    private readonly KokoroSpeechClient _client;

    public KokoroSpeechSynthesizer(KokoroSpeechClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var result = await _client.GetVoicesAsync(cancellationToken);
        return result.IsSuccess
            ? result.Value!.Select(voice => new SpokenOutputVoice(voice.Id)).ToArray()
            : [];
    }

    public async Task<SynthesizedSpeech> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preferences = request.Preferences;
        var result = await _client.SynthesizeAsync(
            new KokoroSpeechRequest(
                request.Text,
                preferences.KokoroProfileId ?? "jarvis-es",
                preferences.KokoroLanguage,
                Speed: 1.0,
                Volume: preferences.KokoroVolume),
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new KokoroSpeechException(result.Failure!.Value);
        }

        return result.Value!;
    }
}

internal sealed class KokoroSpeechException(KokoroClientFailureKind failure) : Exception
{
    public KokoroClientFailureKind Failure { get; } = failure;
}

internal sealed class ProviderSelectingSpeechSynthesizer : ISpeechSynthesizer, ISpeechVoiceCatalog
{
    private readonly ISpeechSynthesizer _sapi;
    private readonly KokoroSpeechSynthesizer? _kokoro;

    public ProviderSelectingSpeechSynthesizer(ISpeechSynthesizer sapi, KokoroSpeechSynthesizer? kokoro)
    {
        _sapi = sapi ?? throw new ArgumentNullException(nameof(sapi));
        _kokoro = kokoro;
    }

    public async Task<SynthesizedSpeech> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Preferences.RequestedProvider == SpokenOutputProvider.None)
        {
            throw new InvalidOperationException("Spoken output is disabled.");
        }

        if (request.Preferences.RequestedProvider != SpokenOutputProvider.Kokoro || _kokoro is null)
        {
            return await _sapi.SynthesizeAsync(request, cancellationToken);
        }

        try
        {
            return await _kokoro.SynthesizeAsync(request, cancellationToken);
        }
        catch (KokoroSpeechException) when (request.Preferences.UseSapiFallback)
        {
            return await _sapi.SynthesizeAsync(request, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var sapi = _sapi is ISpeechVoiceCatalog sapiCatalog
            ? await sapiCatalog.GetVoicesAsync(cancellationToken)
            : [];
        var kokoro = _kokoro is null
            ? []
            : await _kokoro.GetVoicesAsync(cancellationToken);
        return sapi.Concat(kokoro).OrderBy(voice => voice.Id, StringComparer.Ordinal).ToArray();
    }
}
