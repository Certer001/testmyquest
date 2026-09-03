using System.Text.Json;
using System.Text.Json.Nodes;

namespace Course.Workflow.Worker;

public static class PayloadBuilder
{
    public static JsonElement Build(
        JsonElement processData,
        IReadOnlyDictionary<string, string> inputMapping,
        IReadOnlyDictionary<string, JsonElement> inputConstants)
    {
        var root = new JsonObject();

        foreach (var (targetPointer, value) in inputConstants)
        {
            SetPointer(root, targetPointer, JsonNode.Parse(value.GetRawText()));
        }

        foreach (var (targetPointer, sourcePointer) in inputMapping)
        {
            var value = GetPointer(processData, sourcePointer);
            if (value is not null)
            {
                SetPointer(root, targetPointer, JsonNode.Parse(value.Value.GetRawText()));
            }
        }

        return JsonDocument.Parse(root.ToJsonString()).RootElement;
    }

    private static JsonElement? GetPointer(JsonElement document, string pointer)
    {
        if (!pointer.StartsWith('/'))
        {
            return null;
        }

        var segments = pointer[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonElement current = document;
        foreach (var segment in segments)
        {
            var decoded = segment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(decoded, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static void SetPointer(JsonObject root, string pointer, JsonNode? value)
    {
        if (value is null || !pointer.StartsWith('/'))
        {
            return;
        }

        var segments = pointer[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        JsonObject current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var decoded = segments[index].Replace("~1", "/").Replace("~0", "~");
            if (current[decoded] is not JsonObject child)
            {
                child = new JsonObject();
                current[decoded] = child;
            }

            current = child;
        }

        var leaf = segments[^1].Replace("~1", "/").Replace("~0", "~");
        current[leaf] = value;
    }
}
