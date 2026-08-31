namespace LocalAssistant.Core.Memory;

public enum MemoryScopeKind
{
    Personal,
    HouseholdShared,
    Module,
    Administrative,
    Ephemeral,
}

public sealed record MemoryPartition
{
    private MemoryPartition(
        MemoryScopeKind scope,
        string? ownerPrincipalId,
        string? householdId,
        string? moduleId,
        string? installationId)
    {
        Scope = scope;
        OwnerPrincipalId = ownerPrincipalId;
        HouseholdId = householdId;
        ModuleId = moduleId;
        InstallationId = installationId;
    }

    public MemoryScopeKind Scope { get; }

    public string? OwnerPrincipalId { get; }

    public string? HouseholdId { get; }

    public string? ModuleId { get; }

    public string? InstallationId { get; }

    public static MemoryPartition Personal(string ownerPrincipalId) => new(
        MemoryScopeKind.Personal,
        RequireIdentifier(ownerPrincipalId, nameof(ownerPrincipalId)),
        null,
        null,
        null);

    public static MemoryPartition HouseholdShared(string householdId) => new(
        MemoryScopeKind.HouseholdShared,
        null,
        RequireIdentifier(householdId, nameof(householdId)),
        null,
        null);

    public static MemoryPartition Module(string householdId, string moduleId) => new(
        MemoryScopeKind.Module,
        null,
        RequireIdentifier(householdId, nameof(householdId)),
        RequireIdentifier(moduleId, nameof(moduleId)),
        null);

    public static MemoryPartition Administrative(string installationId) => new(
        MemoryScopeKind.Administrative,
        null,
        null,
        null,
        RequireIdentifier(installationId, nameof(installationId)));

    public static MemoryPartition Ephemeral() => new(
        MemoryScopeKind.Ephemeral,
        null,
        null,
        null,
        null);

    private static string RequireIdentifier(string value, string parameterName)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue) || normalizedValue.Length > 128)
        {
            throw new ArgumentException("A memory partition identifier is invalid.", parameterName);
        }

        return normalizedValue;
    }
}
