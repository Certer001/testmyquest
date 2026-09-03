using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;

namespace Course.Shared;

public sealed class FlowMapValidator(string? connectionString = null)
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(FlowMapParser.SchemaRaw));

    public void ValidateSchema(FlowMap map)
    {
        var node = JsonNode.Parse(map.RawJson);
        var result = Schema.Value.Evaluate(node!, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Flag
        });

        if (!result.IsValid)
        {
            throw new InvalidOperationException("flow map does not match schema");
        }
    }

    public async Task ValidateSemanticAsync(FlowMap map, CancellationToken cancellationToken = default)
    {
        ValidateSchema(map);

        var steps = ParseSteps(map.Document);
        var transitions = ParseTransitions(map.Document);
        var stepKeys = steps.Select(step => step.Key).ToList();

        if (stepKeys.Count != stepKeys.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("step keys must be unique");
        }

        if (!stepKeys.Contains(map.StartStep, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("start step was not found");
        }

        var transitionKeys = transitions
            .Select(transition => (transition.From, transition.Outcome))
            .ToList();
        if (transitionKeys.Count != transitionKeys.Distinct().Count())
        {
            throw new InvalidOperationException("transition outcomes must be unique per step");
        }

        var stepByKey = steps.ToDictionary(step => step.Key, StringComparer.Ordinal);
        foreach (var transition in transitions)
        {
            if (!stepByKey.ContainsKey(transition.From))
            {
                throw new InvalidOperationException("transition references unknown step");
            }

            if (!stepByKey.ContainsKey(transition.To))
            {
                throw new InvalidOperationException("transition target was not found");
            }

            if (string.Equals(stepByKey[transition.From].Type, "end", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transitions from end steps are not allowed");
            }
        }

        if (!steps.Any(step => string.Equals(step.Type, "end", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("flow map must contain an end step");
        }

        var adjacency = transitions
            .GroupBy(transition => transition.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var reachable = ComputeReachable(map.StartStep, adjacency);
        if (reachable.Count != stepKeys.Count)
        {
            throw new InvalidOperationException("all steps must be reachable from the start step");
        }

        if (HasCycle(map.StartStep, adjacency))
        {
            throw new InvalidOperationException("flow map must be acyclic");
        }

        if (!reachable.Any(key => string.Equals(stepByKey[key].Type, "end", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("an end step must be reachable");
        }

        foreach (var step in steps)
        {
            ValidateStepOutcomes(step, adjacency);
            if (string.Equals(step.Type, "automatic", StringComparison.Ordinal))
            {
                ValidateRetry(step.Task!.RetryMaxAttempts, step.Task.RetryDelaysMs);
                ValidateMappingOverlap(step.Task.InputMapping.Keys, step.Task.InputConstants);
                if (connectionString is not null)
                {
                    await ValidateAutomaticStepAsync(step, adjacency, cancellationToken);
                }
            }
        }
    }

    public Task ValidateAsync(FlowMap map, CancellationToken cancellationToken = default) =>
        ValidateSemanticAsync(map, cancellationToken);

    private static List<FlowStepDefinition> ParseSteps(JsonElement document)
    {
        var steps = new List<FlowStepDefinition>();
        foreach (var step in document.GetProperty("steps").EnumerateArray())
        {
            var key = step.GetProperty("key").GetString() ?? string.Empty;
            var type = step.GetProperty("type").GetString() ?? string.Empty;
            FlowTaskDefinition? task = null;
            if (string.Equals(type, "automatic", StringComparison.Ordinal))
            {
                var taskElement = step.GetProperty("task");
                var retry = taskElement.GetProperty("retry");
                var inputMapping = taskElement.GetProperty("input_mapping");
                var inputConstants = taskElement.GetProperty("input_constants");
                task = new FlowTaskDefinition(
                    taskElement.GetProperty("module").GetString() ?? string.Empty,
                    taskElement.GetProperty("action").GetString() ?? string.Empty,
                    taskElement.GetProperty("action_version").GetInt32(),
                    ReadStringList(taskElement.GetProperty("required_policy")),
                    ReadStringMap(inputMapping),
                    inputConstants.EnumerateObject()
                        .Select(property => property.Name)
                        .ToHashSet(StringComparer.Ordinal),
                    retry.GetProperty("max_attempts").GetInt32(),
                    retry.GetProperty("delays_ms").EnumerateArray().Select(item => item.GetInt32()).ToList());
            }

            var allowedOutcomes = step.TryGetProperty("allowed_outcomes", out var allowed)
                ? ReadStringList(allowed)
                : [];
            var waitOutcome = step.TryGetProperty("outcome", out var outcomeElement) &&
                              outcomeElement.ValueKind == JsonValueKind.String
                ? outcomeElement.GetString() ?? string.Empty
                : string.Empty;

            steps.Add(new FlowStepDefinition(
                key,
                type,
                task,
                allowedOutcomes,
                waitOutcome));
        }

        return steps;
    }

    private static List<FlowTransitionDefinition> ParseTransitions(JsonElement document) =>
        document.GetProperty("transitions").EnumerateArray()
            .Select(transition => new FlowTransitionDefinition(
                transition.GetProperty("from").GetString() ?? string.Empty,
                transition.GetProperty("outcome").GetString() ?? string.Empty,
                transition.GetProperty("to").GetString() ?? string.Empty))
            .ToList();

    private static void ValidateStepOutcomes(
        FlowStepDefinition step,
        IReadOnlyDictionary<string, List<FlowTransitionDefinition>> adjacency)
    {
        var transitions = adjacency.TryGetValue(step.Key, out var outgoing)
            ? outgoing
            : [];

        if (string.Equals(step.Type, "end", StringComparison.Ordinal))
        {
            if (transitions.Count > 0)
            {
                throw new InvalidOperationException("end steps must not have outgoing transitions");
            }

            return;
        }

        var requiredOutcomes = step.Type switch
        {
            "wait_signal" => new[] { step.WaitOutcome },
            "manual" => step.AllowedOutcomes,
            _ => Array.Empty<string>()
        };

        foreach (var outcome in requiredOutcomes)
        {
            if (transitions.All(transition => !string.Equals(transition.Outcome, outcome, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("step outcome is missing a transition");
            }
        }
    }

    private static void ValidateRetry(int maxAttempts, IReadOnlyList<int> delaysMs)
    {
        if (delaysMs.Count != maxAttempts - 1)
        {
            throw new InvalidOperationException("retry delays length must equal max_attempts minus one");
        }
    }

    private static void ValidateMappingOverlap(IEnumerable<string> mappingKeys, IEnumerable<string> constantKeys)
    {
        var pointers = mappingKeys
            .Concat(constantKeys.Select(NormalizePointer))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        for (var left = 0; left < pointers.Count; left++)
        {
            for (var right = left + 1; right < pointers.Count; right++)
            {
                if (PointersOverlap(pointers[left], pointers[right]))
                {
                    throw new InvalidOperationException("input mapping pointers must not overlap");
                }
            }
        }
    }

    private static bool PointersOverlap(string left, string right) =>
        IsAncestorOrEqual(left, right) || IsAncestorOrEqual(right, left);

    private static bool IsAncestorOrEqual(string ancestor, string descendant)
    {
        if (string.Equals(ancestor, descendant, StringComparison.Ordinal))
        {
            return true;
        }

        if (!descendant.StartsWith(ancestor, StringComparison.Ordinal))
        {
            return false;
        }

        return descendant.Length > ancestor.Length && descendant[ancestor.Length] == '/';
    }

    private static string NormalizePointer(string pointer) =>
        pointer.StartsWith("/", StringComparison.Ordinal) ? pointer : $"/{pointer}";

    private async Task ValidateAutomaticStepAsync(
        FlowStepDefinition step,
        IReadOnlyDictionary<string, List<FlowTransitionDefinition>> adjacency,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT av.required_policy::text, av.outcomes::text, st.enabled
            FROM course.action_versions av
            LEFT JOIN course.action_state st
              ON st.module = av.module AND st.action = av.action AND st.version = av.version
            WHERE av.module = @module AND av.action = @action AND av.version = @version
            """,
            connection);
        command.Parameters.AddWithValue("module", step.Task!.Module);
        command.Parameters.AddWithValue("action", step.Task.Action);
        command.Parameters.AddWithValue("version", step.Task.ActionVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("pinned action version was not found");
        }

        var policyJson = reader.GetString(0) ?? "[]";
        var outcomesJson = reader.GetString(1) ?? "[]";
        var enabled = reader.IsDBNull(2) ? false : reader.GetBoolean(2);
        if (!enabled)
        {
            throw new InvalidOperationException("pinned action version is disabled");
        }

        using var policyDocument = JsonDocument.Parse(policyJson);
        var actionPolicy = policyDocument.RootElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        var taskPolicy = step.Task.RequiredPolicy.OrderBy(item => item, StringComparer.Ordinal).ToList();
        if (!actionPolicy.SequenceEqual(taskPolicy, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("task required_policy must match the published action");
        }

        using var outcomesDocument = JsonDocument.Parse(outcomesJson);
        var actionOutcomes = outcomesDocument.RootElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var transitionOutcomes = adjacency.TryGetValue(step.Key, out var outgoing)
            ? outgoing.Select(item => item.Outcome).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var outcome in actionOutcomes)
        {
            if (!transitionOutcomes.Contains(outcome))
            {
                throw new InvalidOperationException("automatic step is missing a transition for an action outcome");
            }
        }
    }

    private static HashSet<string> ComputeReachable(
        string startStep,
        IReadOnlyDictionary<string, List<FlowTransitionDefinition>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(startStep);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (!adjacency.TryGetValue(current, out var outgoing))
            {
                continue;
            }

            foreach (var transition in outgoing)
            {
                if (!visited.Contains(transition.To))
                {
                    queue.Enqueue(transition.To);
                }
            }
        }

        return visited;
    }

    private static bool HasCycle(
        string startStep,
        IReadOnlyDictionary<string, List<FlowTransitionDefinition>> adjacency)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in CollectNodes(startStep, adjacency))
        {
            if (!state.ContainsKey(node))
            {
                if (Visit(node))
                {
                    return true;
                }
            }
        }

        return false;

        bool Visit(string node)
        {
            state[node] = 1;
            if (adjacency.TryGetValue(node, out var outgoing))
            {
                foreach (var transition in outgoing)
                {
                    if (!state.TryGetValue(transition.To, out var nextState))
                    {
                        if (Visit(transition.To))
                        {
                            return true;
                        }
                    }
                    else if (nextState == 1)
                    {
                        return true;
                    }
                }
            }

            state[node] = 2;
            return false;
        }
    }

    private static HashSet<string> CollectNodes(
        string startStep,
        IReadOnlyDictionary<string, List<FlowTransitionDefinition>> adjacency)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal) { startStep };
        var queue = new Queue<string>();
        queue.Enqueue(startStep);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var outgoing))
            {
                continue;
            }

            foreach (var transition in outgoing)
            {
                if (nodes.Add(transition.To))
                {
                    queue.Enqueue(transition.To);
                }
            }
        }

        return nodes;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement array) =>
        array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToList();

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                map[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return map;
    }

    private sealed record FlowStepDefinition(
        string Key,
        string Type,
        FlowTaskDefinition? Task,
        IReadOnlyList<string> AllowedOutcomes,
        string WaitOutcome);

    private sealed record FlowTransitionDefinition(string From, string Outcome, string To);

    private sealed record FlowTaskDefinition(
        string Module,
        string Action,
        int ActionVersion,
        IReadOnlyList<string> RequiredPolicy,
        IReadOnlyDictionary<string, string> InputMapping,
        IReadOnlySet<string> InputConstants,
        int RetryMaxAttempts,
        IReadOnlyList<int> RetryDelaysMs);
}
