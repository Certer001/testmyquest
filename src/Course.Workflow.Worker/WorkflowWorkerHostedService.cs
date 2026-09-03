using Course.Shared;
using Npgsql;

namespace Course.Workflow.Worker;

public sealed class WorkflowWorkerHostedService(
    WorkflowConnectionFactory connectionFactory,
    JobProcessor jobProcessor,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = IsTestProfile()
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobs = await ClaimJobsAsync(stoppingToken);
                if (jobs.Count == 0)
                {
                    await Task.Delay(pollInterval, stoppingToken);
                    continue;
                }

                foreach (var job in jobs)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await jobProcessor.ProcessAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (DependencyErrors.IsUnavailable(ex))
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"worker loop error: {ex.Message}");
                await Task.Delay(pollInterval, stoppingToken);
            }
        }
    }

    private bool IsTestProfile() =>
        string.Equals(configuration["COURSE_TEST_PROFILE"], "1", StringComparison.Ordinal);

    private async Task<IReadOnlyList<ClaimedJob>> ClaimJobsAsync(CancellationToken cancellationToken)
    {
        var owner = configuration["WORKFLOW_LEASE_OWNER"]
            ?? throw new InvalidOperationException("WORKFLOW_LEASE_OWNER is required");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT claim::text
            FROM workflow.claim_jobs(@owner, @limit) AS claim
            """,
            connection);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("limit", 1);

        var jobs = new List<ClaimedJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(0);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            jobs.Add(ClaimedJob.Parse(document.RootElement));
        }

        return jobs;
    }
}
