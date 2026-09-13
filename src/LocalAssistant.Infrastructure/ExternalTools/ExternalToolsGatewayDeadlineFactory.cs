namespace LocalAssistant.Infrastructure.ExternalTools;

/// <summary>
/// Creates the cancellation source that bounds one gateway operation.
/// </summary>
public interface IExternalToolsGatewayDeadlineFactory
{
    CancellationTokenSource Create(TimeSpan timeout);
}

public sealed class TimeProviderExternalToolsGatewayDeadlineFactory : IExternalToolsGatewayDeadlineFactory
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderExternalToolsGatewayDeadlineFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public CancellationTokenSource Create(TimeSpan timeout) =>
        new CancellationTokenSource(timeout, _timeProvider);
}
