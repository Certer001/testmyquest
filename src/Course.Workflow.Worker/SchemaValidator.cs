using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Course.Workflow.Worker;

public sealed class SchemaValidator
{
    private readonly Dictionary<string, JsonSchema> _cache = new(StringComparer.Ordinal);

    public bool Validate(string schemaRaw, JsonElement payload, out string message)
    {
        message = string.Empty;
        if (!_cache.TryGetValue(schemaRaw, out var schema))
        {
            schema = JsonSchema.FromText(schemaRaw);
            _cache[schemaRaw] = schema;
        }

        var node = JsonNode.Parse(payload.GetRawText());
        var result = schema.Evaluate(node!, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Flag
        });

        if (result.IsValid)
        {
            return true;
        }

        message = "payload does not match schema";
        return false;
    }
}
