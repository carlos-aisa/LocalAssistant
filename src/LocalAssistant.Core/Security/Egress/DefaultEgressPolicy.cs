namespace LocalAssistant.Core.Security.Egress;

public sealed class DefaultEgressPolicy : IEgressPolicy
{
    private static readonly Dictionary<DataCategory, EgressPolicyEffect> Effects =
        new Dictionary<DataCategory, EgressPolicyEffect>
        {
            [DataCategory.SourceCode] = EgressPolicyEffect.Deny,
            [DataCategory.RepositoryData] = EgressPolicyEffect.Deny,
            [DataCategory.LocalFiles] = EgressPolicyEffect.Deny,
            [DataCategory.LocalDocuments] = EgressPolicyEffect.Deny,
            [DataCategory.RagContent] = EgressPolicyEffect.Deny,
            [DataCategory.Memory] = EgressPolicyEffect.Deny,
            [DataCategory.Conversations] = EgressPolicyEffect.Deny,
            [DataCategory.DatabaseData] = EgressPolicyEffect.Deny,
            [DataCategory.Secrets] = EgressPolicyEffect.Deny,
            [DataCategory.Credentials] = EgressPolicyEffect.Deny,
            [DataCategory.Environment] = EgressPolicyEffect.Deny,
            [DataCategory.PrivateConfiguration] = EgressPolicyEffect.Deny,
            [DataCategory.Location] = EgressPolicyEffect.AllowWhenRequired,
            [DataCategory.SearchQuery] = EgressPolicyEffect.AllowWhenSanitized,
            [DataCategory.PublicData] = EgressPolicyEffect.Allow,
        };

    public EgressDecision Evaluate(EgressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fields.Count == 0)
        {
            return new EgressDecision(false, EgressDecisionCode.EmptyPayload, []);
        }

        var unknownFields = request.Fields
            .Where(field => field.Categories.Any(category => !Effects.ContainsKey(category)))
            .Select(field => field.Name)
            .ToArray();
        if (unknownFields.Length > 0)
        {
            return new EgressDecision(false, EgressDecisionCode.UnknownDataCategory, unknownFields);
        }

        var deniedFields = request.Fields
            .Where(field => field.Categories.Any(category => Effects[category] == EgressPolicyEffect.Deny))
            .Select(field => field.Name)
            .ToArray();
        if (deniedFields.Length > 0)
        {
            return new EgressDecision(false, EgressDecisionCode.DataCategoryDenied, deniedFields);
        }

        var unnecessaryLocationFields = request.Fields
            .Where(field =>
                field.Categories.Contains(DataCategory.Location) &&
                !field.IsRequiredForPurpose)
            .Select(field => field.Name)
            .ToArray();
        if (unnecessaryLocationFields.Length > 0)
        {
            return new EgressDecision(false, EgressDecisionCode.LocationNotRequired, unnecessaryLocationFields);
        }

        var unsanitizedFields = request.Fields
            .Where(field =>
                field.Categories.Contains(DataCategory.SearchQuery) &&
                !field.IsSanitized)
            .Select(field => field.Name)
            .ToArray();
        if (unsanitizedFields.Length > 0)
        {
            return new EgressDecision(false, EgressDecisionCode.SanitizationRequired, unsanitizedFields);
        }

        return new EgressDecision(true, EgressDecisionCode.Allowed, []);
    }
}
