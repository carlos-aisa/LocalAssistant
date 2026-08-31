using LocalAssistant.Core.Memory;

namespace LocalAssistant.Tests.Memory;

public sealed class PersonalMemoryContractsTests
{
    [Fact]
    public void PersonalMemoryDeclaresAnOwnerBoundPartition()
    {
        var memory = new PersonalMemory(
            Guid.NewGuid(),
            "owner-a",
            "Private preference.",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(MemoryScopeKind.Personal, memory.Partition.Scope);
        Assert.Equal("owner-a", memory.Partition.OwnerPrincipalId);
    }

    [Fact]
    public void ScopeFactoriesKeepHouseholdModuleAndEphemeralPartitionsSeparate()
    {
        var shared = MemoryPartition.HouseholdShared("household-a");
        var module = MemoryPartition.Module("household-a", "meal-planning");
        var ephemeral = MemoryPartition.Ephemeral();

        Assert.Equal(MemoryScopeKind.HouseholdShared, shared.Scope);
        Assert.Equal(MemoryScopeKind.Module, module.Scope);
        Assert.Equal("meal-planning", module.ModuleId);
        Assert.Equal(MemoryScopeKind.Ephemeral, ephemeral.Scope);
    }

    [Fact]
    public void DraftTrimsValidText()
    {
        var draft = new PersonalMemoryDraft("  Prefer concise answers.  ");

        Assert.Equal("Prefer concise answers.", draft.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DraftRejectsEmptyText(string? text)
    {
        var exception = Assert.Throws<ArgumentException>(() => new PersonalMemoryDraft(text));

        Assert.Equal("text", exception.ParamName);
    }

    [Fact]
    public void DraftRejectsTextOverTheMaximumLength()
    {
        var text = new string('a', PersonalMemoryDraft.MaximumTextLength + 1);

        var exception = Assert.Throws<ArgumentException>(() => new PersonalMemoryDraft(text));

        Assert.Equal("text", exception.ParamName);
    }

    [Fact]
    public void ListQueryUsesTheDefaultLimit()
    {
        var query = new PersonalMemoryListQuery();

        Assert.Equal(PersonalMemoryListQuery.DefaultLimit, query.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PersonalMemoryListQuery.MaximumLimit + 1)]
    public void ListQueryRejectsAnOutOfRangeLimit(int limit)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PersonalMemoryListQuery(limit));

        Assert.Equal("limit", exception.ParamName);
    }
}
