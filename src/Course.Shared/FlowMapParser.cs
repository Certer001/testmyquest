using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Course.Shared;

public sealed class FlowMap
{
    public required string ContractVersion { get; init; }
    public required string FlowName { get; init; }
    public required int Version { get; init; }
    public required string StartStep { get; init; }
    public required JsonElement Document { get; init; }
    public required string RawJson { get; init; }
    public required string Checksum { get; init; }
}

public sealed class FlowMapDocument
{
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = string.Empty;

    [JsonPropertyName("flow_name")]
    public string FlowName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("start_step")]
    public string StartStep { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public JsonElement Steps { get; set; }

    [JsonPropertyName("transitions")]
    public JsonElement Transitions { get; set; }
}

public static class FlowMapParser
{
    private static readonly Lazy<string> EmbeddedSchema = new(LoadEmbeddedSchema);

    public static string SchemaRaw => EmbeddedSchema.Value;

    public static async Task<FlowMap> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = path == "/dev/stdin"
            ? await Console.In.ReadToEndAsync(cancellationToken)
            : await File.ReadAllTextAsync(path, cancellationToken);
        var isYaml = path.EndsWith(".flow.yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".flow.yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
        return Parse(content, isYaml);
    }

    public static FlowMap Parse(string content, bool isYaml = false)
    {
        var json = isYaml ? ConvertYamlToJson(content) : content.Trim();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();

        var parsed = JsonSerializer.Deserialize<FlowMapDocument>(json, JsonDefaults.ManifestOptions)
            ?? throw new InvalidOperationException("flow map is not a JSON object");

        if (!string.Equals(parsed.ContractVersion, ContractConstants.ContractVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unsupported contract version");
        }

        var canonical = CanonicalJson.Serialize(root);
        var checksum = PayloadHash.Compute(canonical);

        return new FlowMap
        {
            ContractVersion = parsed.ContractVersion,
            FlowName = parsed.FlowName,
            Version = parsed.Version,
            StartStep = parsed.StartStep,
            Document = root,
            RawJson = canonical,
            Checksum = checksum
        };
    }

    public static string ComputeChecksum(string jsonOrYaml, bool isYaml = false)
    {
        var json = isYaml ? ConvertYamlToJson(jsonOrYaml) : jsonOrYaml.Trim();
        using var document = JsonDocument.Parse(json);
        return PayloadHash.Compute(CanonicalJson.Serialize(document.RootElement));
    }

    private static string ConvertYamlToJson(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();
        var yamlObject = deserializer.Deserialize<object>(yaml)
            ?? throw new InvalidOperationException("flow map yaml is empty");
        var normalized = NormalizeYamlObject(yamlObject);
        return JsonSerializer.Serialize(normalized, JsonDefaults.ManifestOptions);
    }

    private static object? NormalizeYamlObject(object? value) =>
        value switch
        {
            IDictionary<object, object> map => map.ToDictionary(
                pair => pair.Key.ToString() ?? string.Empty,
                pair => NormalizeYamlObject(pair.Value),
                StringComparer.Ordinal),
            IDictionary<string, object> map => map.ToDictionary(
                pair => pair.Key,
                pair => NormalizeYamlObject(pair.Value),
                StringComparer.Ordinal),
            IList<object> list => list.Select(NormalizeYamlObject).ToList(),
            string text when TryParseInteger(text, out var integer) => integer,
            string or bool or int or long or double or decimal => value,
            null => null,
            _ => value.ToString() ?? string.Empty
        };

    private static bool TryParseInteger(string text, out long integer) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer);

    private static string LoadEmbeddedSchema()
    {
        var assembly = typeof(FlowMapParser).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("workflow-map.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("embedded workflow map schema was not found");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("embedded workflow map schema was not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class FlowConflictException(string message) : Exception(message);
public sealed class FlowNotFoundException(string message) : Exception(message);
public sealed class WorkflowLeaseStaleException(string message) : Exception(message);
public sealed class WorkflowMappingMissingException(string message) : Exception(message);

public sealed record FlowListItem(
    string FlowName,
    int FlowVersion,
    bool IsActive,
    string Status);
