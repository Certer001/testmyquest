namespace Course.Shared;

using System.Text;
using System.Text.Json;

public static class ContractConstants
{
    public const string ContractVersion = "course-1";
}

public sealed record CliSuccessResult(string Resource, string Operation, string Key, int? Version = null);

public sealed record CliListItem(
    string Module,
    string Action,
    int Version,
    bool Enabled,
    bool IsDefault);

public static class CliResponse
{
    public static object Ok(object result) => new
    {
        status = "ok",
        result,
        meta = new { contractVersion = ContractConstants.ContractVersion }
    };

    public static object Error(string code, string message) => new
    {
        status = "error",
        code,
        message,
        meta = new { contractVersion = ContractConstants.ContractVersion }
    };
}

public static class HttpEnvelope
{
    public static object Ok(string outcome, object result, Guid correlationId, int actionVersion) => new
    {
        status = "ok",
        outcome,
        result,
        meta = new { correlationId = correlationId.ToString(), actionVersion }
    };

    public static object Error(
        string code,
        string message,
        Guid correlationId,
        int? actionVersion,
        bool retryable = false,
        object? details = null) => new
    {
        status = "error",
        code,
        message,
        retryable,
        details = details ?? new { },
        meta = new
        {
            correlationId = correlationId.ToString(),
            actionVersion
        }
    };
}

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "moduledev-course";
    public string Audience { get; set; } = "moduledev-api";
    public string SigningKey { get; set; } = string.Empty;
}

public sealed class TrustedContext
{
    public required string Principal { get; init; }
    public required string Consumer { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required Guid CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public required DateTimeOffset Deadline { get; init; }

    public object ToJson() => new
    {
        principal = Principal,
        consumer = Consumer,
        scopes = Scopes,
        correlationId = CorrelationId.ToString(),
        requestId = RequestId,
        deadline = Deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    };
}

public sealed class ActionManifest
{
    public required string ContractVersion { get; init; }
    public required string Module { get; init; }
    public required string Action { get; init; }
    public required int Version { get; init; }
    public required string HttpMethod { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetFunction { get; init; }
    public required string RequestSchemaRaw { get; init; }
    public required string ResponseSchemaRaw { get; init; }
    public required IReadOnlyList<string> Outcomes { get; init; }
    public required IReadOnlyList<string> RequiredPolicy { get; init; }
    public required string IdempotencyMode { get; init; }
    public required string IdempotencyScope { get; init; }
    public required int TimeoutMs { get; init; }
    public required bool Enabled { get; init; }
    public required bool IsDefault { get; init; }
}

public static class PayloadHash
{
    public static string Compute(JsonElement payload) =>
        Compute(CanonicalJson.Serialize(payload));

    public static string Compute(string canonicalJson)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class CanonicalJson
{
    public static string Serialize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteElement(writer, element);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }
}

public static class DependencyErrors
{
    private static readonly HashSet<string> ConnectionSqlStates =
    [
        "08000", "08001", "08003", "08004", "08006", "08007", "08P01",
        "57P01", "57P02", "57P03", "53300"
    ];

    public static bool IsUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or IOException or System.Net.Sockets.SocketException)
            {
                return true;
            }

            if (current is Npgsql.PostgresException postgres &&
                ConnectionSqlStates.Contains(postgres.SqlState))
            {
                return true;
            }

            if (current is Npgsql.NpgsqlException npgsql &&
                current is not Npgsql.PostgresException &&
                npgsql.IsTransient)
            {
                return true;
            }
        }

        return false;
    }
}
