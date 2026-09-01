namespace LocalAssistant.Api.Security;

public sealed class PrivateClientOptions
{
    public const string SectionName = "LocalAssistant:PrivateClients";

    public string? DatabasePath { get; set; }

    public TimeSpan AdministrativeChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
}
