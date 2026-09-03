using Npgsql;

namespace Course.Workflow.Worker;

public sealed class WorkflowConnectionFactory(NpgsqlDataSource dataSource, bool testProfile)
{
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (testProfile)
        {
            await using var command = new NpgsqlCommand("SET course.test_profile = '1'", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }
}
