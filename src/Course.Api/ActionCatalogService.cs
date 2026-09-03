using System.Text.Json;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Api;

public sealed class ActionCatalogService(NpgsqlDataSource dataSource)
{
    public async Task<ActionDefinition?> ResolveAsync(
        string module,
        string action,
        int? requestedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            requestedVersion is null
                ? """
                  SELECT av.module, av.action, av.version, av.http_method, av.target_schema, av.target_function,
                         av.request_schema::text, av.response_schema::text, av.outcomes::text, av.required_policy::text,
                         av.idempotency_mode, av.idempotency_scope, av.timeout_ms, st.enabled, st.is_default
                  FROM course.action_versions av
                  JOIN course.action_state st
                    ON st.module = av.module AND st.action = av.action AND st.version = av.version
                  WHERE av.module = @module AND av.action = @action AND st.enabled = true AND st.is_default = true
                  """
                : """
                  SELECT av.module, av.action, av.version, av.http_method, av.target_schema, av.target_function,
                         av.request_schema::text, av.response_schema::text, av.outcomes::text, av.required_policy::text,
                         av.idempotency_mode, av.idempotency_scope, av.timeout_ms, st.enabled, st.is_default
                  FROM course.action_versions av
                  JOIN course.action_state st
                    ON st.module = av.module AND st.action = av.action AND st.version = av.version
                  WHERE av.module = @module AND av.action = @action AND av.version = @version
                  """,
            connection);

        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("action", action);
        if (requestedVersion is not null)
        {
            command.Parameters.AddWithValue("version", requestedVersion.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadDefinition(reader);
    }

    public async Task<IReadOnlyList<ActionDefinition>> ListEnabledDefaultsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT av.module, av.action, av.version, av.http_method, av.target_schema, av.target_function,
                   av.request_schema::text, av.response_schema::text, av.outcomes::text, av.required_policy::text,
                   av.idempotency_mode, av.idempotency_scope, av.timeout_ms, st.enabled, st.is_default
            FROM course.action_versions av
            JOIN course.action_state st
              ON st.module = av.module AND st.action = av.action AND st.version = av.version
            WHERE st.enabled = true AND st.is_default = true
            ORDER BY av.module, av.action
            """,
            connection);

        var items = new List<ActionDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDefinition(reader));
        }

        return items;
    }

    public async Task<ActionDefinition?> GetExactVersionAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT av.module, av.action, av.version, av.http_method, av.target_schema, av.target_function,
                   av.request_schema::text, av.response_schema::text, av.outcomes::text, av.required_policy::text,
                   av.idempotency_mode, av.idempotency_scope, av.timeout_ms, st.enabled, st.is_default
            FROM course.action_versions av
            JOIN course.action_state st
              ON st.module = av.module AND st.action = av.action AND st.version = av.version
            WHERE av.module = @module AND av.action = @action AND av.version = @version
            """,
            connection);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadDefinition(reader);
    }

    private static ActionDefinition ReadDefinition(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [],
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12),
            reader.GetBoolean(13),
            reader.GetBoolean(14));
}

public sealed record ActionDefinition(
    string Module,
    string Action,
    int Version,
    string HttpMethod,
    string TargetSchema,
    string TargetFunction,
    string RequestSchemaRaw,
    string ResponseSchemaRaw,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> RequiredPolicy,
    string IdempotencyMode,
    string IdempotencyScope,
    int TimeoutMs,
    bool Enabled,
    bool IsDefault);
