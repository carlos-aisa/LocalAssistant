namespace LocalAssistant.Core.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registrations = new Dictionary<string, ITool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (!registrations.TryAdd(tool.Definition.Metadata.Name, tool))
            {
                throw new ArgumentException(
                    $"A tool named '{tool.Definition.Metadata.Name}' is already registered.",
                    nameof(tools));
            }
        }

        _tools = registrations;
        Definitions = registrations.Values.Select(static tool => tool.Definition).ToArray();
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public bool TryGet(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);
}
