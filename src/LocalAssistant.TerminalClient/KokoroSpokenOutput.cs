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
