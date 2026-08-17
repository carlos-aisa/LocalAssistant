namespace LocalAssistant.Core.LanguageModels;

public sealed class ScriptedLanguageProvider : ILanguageProvider
{
    private readonly Queue<Func<LanguageProviderRequest, LanguageProviderResponse>> _steps;
    private readonly object _syncRoot = new();

    public ScriptedLanguageProvider(
        IEnumerable<Func<LanguageProviderRequest, LanguageProviderResponse>> steps,
        string name = "fake")
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<Func<LanguageProviderRequest, LanguageProviderResponse>>(steps);
        Name = name;
    }

    public string Name { get; }

    public int CallCount { get; private set; }

    public Task<LanguageProviderResponse> GetResponseAsync(
        LanguageProviderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Func<LanguageProviderRequest, LanguageProviderResponse> step;
        lock (_syncRoot)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The scripted provider has no response steps left.");
            }

            step = _steps.Dequeue();
            CallCount++;
        }

        return Task.FromResult(step(request));
    }

    public static Func<LanguageProviderRequest, LanguageProviderResponse> Return(
        LanguageProviderResponse response)
    {
        return _ => response;
    }
}
