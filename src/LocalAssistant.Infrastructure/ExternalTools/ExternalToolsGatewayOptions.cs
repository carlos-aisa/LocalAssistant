namespace LocalAssistant.Infrastructure.ExternalTools;

public sealed class ExternalToolsGatewayOptions
{
    public const string SectionName = "LocalAssistant:ExternalToolsGateway";

    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaximumConcurrentOperations { get; init; } = 1;

    public int MaximumNormalizedResultBytes { get; init; } = 32 * 1024;
}
