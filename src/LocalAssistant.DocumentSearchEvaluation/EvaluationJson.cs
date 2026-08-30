using System.Text.Json;

namespace LocalAssistant.DocumentSearchEvaluation;

internal static class EvaluationJson
{
    public static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
}
