using LocalAssistant.Core.Security.Egress;

namespace LocalAssistant.Tests.Security.Egress;

public sealed class DefaultEgressPolicyTests
{
    private readonly DefaultEgressPolicy _sut = new();

    [Fact]
    public void AllowsPublicData()
    {
        var result = _sut.Evaluate(Request(Field("query", [DataCategory.PublicData])));

        Assert.True(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.Allowed, result.Code);
    }

    [Fact]
    public void DeniesUnknownCategoriesByDefault()
    {
        var result = _sut.Evaluate(Request(Field("value", [new DataCategory("FUTURE_CATEGORY")])));

        Assert.False(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.UnknownDataCategory, result.Code);
        Assert.Equal(["value"], result.FieldNames);
    }

    [Theory]
    [InlineData("SOURCE_CODE")]
    [InlineData("LOCAL_DOCUMENTS")]
    [InlineData("SECRETS")]
    public void DeniesProtectedDataCategories(string categoryName)
    {
        var category = new DataCategory(categoryName);
        var result = _sut.Evaluate(Request(Field("protected", [category])));

        Assert.False(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.DataCategoryDenied, result.Code);
    }

    [Fact]
    public void DeniesMixedPayloadWhenOneFieldIsProtected()
    {
        var result = _sut.Evaluate(Request(
            Field("publicQuery", [DataCategory.PublicData]),
            Field("documentExcerpt", [DataCategory.LocalDocuments])));

        Assert.False(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.DataCategoryDenied, result.Code);
        Assert.Equal(["documentExcerpt"], result.FieldNames);
    }

    [Fact]
    public void AllowsLocationOnlyWhenItIsRequired()
    {
        var result = _sut.Evaluate(Request(Field(
            "origin",
            [DataCategory.Location],
            isRequiredForPurpose: true)));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void DeniesLocationWhenItIsNotRequired()
    {
        var result = _sut.Evaluate(Request(Field("home", [DataCategory.Location])));

        Assert.False(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.LocationNotRequired, result.Code);
    }

    [Fact]
    public void RequiresSearchQueryToBeSanitized()
    {
        var result = _sut.Evaluate(Request(Field("query", [DataCategory.SearchQuery])));

        Assert.False(result.IsAllowed);
        Assert.Equal(EgressDecisionCode.SanitizationRequired, result.Code);
    }

    [Fact]
    public void AllowsSanitizedSearchQuery()
    {
        var result = _sut.Evaluate(Request(Field(
            "query",
            [DataCategory.SearchQuery],
            isSanitized: true)));

        Assert.True(result.IsAllowed);
    }

    private static EgressRequest Request(params EgressPayloadField[] fields) =>
        new("test-provider", "test-purpose", fields);

    private static EgressPayloadField Field(
        string name,
        IReadOnlyList<DataCategory> categories,
        bool isRequiredForPurpose = false,
        bool isSanitized = false) =>
        new(name, categories, isRequiredForPurpose, isSanitized);
}
