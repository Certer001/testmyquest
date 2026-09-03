using System.Text.Json;
using Npgsql;

namespace Course.Shared;

public sealed class FlowPublicationService(string connectionString)
{
    public async Task ValidateOnlyAsync(string mapPath, CancellationToken cancellationToken = default)
    {
        var map = await FlowMapParser.ParseFileAsync(mapPath, cancellationToken);
        var validator = new FlowMapValidator(connectionString);
        await validator.ValidateAsync(map, cancellationToken);
    }

    public async Task PublishAsync(string mapPath, CancellationToken cancellationToken = default)
    {
        var map = await FlowMapParser.ParseFileAsync(mapPath, cancellationToken);
        var validator = new FlowMapValidator(connectionString);
        await validator.ValidateAsync(map, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = new NpgsqlCommand(
            """
            SELECT map_checksum
            FROM workflow.flow_versions
            WHERE flow_name = @flow_name AND flow_version = @flow_version
            """,
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("flow_name", map.FlowName);
            existing.Parameters.AddWithValue("flow_version", map.Version);
            var current = await existing.ExecuteScalarAsync(cancellationToken) as string;
            if (current is not null)
            {
                if (!string.Equals(current, map.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowConflictException("published flow version is immutable");
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO workflow.flow_versions (
              flow_name, flow_version, status, is_active, start_step, map_definition, map_checksum
            ) VALUES (
              @flow_name, @flow_version, 'PUBLISHED', false, @start_step, @map_definition::jsonb, @map_checksum
            )
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("flow_name", map.FlowName);
            insert.Parameters.AddWithValue("flow_version", map.Version);
            insert.Parameters.AddWithValue("start_step", map.StartStep);
            insert.Parameters.AddWithValue("map_definition", map.RawJson);
            insert.Parameters.AddWithValue("map_checksum", map.Checksum);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FlowListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT flow_name, flow_version, is_active, status
            FROM workflow.flow_versions
            ORDER BY flow_name, flow_version
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<FlowListItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new FlowListItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetBoolean(2),
                reader.GetString(3)));
        }

        return items;
    }

    public async Task ActivateAsync(string flowName, int version, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exists = new NpgsqlCommand(
            """
            SELECT 1 FROM workflow.flow_versions
            WHERE flow_name = @flow_name AND flow_version = @flow_version
            """,
            connection,
            transaction))
        {
            exists.Parameters.AddWithValue("flow_name", flowName);
            exists.Parameters.AddWithValue("flow_version", version);
            var found = await exists.ExecuteScalarAsync(cancellationToken);
            if (found is null)
            {
                throw new FlowNotFoundException("flow version was not found");
            }
        }

        await using (var deactivate = new NpgsqlCommand(
            """
            UPDATE workflow.flow_versions
            SET is_active = false
            WHERE flow_name = @flow_name
            """,
            connection,
            transaction))
        {
            deactivate.Parameters.AddWithValue("flow_name", flowName);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var activate = new NpgsqlCommand(
            """
            UPDATE workflow.flow_versions
            SET is_active = true
            WHERE flow_name = @flow_name AND flow_version = @flow_version
            """,
            connection,
            transaction))
        {
            activate.Parameters.AddWithValue("flow_name", flowName);
            activate.Parameters.AddWithValue("flow_version", version);
            var updated = await activate.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                throw new FlowNotFoundException("flow version was not found");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
