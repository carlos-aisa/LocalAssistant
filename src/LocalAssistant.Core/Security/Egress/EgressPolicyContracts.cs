namespace LocalAssistant.Core.Security.Egress;

public sealed record DataCategory
{
    public DataCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A data category name is required.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    public static DataCategory SourceCode { get; } = new("SOURCE_CODE");

    public static DataCategory RepositoryData { get; } = new("REPOSITORY_DATA");

    public static DataCategory LocalFiles { get; } = new("LOCAL_FILES");

    public static DataCategory LocalDocuments { get; } = new("LOCAL_DOCUMENTS");

    public static DataCategory RagContent { get; } = new("RAG_CONTENT");

    public static DataCategory Memory { get; } = new("MEMORY");

    public static DataCategory Conversations { get; } = new("CONVERSATIONS");

    public static DataCategory DatabaseData { get; } = new("DATABASE_DATA");

    public static DataCategory Secrets { get; } = new("SECRETS");

    public static DataCategory Credentials { get; } = new("CREDENTIALS");

    public static DataCategory Environment { get; } = new("ENVIRONMENT");

    public static DataCategory PrivateConfiguration { get; } = new("PRIVATE_CONFIG");

    public static DataCategory Location { get; } = new("LOCATION");

    public static DataCategory SearchQuery { get; } = new("SEARCH_QUERY");

    public static DataCategory PublicData { get; } = new("PUBLIC_DATA");
}

public sealed record EgressPayloadField
{
    public EgressPayloadField(
        string name,
        IReadOnlyList<DataCategory> categories,
        bool isRequiredForPurpose,
        bool isSanitized)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A payload field name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0)
        {
            throw new ArgumentException("A payload field must have at least one data category.", nameof(categories));
        }

        Name = name;
        Categories = [.. categories];
        IsRequiredForPurpose = isRequiredForPurpose;
        IsSanitized = isSanitized;
    }

    public string Name { get; }

    public IReadOnlyList<DataCategory> Categories { get; }

    public bool IsRequiredForPurpose { get; }

    public bool IsSanitized { get; }
}

public sealed record EgressRequest
{
    public EgressRequest(string destination, string purpose, IReadOnlyList<EgressPayloadField> fields)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("A destination is required.", nameof(destination));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException("A purpose is required.", nameof(purpose));
        }

        ArgumentNullException.ThrowIfNull(fields);
        Destination = destination;
        Purpose = purpose;
        Fields = [.. fields];
    }

    public string Destination { get; }

    public string Purpose { get; }

    public IReadOnlyList<EgressPayloadField> Fields { get; }
}

public enum EgressPolicyEffect
{
    Deny,
    Allow,
    AllowWhenRequired,
    AllowWhenSanitized,
}

public enum EgressDecisionCode
{
    Allowed,
    EmptyPayload,
    UnknownDataCategory,
    DataCategoryDenied,
    LocationNotRequired,
    SanitizationRequired,
}

public sealed record EgressDecision(
    bool IsAllowed,
    EgressDecisionCode Code,
    IReadOnlyList<string> FieldNames);

public interface IEgressPolicy
{
    EgressDecision Evaluate(EgressRequest request);
}
