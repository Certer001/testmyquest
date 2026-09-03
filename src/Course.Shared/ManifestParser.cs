using System.Text.Json;
using System.Text.Json.Serialization;

namespace Course.Shared;

public static class JsonDefaults
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static JsonSerializerOptions ManifestOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class ManifestDocument
{
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = string.Empty;

    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("http_method")]
    public string HttpMethod { get; set; } = string.Empty;

    [JsonPropertyName("target_schema")]
    public string TargetSchema { get; set; } = string.Empty;

    [JsonPropertyName("target_function")]
    public string TargetFunction { get; set; } = string.Empty;

    [JsonPropertyName("request_schema")]
    public JsonElement RequestSchema { get; set; }

    [JsonPropertyName("response_schema")]
    public JsonElement ResponseSchema { get; set; }

    [JsonPropertyName("outcomes")]
    public List<string> Outcomes { get; set; } = [];

    [JsonPropertyName("required_policy")]
    public List<string> RequiredPolicy { get; set; } = [];

    [JsonPropertyName("idempotency_mode")]
    public string IdempotencyMode { get; set; } = string.Empty;

    [JsonPropertyName("idempotency_scope")]
    public string IdempotencyScope { get; set; } = string.Empty;

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }
}

public static class ManifestParser
{
    public static ActionManifest Parse(string json)
    {
        var document = JsonSerializer.Deserialize<ManifestDocument>(json, JsonDefaults.ManifestOptions)
            ?? throw new InvalidOperationException("manifest is not a JSON object");

        if (!string.Equals(document.ContractVersion, ContractConstants.ContractVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unsupported contract version");
        }

        if (!string.Equals(document.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("only POST actions are supported");
        }

        return new ActionManifest
        {
            ContractVersion = document.ContractVersion,
            Module = document.Module,
            Action = document.Action,
            Version = document.Version,
            HttpMethod = document.HttpMethod.ToUpperInvariant(),
            TargetSchema = document.TargetSchema,
            TargetFunction = document.TargetFunction,
            RequestSchemaRaw = document.RequestSchema.GetRawText(),
            ResponseSchemaRaw = document.ResponseSchema.GetRawText(),
            Outcomes = document.Outcomes,
            RequiredPolicy = document.RequiredPolicy,
            IdempotencyMode = document.IdempotencyMode,
            IdempotencyScope = document.IdempotencyScope,
            TimeoutMs = document.TimeoutMs,
            Enabled = document.Enabled,
            IsDefault = document.IsDefault
        };
    }

    public static string ComputeChecksum(string json) =>
        PayloadHash.Compute(json.Trim());
}
