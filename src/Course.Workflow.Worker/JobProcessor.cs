using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Course.Workflow.Worker;

public sealed class JobProcessor(
    WorkflowConnectionFactory connectionFactory,
    SchemaValidator schemaValidator,
    string leaseOwner)
{
    public async Task ProcessAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessCoreAsync(job, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailJobAsync(
                job,
                "internal.error",
                retryable: true,
                ex.Message,
                cancellationToken);
        }
    }

    private async Task ProcessCoreAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        FailpointGate.MaybeReach("after_job_claim", leaseOwner);

        var payload = PayloadBuilder.Build(
            job.ProcessData,
            job.Action.InputMapping,
            job.Action.InputConstants);

        if (!schemaValidator.Validate(job.Action.RequestSchemaRaw, payload, out _))
        {
            await FailJobAsync(
                job,
                "payload.invalid",
                retryable: false,
                "payload does not match schema",
                cancellationToken);
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var invoke = await InvokeAsync(connection, transaction, job, payload, cancellationToken);
        if (!invoke.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            await FailJobAsync(
                job,
                invoke.ErrorCode ?? "internal.error",
                invoke.Retryable,
                invoke.ErrorMessage ?? "action failed",
                cancellationToken);
            return;
        }

        if (!job.Action.Outcomes.Contains(invoke.Outcome!) ||
            !schemaValidator.Validate(job.Action.ResponseSchemaRaw, invoke.Result, out _))
        {
            await transaction.RollbackAsync(cancellationToken);
            await FailJobAsync(
                job,
                "action.contract_violation",
                retryable: false,
                "action result violates manifest",
                cancellationToken);
            return;
        }

        FailpointGate.MaybeReach("after_action_before_finish", leaseOwner);

        var finishJson = await FinishJobAsync(
            connection,
            transaction,
            job,
            invoke.Outcome!,
            invoke.Result,
            cancellationToken);
        var finish = WorkflowFunctionResult.Parse(finishJson);
        if (!finish.Ok)
        {
            await transaction.RollbackAsync(cancellationToken);
            await FailJobAsync(
                job,
                finish.ErrorCode ?? "internal.error",
                retryable: string.Equals(finish.ErrorCode, "workflow.lease_stale", StringComparison.Ordinal),
                finish.ErrorMessage ?? "finish_job failed",
                cancellationToken);
            return;
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildContextJson(ClaimedJob job, Guid correlationId)
    {
        var scopes = job.Action.RequiredPolicy
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!scopes.Contains("workflow:execute", StringComparer.Ordinal))
        {
            scopes.Add("workflow:execute");
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(job.Action.TimeoutMs);
        var context = new Dictionary<string, object?>
        {
            ["principal"] = "workflow-worker",
            ["consumer"] = "internal",
            ["scopes"] = scopes,
            ["correlationId"] = correlationId.ToString(),
            ["requestId"] = job.ExecutionId.ToString(),
            ["deadline"] = deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["processId"] = job.ProcessId.ToString(),
            ["jobId"] = job.JobId.ToString(),
            ["executionId"] = job.ExecutionId.ToString(),
            ["attemptId"] = job.AttemptId.ToString()
        };

        return JsonSerializer.Serialize(context);
    }

    private static async Task<InvokeOutcome> InvokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedJob job,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            SELECT api.invoke(@module, @action, @version, @context::jsonb, @payload::jsonb)::text
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("module", job.Action.Module);
        command.Parameters.AddWithValue("action", job.Action.Action);
        command.Parameters.AddWithValue("version", job.Action.Version);
        command.Parameters.AddWithValue("context", NpgsqlDbType.Jsonb, BuildContextJson(job, correlationId));
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload.GetRawText());

        var json = await command.ExecuteScalarAsync(cancellationToken) as string ?? "{}";
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : "error";

        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            var code = root.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : "internal.error";
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : "action failed";
            var retryable = root.TryGetProperty("retryable", out var retryableElement) &&
                            retryableElement.ValueKind == JsonValueKind.True;
            return new InvokeOutcome(false, null, default, code, message, retryable);
        }

        var outcome = root.GetProperty("outcome").GetString()!;
        var result = root.GetProperty("result").Clone();
        return new InvokeOutcome(true, outcome, result, null, null, false);
    }

    private async Task<string?> FinishJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedJob job,
        string outcome,
        JsonElement result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT workflow.finish_job(
                @job_id,
                @owner,
                @lease_version,
                @outcome,
                @result::jsonb
            )::text
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("job_id", job.JobId);
        command.Parameters.AddWithValue("owner", leaseOwner);
        command.Parameters.AddWithValue("lease_version", job.LeaseVersion);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, result.GetRawText());
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task FailJobAsync(
        ClaimedJob job,
        string errorCode,
        bool retryable,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT workflow.fail_job(
                @job_id,
                @owner,
                @lease_version,
                @error_code,
                @retryable,
                @error_message
            )::text
            """,
            connection);
        command.Parameters.AddWithValue("job_id", job.JobId);
        command.Parameters.AddWithValue("owner", leaseOwner);
        command.Parameters.AddWithValue("lease_version", job.LeaseVersion);
        command.Parameters.AddWithValue("error_code", errorCode);
        command.Parameters.AddWithValue("retryable", retryable);
        command.Parameters.AddWithValue("error_message", errorMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
