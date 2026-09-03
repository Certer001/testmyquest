using System.Text.Json;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Api;

public sealed class ActionExecutorService(
    NpgsqlDataSource dataSource,
    ActionCatalogService catalog,
    SchemaValidator schemaValidator,
    ILogger<ActionExecutorService> logger)
{
    public async Task<ActionExecutionResult> ExecuteAsync(
        string module,
        string action,
        int? requestedVersion,
        string? idempotencyKey,
        TrustedPrincipal principal,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        ActionDefinition? definition;
        try
        {
            definition = await catalog.ResolveAsync(module, action, requestedVersion, cancellationToken);
        }
        catch (Exception ex) when (DependencyErrors.IsUnavailable(ex))
        {
            logger.LogWarning(ex, "dependency unavailable resolving {Module}.{Action}", module, action);
            return ActionExecutionResult.Error(
                503, "dependency.unavailable", "dependency is unavailable", correlationId, requestedVersion);
        }

        if (definition is null || !definition.Enabled)
        {
            return ActionExecutionResult.Error(
                404, "action.not_found", "action is unknown or disabled", correlationId, requestedVersion);
        }

        if (!HasRequiredPolicy(definition, principal))
        {
            return ActionExecutionResult.Error(
                403, "access.denied", "insufficient policy", correlationId, definition.Version);
        }

        if (definition.IdempotencyMode == "required" && string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return ActionExecutionResult.Error(
                400, "idempotency.required", "idempotency key is required", correlationId, definition.Version);
        }

        if (!schemaValidator.Validate(definition.RequestSchemaRaw, payload, out _))
        {
            return ActionExecutionResult.Error(
                422, "payload.invalid", "payload does not match schema", correlationId, definition.Version);
        }

        var payloadHash = PayloadHash.Compute(payload);
        var context = BuildContext(principal, correlationId, idempotencyKey, definition.TimeoutMs);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            if (definition.IdempotencyMode != "none" && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var scopeKey = BuildScopeKey(definition, principal);
                await AcquireIdempotencyLockAsync(connection, transaction, scopeKey, idempotencyKey, cancellationToken);
                var replay = await TryReplayAsync(
                    connection,
                    transaction,
                    scopeKey,
                    idempotencyKey,
                    payloadHash,
                    correlationId,
                    definition.Version,
                    cancellationToken);
                if (replay is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return replay;
                }
            }

            var invokeJson = await InvokeAsync(connection, transaction, definition, context, payload, cancellationToken);
            var invoke = JsonDocument.Parse(invokeJson).RootElement;
            var status = invoke.GetProperty("status").GetString();

            if (string.Equals(status, "error", StringComparison.Ordinal))
            {
                var code = invoke.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString() ?? "internal.error"
                    : "internal.error";
                var message = invoke.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "action failed"
                    : "action failed";
                await transaction.RollbackAsync(cancellationToken);
                return MapInvokeError(code, message, correlationId, definition.Version);
            }

            var outcome = invoke.GetProperty("outcome").GetString()!;
            var result = invoke.GetProperty("result");
            if (!definition.Outcomes.Contains(outcome) ||
                !schemaValidator.Validate(definition.ResponseSchemaRaw, result, out _))
            {
                await transaction.RollbackAsync(cancellationToken);
                return ActionExecutionResult.Error(
                    500,
                    "action.contract_violation",
                    "action result violates manifest",
                    correlationId,
                    definition.Version);
            }

            var success = ActionExecutionResult.Success(outcome, result, correlationId, definition.Version);

            if (definition.IdempotencyMode != "none" && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                await SaveIdempotencyAsync(
                    connection,
                    transaction,
                    BuildScopeKey(definition, principal),
                    idempotencyKey,
                    payloadHash,
                    success,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return success;
        }
        catch (Exception ex) when (DependencyErrors.IsUnavailable(ex))
        {
            logger.LogWarning(ex, "dependency unavailable for {Module}.{Action}", module, action);
            return ActionExecutionResult.Error(
                503, "dependency.unavailable", "dependency is unavailable", correlationId, definition.Version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "action execution failed for {Module}.{Action}", module, action);
            return ActionExecutionResult.Error(
                500, "internal.error", "internal error", correlationId, definition.Version);
        }
    }

    private static bool HasRequiredPolicy(ActionDefinition definition, TrustedPrincipal principal) =>
        definition.RequiredPolicy.All(scope => principal.Scopes.Contains(scope));

    private static TrustedContext BuildContext(
        TrustedPrincipal principal,
        Guid correlationId,
        string? idempotencyKey,
        int timeoutMs) =>
        new()
        {
            Principal = principal.Subject,
            Consumer = principal.Consumer,
            Scopes = principal.Scopes,
            CorrelationId = correlationId,
            RequestId = idempotencyKey,
            Deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs)
        };

    private static string BuildScopeKey(ActionDefinition definition, TrustedPrincipal principal) =>
        definition.IdempotencyScope switch
        {
            "principal_action" => $"{principal.Subject}:{definition.Module}:{definition.Action}:{definition.Version}",
            "consumer_action" => $"{principal.Consumer}:{definition.Module}:{definition.Action}:{definition.Version}",
            "global_action" => $"{definition.Module}:{definition.Action}:{definition.Version}",
            _ => $"{definition.Module}:{definition.Action}:{definition.Version}"
        };

    private static async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scopeKey,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@lock_key))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", $"{scopeKey}|{idempotencyKey}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ActionExecutionResult?> TryReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scopeKey,
        string idempotencyKey,
        string payloadHash,
        Guid correlationId,
        int actionVersion,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO course.idempotency_records (scope_key, idempotency_key, payload_hash)
            VALUES (@scope_key, @idempotency_key, @payload_hash)
            ON CONFLICT (scope_key, idempotency_key) DO NOTHING
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("scope_key", scopeKey);
            insert.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insert.Parameters.AddWithValue("payload_hash", payloadHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = new NpgsqlCommand(
            """
            SELECT payload_hash, response_envelope::text
            FROM course.idempotency_records
            WHERE scope_key = @scope_key AND idempotency_key = @idempotency_key
            FOR UPDATE
            """,
            connection,
            transaction);
        select.Parameters.AddWithValue("scope_key", scopeKey);
        select.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedHash = reader.GetString(0);
        var envelope = reader.IsDBNull(1) ? null : reader.GetString(1);
        await reader.CloseAsync();

        if (!string.Equals(storedHash, payloadHash, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Error(
                409, "idempotency.conflict", "idempotency key was reused with a different payload",
                correlationId, actionVersion);
        }

        if (envelope is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(envelope);
        var root = document.RootElement;
        if (root.GetProperty("status").GetString() == "ok")
        {
            return ActionExecutionResult.Success(
                root.GetProperty("outcome").GetString()!,
                root.GetProperty("result"),
                correlationId,
                actionVersion,
                cached: true);
        }

        var code = root.GetProperty("code").GetString()!;
        return MapInvokeError(code, root.GetProperty("message").GetString()!, correlationId, actionVersion);
    }

    private static async Task SaveIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scopeKey,
        string idempotencyKey,
        string payloadHash,
        ActionExecutionResult result,
        CancellationToken cancellationToken)
    {
        var envelope = result.StatusCode == 200
            ? JsonSerializer.Serialize(new
            {
                status = "ok",
                outcome = result.Outcome,
                result = JsonSerializer.Deserialize<object>(result.ResultJson)
            })
            : JsonSerializer.Serialize(new
            {
                status = "error",
                code = result.ErrorCode,
                message = result.ErrorMessage
            });

        await using var update = new NpgsqlCommand(
            """
            UPDATE course.idempotency_records
            SET response_envelope = @response_envelope::jsonb, payload_hash = @payload_hash
            WHERE scope_key = @scope_key AND idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("response_envelope", envelope);
        update.Parameters.AddWithValue("payload_hash", payloadHash);
        update.Parameters.AddWithValue("scope_key", scopeKey);
        update.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> InvokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionDefinition definition,
        TrustedContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT api.invoke(@module, @action, @version, @context::jsonb, @payload::jsonb)::text
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("module", definition.Module);
        command.Parameters.AddWithValue("action", definition.Action);
        command.Parameters.AddWithValue("version", definition.Version);
        command.Parameters.AddWithValue("context", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(context.ToJson()));
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload.GetRawText());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? "{}";
    }

    private static ActionExecutionResult MapInvokeError(
        string? code,
        string message,
        Guid correlationId,
        int actionVersion) =>
        code switch
        {
            "access.denied" => ActionExecutionResult.Error(403, code, message, correlationId, actionVersion),
            "action.not_found" => ActionExecutionResult.Error(404, code, message, correlationId, actionVersion),
            "operation.not_found" => ActionExecutionResult.Error(404, code, message, correlationId, actionVersion),
            "payload.invalid" => ActionExecutionResult.Error(422, code, message, correlationId, actionVersion),
            _ => ActionExecutionResult.Error(500, code ?? "internal.error", message, correlationId, actionVersion)
        };
}

public sealed class ActionExecutionResult
{
    public required int StatusCode { get; init; }
    public string? Outcome { get; init; }
    public string ResultJson { get; init; } = "{}";
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required Guid CorrelationId { get; init; }
    public required int? ActionVersion { get; init; }

    public static ActionExecutionResult Success(
        string outcome,
        JsonElement result,
        Guid correlationId,
        int actionVersion,
        bool cached = false) =>
        new()
        {
            StatusCode = 200,
            Outcome = outcome,
            ResultJson = result.GetRawText(),
            CorrelationId = correlationId,
            ActionVersion = actionVersion
        };

    public static ActionExecutionResult Error(
        int statusCode,
        string code,
        string message,
        Guid correlationId,
        int? actionVersion) =>
        new()
        {
            StatusCode = statusCode,
            ErrorCode = code,
            ErrorMessage = message,
            CorrelationId = correlationId,
            ActionVersion = actionVersion
        };
}
