using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Course.Shared;

public sealed class MigrationService(string connectionString)
{
    public async Task ApplyAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"Migration directory not found: {directory}");
        }

        var files = Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var historyAvailable = await IsMigrationHistoryAvailableAsync(connection, cancellationToken);

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

            if (historyAvailable)
            {
                await using var check = new NpgsqlCommand(
                    "SELECT checksum FROM course.migration_history WHERE filename = @filename",
                    connection);
                check.Parameters.AddWithValue("filename", filename);
                var existing = await check.ExecuteScalarAsync(cancellationToken) as string;

                if (existing is not null)
                {
                    if (!string.Equals(existing, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new MigrationConflictException("migration checksum conflict");
                    }

                    continue;
                }
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var execute = new NpgsqlCommand(sql, connection, transaction))
                {
                    await execute.ExecuteNonQueryAsync(cancellationToken);
                }

                if (historyAvailable || await TableExistsAsync(connection, transaction, "course", "migration_history", cancellationToken))
                {
                    historyAvailable = true;
                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO course.migration_history (filename, checksum)
                        VALUES (@filename, @checksum)
                        """,
                        connection,
                        transaction);
                    insert.Parameters.AddWithValue("filename", filename);
                    insert.Parameters.AddWithValue("checksum", checksum);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }

    private static async Task<bool> IsMigrationHistoryAvailableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT 1 FROM course.migration_history LIMIT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "3F000")
        {
            return false;
        }
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}

public sealed class MigrationConflictException(string message) : Exception(message);
