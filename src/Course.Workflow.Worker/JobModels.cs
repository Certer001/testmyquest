using System.Text.Json;

namespace Course.Workflow.Worker;

public sealed class ClaimedJob
{
    public Guid JobId { get; init; }
    public Guid ProcessId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid AttemptId { get; init; }
    public long LeaseVersion { get; init; }
    public JsonElement ProcessData { get; init; }
    public ActionContract Action { get; init; } = null!;

    public static ClaimedJob Parse(JsonElement root)
    {
        return new ClaimedJob
        {
            JobId = ReadGuid(root, "jobId", "job_id"),
            ProcessId = ReadGuid(root, "processId", "process_id"),
            ExecutionId = ReadGuid(root, "executionId", "execution_id"),
            AttemptId = ReadGuid(root, "attemptId", "attempt_id"),
            LeaseVersion = ReadInt64(root, "leaseVersion", "lease_version"),
            ProcessData = CloneElement(ReadObject(root, "processData", "process_data")),
            Action = ActionContract.Parse(CloneElement(ReadObject(root, "action", "action_contract")))
        };
    }

    private static JsonElement CloneElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? element : element.Clone();

    private static JsonElement ReadObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        return default;
    }

    private static Guid ReadGuid(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        throw new JsonException($"Missing or invalid guid field: {string.Join('/', names)}");
    }

    private static long ReadInt64(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        throw new JsonException($"Missing or invalid integer field: {string.Join('/', names)}");
    }
}

public sealed class ActionContract
{
    public string Module { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public int Version { get; init; }
    public string RequestSchemaRaw { get; init; } = string.Empty;
    public string ResponseSchemaRaw { get; init; } = string.Empty;
    public IReadOnlyList<string> Outcomes { get; init; } = [];
    public IReadOnlyList<string> RequiredPolicy { get; init; } = [];
    public IReadOnlyDictionary<string, string> InputMapping { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, JsonElement> InputConstants { get; init; } =
        new Dictionary<string, JsonElement>();
    public int TimeoutMs { get; init; }

    public static ActionContract Parse(JsonElement root)
    {
        return new ActionContract
        {
            Module = ReadString(root, "module"),
            Action = ReadString(root, "action"),
            Version = ReadInt(root, "version", "action_version"),
            RequestSchemaRaw = ReadSchema(root, "requestSchema", "request_schema"),
            ResponseSchemaRaw = ReadSchema(root, "responseSchema", "response_schema"),
            Outcomes = ReadStringList(root, "outcomes"),
            RequiredPolicy = ReadStringList(root, "requiredPolicy", "required_policy"),
            InputMapping = ReadStringMap(root, "inputMapping", "input_mapping"),
            InputConstants = ReadJsonMap(root, "inputConstants", "input_constants"),
            TimeoutMs = ReadInt(root, "timeoutMs", "timeout_ms")
        };
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string ReadSchema(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "{}",
                JsonValueKind.Object => value.GetRawText(),
                _ => "{}"
            };
        }

        return "{}";
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToList();
            }
        }

        return [];
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        map[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }

                return map;
            }
        }

        return new Dictionary<string, string>();
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonMap(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value.EnumerateObject()
                    .ToDictionary(
                        property => property.Name,
                        property => property.Value.Clone(),
                        StringComparer.Ordinal);
            }
        }

        return new Dictionary<string, JsonElement>();
    }
}

public sealed record InvokeOutcome(
    bool Success,
    string? Outcome,
    JsonElement Result,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable);

public sealed record WorkflowFunctionResult(bool Ok, string? ErrorCode, string? ErrorMessage)
{
    public static WorkflowFunctionResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WorkflowFunctionResult(true, null, null);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : "ok";
        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            return new WorkflowFunctionResult(
                false,
                root.TryGetProperty("code", out var code) ? code.GetString() : "internal.error",
                root.TryGetProperty("message", out var message) ? message.GetString() : "workflow function failed");
        }

        return new WorkflowFunctionResult(true, null, null);
    }
}
