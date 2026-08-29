using LocalAssistant.Core.Memory;

namespace LocalAssistant.Api.Contracts;

public sealed record CreatePersonalMemoryRequest(string? Text);

public sealed record ListPersonalMemoriesRequest(int? Limit);

public sealed record PersonalMemoryResponse(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public static PersonalMemoryResponse FromMemory(PersonalMemory memory) => new(
        memory.Id,
        memory.Text,
        memory.CreatedAtUtc,
        memory.ModifiedAtUtc,
        memory.ExpiresAtUtc);
}

public sealed record PersonalMemoryListResponse(
    IReadOnlyList<PersonalMemoryResponse> Memories)
{
    public static PersonalMemoryListResponse FromMemories(
        IReadOnlyList<PersonalMemory> memories) => new(
        memories.Select(PersonalMemoryResponse.FromMemory).ToArray());
}
