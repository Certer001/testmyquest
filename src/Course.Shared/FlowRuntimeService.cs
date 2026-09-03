using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Course.Shared;

public sealed class FlowRuntimeService(string connectionString)
{
    public async Task<FlowProcessSnapshot> StartAsync(
        string flowName,
        string businessKey,
        JsonElement processData,
        CancellationToken cancellationToken = default)
    {
        var dataHash = PayloadHash.Compute(processData);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var active = await LoadActiveFlowAsync(connection, transaction, flowName, cancellationToken);
        if (active is null)
        {
            throw new FlowNotFoundException("active flow version was not found");
        }

        var existing = await LoadExistingProcessAsync(
            connection,
            transaction,
            flowName,
            businessKey,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.DataHash, dataHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowConflictException("business key is already bound to different process data");
            }

            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var processId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO workflow.process_instances (
              process_id, business_key, flow_name, flow_version, state, process_data, data_hash
            ) VALUES (
              @process_id, @business_key, @flow_name, @flow_version, 'CREATED', @process_data::jsonb, @data_hash
            )
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("process_id", processId);
            insert.Parameters.AddWithValue("business_key", businessKey);
            insert.Parameters.AddWithValue("flow_name", flowName);
            insert.Parameters.AddWithValue("flow_version", active.FlowVersion);
            insert.Parameters.AddWithValue("process_data", NpgsqlDbType.Jsonb, processData.GetRawText());
            insert.Parameters.AddWithValue("data_hash", dataHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnterStepAsync(connection, transaction, processId, active.StartStep, cancellationToken);
        var snapshot = await LoadProcessAsync(connection, transaction, processId, cancellationToken)
            ?? throw new InvalidOperationException("process was not found after start");

        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<FlowProcessSnapshot> GetAsync(Guid processId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadProcessAsync(connection, null, processId, cancellationToken)
            ?? throw new FlowNotFoundException("process was not found");
    }

    public async Task<FlowSignalResult> SignalAsync(
        Guid processId,
        string signalType,
        string messageId,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var bodyHash = PayloadHash.Compute(payload);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var process = await LoadProcessAsync(connection, transaction, processId, cancellationToken);
        if (process is null)
        {
            throw new FlowNotFoundException("process was not found");
        }

        var existingSignal = await LoadSignalAsync(connection, transaction, messageId, cancellationToken);
        if (existingSignal is not null)
        {
            if (existingSignal.ProcessId != processId ||
                !string.Equals(existingSignal.BodyHash, bodyHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowConflictException("signal message id is already used");
            }

            await transaction.CommitAsync(cancellationToken);
            return new FlowSignalResult("duplicate");
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO workflow.signals (
              message_id, process_id, signal_type, body, body_hash, status
            ) VALUES (
              @message_id, @process_id, @signal_type, @body::jsonb, @body_hash, 'ACCEPTED'
            )
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("message_id", messageId);
            insert.Parameters.AddWithValue("process_id", processId);
            insert.Parameters.AddWithValue("signal_type", signalType);
            insert.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, payload.GetRawText());
            insert.Parameters.AddWithValue("body_hash", bodyHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (string.Equals(process.State, "WAITING_SIGNAL", StringComparison.Ordinal))
        {
            var waitingStep = await LoadWaitingSignalStepAsync(
                connection,
                transaction,
                processId,
                signalType,
                cancellationToken);
            if (waitingStep is null)
            {
                throw new WorkflowMappingMissingException("process is not waiting for this signal type");
            }

            await ApplyWaitingSignalAsync(
                connection,
                transaction,
                processId,
                waitingStep,
                messageId,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FlowSignalResult("accepted");
    }

    public async Task TestFinishAsync(
        Guid jobId,
        string owner,
        long leaseVersion,
        string outcome,
        JsonElement result,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("test-finish is only available in test profile");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("lease_version", leaseVersion);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, result.GetRawText());

        var json = await command.ExecuteScalarAsync(cancellationToken) as string ?? "{}";
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (string.Equals(root.GetProperty("status").GetString(), "error", StringComparison.Ordinal))
        {
            var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "internal.error";
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : "workflow function failed";
            if (string.Equals(code, "workflow.lease_stale", StringComparison.Ordinal))
            {
                throw new WorkflowLeaseStaleException(message ?? "job lease is stale");
            }

            throw new InvalidOperationException(message);
        }
    }

    private static async Task EnterStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT workflow.enter_step(@process_id, @step_key)",
            connection,
            transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("step_key", stepKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ActiveFlowVersion?> LoadActiveFlowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string flowName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT flow_version, start_step
            FROM workflow.flow_versions
            WHERE flow_name = @flow_name AND is_active = true
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("flow_name", flowName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ActiveFlowVersion(reader.GetInt32(0), reader.GetString(1));
    }

    private static async Task<FlowProcessSnapshot?> LoadExistingProcessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string flowName,
        string businessKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT process_id, flow_name, flow_version, state, current_step_key, data_hash
            FROM workflow.process_instances
            WHERE flow_name = @flow_name AND business_key = @business_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("flow_name", flowName);
        command.Parameters.AddWithValue("business_key", businessKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FlowProcessSnapshot(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    private static async Task<FlowProcessSnapshot?> LoadProcessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid processId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT process_id, flow_name, flow_version, state, current_step_key, data_hash
            FROM workflow.process_instances
            WHERE process_id = @process_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("process_id", processId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FlowProcessSnapshot(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    private static async Task<ExistingSignal?> LoadSignalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT process_id, body_hash
            FROM workflow.signals
            WHERE message_id = @message_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingSignal(reader.GetGuid(0), reader.GetString(1));
    }

    private static async Task<WaitingSignalStep?> LoadWaitingSignalStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string signalType,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT step_instance_id, step_key, wait_outcome
            FROM workflow.step_instances
            WHERE process_id = @process_id
              AND step_type = 'WAIT_SIGNAL'
              AND state = 'WAITING'
              AND signal_type = @signal_type
            ORDER BY entered_at DESC
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("signal_type", signalType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WaitingSignalStep(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    private static async Task ApplyWaitingSignalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        WaitingSignalStep waitingStep,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using (var apply = new NpgsqlCommand(
            """
            UPDATE workflow.signals
            SET status = 'APPLIED'
            WHERE message_id = @message_id
            """,
            connection,
            transaction))
        {
            apply.Parameters.AddWithValue("message_id", messageId);
            await apply.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var complete = new NpgsqlCommand(
            """
            UPDATE workflow.step_instances
            SET state = 'COMPLETED',
                outcome = @outcome,
                completed_at = now()
            WHERE step_instance_id = @step_instance_id
            """,
            connection,
            transaction))
        {
            complete.Parameters.AddWithValue("outcome", waitingStep.WaitOutcome);
            complete.Parameters.AddWithValue("step_instance_id", waitingStep.StepInstanceId);
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var append = new NpgsqlCommand(
            """
            SELECT workflow.append_event(
                @process_id,
                @step_instance_id,
                'SignalApplied',
                jsonb_build_object('messageId', @message_id)
            )
            """,
            connection,
            transaction))
        {
            append.Parameters.AddWithValue("process_id", processId);
            append.Parameters.AddWithValue("step_instance_id", waitingStep.StepInstanceId);
            append.Parameters.AddWithValue("message_id", messageId);
            await append.ExecuteNonQueryAsync(cancellationToken);
        }

        var target = await FindTransitionTargetAsync(
            connection,
            transaction,
            processId,
            waitingStep.StepKey,
            waitingStep.WaitOutcome,
            cancellationToken);
        if (target is null)
        {
            throw new InvalidOperationException("transition not found for signal outcome");
        }

        await EnterStepAsync(connection, transaction, processId, target, cancellationToken);
    }

    private static async Task<string?> FindTransitionTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string fromStep,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT workflow.find_transition_target(fv.map_definition, @from_step, @outcome)
            FROM workflow.process_instances pi
            JOIN workflow.flow_versions fv
              ON fv.flow_name = pi.flow_name AND fv.flow_version = pi.flow_version
            WHERE pi.process_id = @process_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("from_step", fromStep);
        command.Parameters.AddWithValue("outcome", outcome);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private sealed record ActiveFlowVersion(int FlowVersion, string StartStep);
    private sealed record ExistingSignal(Guid ProcessId, string BodyHash);
    private sealed record WaitingSignalStep(Guid StepInstanceId, string StepKey, string WaitOutcome);
}

public sealed record FlowProcessSnapshot(
    Guid ProcessId,
    string FlowName,
    int FlowVersion,
    string State,
    string? CurrentStepKey,
    string DataHash);

public sealed record FlowSignalResult(string Status);
