using System.Text.Json;
using LocalAssistant.Core.Reminders;

namespace LocalAssistant.Core.Tools;

public sealed class CreateReminderTool : ITool
{
    public const string ToolName = "create_reminder";

    private const int MaximumTitleLength = 200;
    private const string Description =
        "Creates a temporary private in-memory reminder record for experimental state-change " +
        "testing after confirmation. The record is lost when the process restarts and does " +
        "not schedule or deliver a notification when dueAtUtc is reached.";

    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            title = new
            {
                type = "string",
                minLength = 1,
                maxLength = MaximumTitleLength,
                description = "Reminder text.",
            },
            dueAtUtc = new
            {
                type = "string",
                format = "date-time",
                description = "Future reminder time in UTC.",
            },
        },
        required = new[] { "title", "dueAtUtc" },
        additionalProperties = false,
    });

    private readonly IReminderStore _reminders;
    private readonly TimeProvider _clock;

    public CreateReminderTool(IReminderStore reminders, TimeProvider clock)
    {
        _reminders = reminders;
        _clock = clock;
    }

    public ToolDefinition Definition { get; } = new(
        new ToolMetadata(
            ToolName,
            Description,
            new ToolRiskProfile(
                ToolOperationImpact.ChangesState,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: true,
                ["reminders.write"])),
        InputSchema);

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionResult.Failure(
            "tool_execution_context_invalid",
            "The reminder operation context is missing.",
            "The reminder could not be created."));

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.PrincipalId) || context.OperationId is null)
        {
            return ToolExecutionResult.Failure(
                "tool_execution_context_invalid",
                "The reminder operation context is missing.",
                "The reminder could not be created.");
        }

        if (!TryReadArguments(arguments, out var title, out var dueAtUtc, out var error))
        {
            return ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                error,
                "The reminder arguments are invalid.");
        }

        if (dueAtUtc <= _clock.GetUtcNow())
        {
            return ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                "The argument 'dueAtUtc' must be in the future.",
                "The reminder arguments are invalid.");
        }

        var reminder = await _reminders.GetOrCreateAsync(
            context.PrincipalId,
            context.OperationId.Value,
            title,
            dueAtUtc,
            cancellationToken);
        var content = JsonSerializer.Serialize(new
        {
            id = reminder.Id,
            title = reminder.Title,
            dueAtUtc = reminder.DueAtUtc,
            createdAtUtc = reminder.CreatedAtUtc,
            storage = "in_memory",
            durable = false,
            notificationScheduled = false,
        });

        return ToolExecutionResult.Success(content);
    }

    private static bool TryReadArguments(
        JsonElement arguments,
        out string title,
        out DateTimeOffset dueAtUtc,
        out string error)
    {
        title = string.Empty;
        dueAtUtc = default;
        error = "The reminder arguments are invalid.";

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "The reminder arguments must be a JSON object.";
            return false;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("title" or "dueAtUtc"))
            {
                error = $"The argument '{property.Name}' is not supported.";
                return false;
            }
        }

        if (!arguments.TryGetProperty("title", out var titleElement) ||
            titleElement.ValueKind != JsonValueKind.String)
        {
            error = "The argument 'title' must be a string.";
            return false;
        }

        title = titleElement.GetString()?.Trim() ?? string.Empty;
        if (title.Length is 0 or > MaximumTitleLength)
        {
            error = "The argument 'title' must contain between 1 and 200 characters.";
            return false;
        }

        if (!arguments.TryGetProperty("dueAtUtc", out var dueAtUtcElement) ||
            dueAtUtcElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                dueAtUtcElement.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out dueAtUtc) ||
            dueAtUtc.Offset != TimeSpan.Zero)
        {
            error = "The argument 'dueAtUtc' must be a UTC date and time.";
            return false;
        }

        return true;
    }
}
