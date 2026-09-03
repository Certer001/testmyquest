using System.Text.Json;

namespace Course.Workflow.Worker;

public static class FailpointGate
{
    public static void MaybeReach(string name, string instanceId)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var configured = Environment.GetEnvironmentVariable("COURSE_FAILPOINT");
        if (!string.Equals(configured, name, StringComparison.Ordinal))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            @event = "failpoint.reached",
            name,
            instanceId
        });
        Console.Out.WriteLine(payload);
        Console.Out.Flush();
        Thread.Sleep(Timeout.InfiniteTimeSpan);
    }
}
