using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Course.Shared;

public sealed class ActionPublicationService(string connectionString)
{
    public async Task ValidateOnlyAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        _ = await LoadManifestAsync(manifestPath, cancellationToken);
    }

    public async Task PublishAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var (manifest, checksum, raw) = await LoadManifestAsync(manifestPath, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = new NpgsqlCommand(
            """
            SELECT manifest_checksum
            FROM course.action_versions
            WHERE module = @module AND action = @action AND version = @version
            """,
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("module", manifest.Module);
            existing.Parameters.AddWithValue("action", manifest.Action);
            existing.Parameters.AddWithValue("version", manifest.Version);
            var current = await existing.ExecuteScalarAsync(cancellationToken) as string;
            if (current is not null)
            {
                if (!string.Equals(current, checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PublicationConflictException("published action version is immutable");
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using (var insertVersion = new NpgsqlCommand(
            """
            INSERT INTO course.action_versions (
              module, action, version, http_method, target_schema, target_function,
              request_schema, response_schema, outcomes, required_policy,
              idempotency_mode, idempotency_scope, timeout_ms, manifest_checksum
            ) VALUES (
              @module, @action, @version, @http_method, @target_schema, @target_function,
              @request_schema::jsonb, @response_schema::jsonb, @outcomes::jsonb, @required_policy::jsonb,
              @idempotency_mode, @idempotency_scope, @timeout_ms, @manifest_checksum
            )
            """,
            connection,
            transaction))
        {
            insertVersion.Parameters.AddWithValue("module", manifest.Module);
            insertVersion.Parameters.AddWithValue("action", manifest.Action);
            insertVersion.Parameters.AddWithValue("version", manifest.Version);
            insertVersion.Parameters.AddWithValue("http_method", manifest.HttpMethod);
            insertVersion.Parameters.AddWithValue("target_schema", manifest.TargetSchema);
            insertVersion.Parameters.AddWithValue("target_function", manifest.TargetFunction);
            insertVersion.Parameters.AddWithValue("request_schema", manifest.RequestSchemaRaw);
            insertVersion.Parameters.AddWithValue("response_schema", manifest.ResponseSchemaRaw);
            insertVersion.Parameters.AddWithValue("outcomes", JsonSerializer.Serialize(manifest.Outcomes));
            insertVersion.Parameters.AddWithValue("required_policy", JsonSerializer.Serialize(manifest.RequiredPolicy));
            insertVersion.Parameters.AddWithValue("idempotency_mode", manifest.IdempotencyMode);
            insertVersion.Parameters.AddWithValue("idempotency_scope", manifest.IdempotencyScope);
            insertVersion.Parameters.AddWithValue("timeout_ms", manifest.TimeoutMs);
            insertVersion.Parameters.AddWithValue("manifest_checksum", checksum);
            await insertVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertState = new NpgsqlCommand(
            """
            INSERT INTO course.action_state (module, action, version, enabled, is_default)
            VALUES (@module, @action, @version, @enabled, @is_default)
            """,
            connection,
            transaction))
        {
            insertState.Parameters.AddWithValue("module", manifest.Module);
            insertState.Parameters.AddWithValue("action", manifest.Action);
            insertState.Parameters.AddWithValue("version", manifest.Version);
            insertState.Parameters.AddWithValue("enabled", manifest.Enabled);
            insertState.Parameters.AddWithValue("is_default", manifest.IsDefault);
            await insertState.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CliListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT av.module, av.action, av.version, st.enabled, st.is_default
            FROM course.action_versions av
            JOIN course.action_state st
              ON st.module = av.module AND st.action = av.action AND st.version = av.version
            ORDER BY av.module, av.action, av.version
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CliListItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CliListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return items;
    }

    public async Task ActivateAsync(string routeKey, int version, CancellationToken cancellationToken = default)
    {
        var (module, action) = ParseRouteKey(routeKey);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureVersionExistsAsync(connection, transaction, module, action, version, cancellationToken);

        await using (var disableDefaults = new NpgsqlCommand(
            """
            UPDATE course.action_state
            SET is_default = false
            WHERE module = @module AND action = @action
            """,
            connection,
            transaction))
        {
            disableDefaults.Parameters.AddWithValue("module", module);
            disableDefaults.Parameters.AddWithValue("action", action);
            await disableDefaults.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var activate = new NpgsqlCommand(
            """
            UPDATE course.action_state
            SET enabled = true, is_default = true
            WHERE module = @module AND action = @action AND version = @version
            """,
            connection,
            transaction))
        {
            activate.Parameters.AddWithValue("module", module);
            activate.Parameters.AddWithValue("action", action);
            activate.Parameters.AddWithValue("version", version);
            var updated = await activate.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                throw new InvalidOperationException("action version was not found");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DisableAsync(
        string routeKey,
        int version,
        int? replacementVersion,
        CancellationToken cancellationToken = default)
    {
        var (module, action) = ParseRouteKey(routeKey);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var isDefault = await IsDefaultVersionAsync(connection, transaction, module, action, version, cancellationToken);
        if (isDefault && replacementVersion is null)
        {
            throw new InvalidOperationException("replacement version is required");
        }

        if (replacementVersion is not null)
        {
            await EnsureVersionExistsAsync(connection, transaction, module, action, replacementVersion.Value, cancellationToken);
        }

        await using (var disable = new NpgsqlCommand(
            """
            UPDATE course.action_state
            SET enabled = false, is_default = false
            WHERE module = @module AND action = @action AND version = @version
            """,
            connection,
            transaction))
        {
            disable.Parameters.AddWithValue("module", module);
            disable.Parameters.AddWithValue("action", action);
            disable.Parameters.AddWithValue("version", version);
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        if (replacementVersion is not null)
        {
            await using var disableOtherDefaults = new NpgsqlCommand(
                """
                UPDATE course.action_state
                SET is_default = false
                WHERE module = @module AND action = @action
                """,
                connection,
                transaction);
            disableOtherDefaults.Parameters.AddWithValue("module", module);
            disableOtherDefaults.Parameters.AddWithValue("action", action);
            await disableOtherDefaults.ExecuteNonQueryAsync(cancellationToken);

            await using var activateReplacement = new NpgsqlCommand(
                """
                UPDATE course.action_state
                SET enabled = true, is_default = true
                WHERE module = @module AND action = @action AND version = @version
                """,
                connection,
                transaction);
            activateReplacement.Parameters.AddWithValue("module", module);
            activateReplacement.Parameters.AddWithValue("action", action);
            activateReplacement.Parameters.AddWithValue("version", replacementVersion.Value);
            await activateReplacement.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureVersionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM course.action_versions
            WHERE module = @module AND action = @action AND version = @version
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", version);
        var exists = await command.ExecuteScalarAsync(cancellationToken);
        if (exists is null)
        {
            throw new InvalidOperationException("action version was not found");
        }
    }

    private static async Task<bool> IsDefaultVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT is_default FROM course.action_state
            WHERE module = @module AND action = @action AND version = @version
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", version);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool enabled && enabled;
    }

    private static (string Module, string Action) ParseRouteKey(string routeKey)
    {
        var dot = routeKey.IndexOf('.');
        if (dot <= 0 || dot == routeKey.Length - 1)
        {
            throw new InvalidOperationException("invalid route key");
        }

        return (routeKey[..dot], routeKey[(dot + 1)..]);
    }

    private static async Task<(ActionManifest Manifest, string Checksum, string Raw)> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var raw = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = ManifestParser.Parse(raw);
        var checksum = ManifestParser.ComputeChecksum(raw);
        return (manifest, checksum, raw);
    }
}

public sealed class PublicationConflictException(string message) : Exception(message);
