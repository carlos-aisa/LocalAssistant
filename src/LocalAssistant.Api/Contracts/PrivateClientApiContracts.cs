namespace LocalAssistant.Api.Contracts;

public sealed record CreatePrivateSessionRequest(string ClientId, string Credential);

public sealed record PrivateSessionResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record CompletePrivateClientPairingRequest(
    string Challenge,
    string DisplayName);

public sealed record PrivateClientCredentialResponse(
    string ClientId,
    string DisplayName,
    string Credential);

public sealed record ConsumeAdministrativeChallengeRequest(string Challenge);
