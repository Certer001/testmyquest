using System.Text.Json;
using Course.Shared;

namespace Course.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var commandArgs = args;
        if (commandArgs.Length > 0 && commandArgs[0] == "course.sh")
        {
            commandArgs = commandArgs[1..];
        }

        if (commandArgs.Length == 0)
        {
            return WriteError("request.invalid", "command is required");
        }

        var publicationConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Publication")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_publication;Password=course_publication";
        var migrationConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Migration")
            ?? publicationConnection;

        try
        {
            return commandArgs[0] switch
            {
                "migration" when commandArgs is ["migration", "apply", var directory] =>
                    RunMigration(migrationConnection, directory),
                "action" when commandArgs is ["action", "validate", var manifest] =>
                    RunValidate(publicationConnection, manifest),
                "action" when commandArgs is ["action", "publish", var manifest] =>
                    RunPublish(publicationConnection, manifest),
                "action" when commandArgs is ["action", "list"] =>
                    RunList(publicationConnection),
                "action" when commandArgs.Length >= 3 && commandArgs[1] == "activate" =>
                    RunActivate(publicationConnection, commandArgs[2], commandArgs),
                "action" when commandArgs.Length >= 3 && commandArgs[1] == "disable" =>
                    RunDisable(publicationConnection, commandArgs[2], commandArgs),
                "flow" when commandArgs is ["flow", "validate", var mapPath] =>
                    RunFlowValidate(publicationConnection, mapPath),
                "flow" when commandArgs is ["flow", "publish", var mapPath] =>
                    RunFlowPublish(publicationConnection, mapPath),
                "flow" when commandArgs is ["flow", "list"] =>
                    RunFlowList(publicationConnection),
                "flow" when commandArgs.Length >= 3 && commandArgs[1] == "activate" =>
                    RunFlowActivate(publicationConnection, commandArgs[2], commandArgs),
                "flow" when commandArgs.Length >= 5 && commandArgs[1] == "start" =>
                    RunFlowStart(publicationConnection, commandArgs),
                "flow" when commandArgs.Length >= 3 && commandArgs[1] == "get" =>
                    RunFlowGet(publicationConnection, commandArgs[2]),
                "flow" when commandArgs.Length >= 3 && commandArgs[1] == "signal" =>
                    RunFlowSignal(publicationConnection, commandArgs),
                "flow" when commandArgs.Length >= 3 && commandArgs[1] == "test-finish" =>
                    RunFlowTestFinish(publicationConnection, commandArgs),
                _ => WriteError("request.invalid", "unsupported command")
            };
        }
        catch (MigrationConflictException error)
        {
            return WriteError("migration.conflict", error.Message);
        }
        catch (PublicationConflictException error)
        {
            return WriteError("manifest.conflict", error.Message);
        }
        catch (FlowConflictException error)
        {
            return WriteError("flow.conflict", error.Message);
        }
        catch (FlowNotFoundException error)
        {
            return WriteError("flow.not_found", error.Message);
        }
        catch (WorkflowLeaseStaleException error)
        {
            return WriteError("workflow.lease_stale", error.Message);
        }
        catch (WorkflowMappingMissingException error)
        {
            return WriteError("workflow.mapping_missing", error.Message);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return WriteError("internal.error", "command failed");
        }
    }

    private static int RunMigration(string connection, string directory)
    {
        var service = new MigrationService(connection);
        service.ApplyAsync(directory).GetAwaiter().GetResult();
        return WriteOk(new { resource = "migration", operation = "applied", key = directory });
    }

    private static int RunValidate(string connection, string manifest)
    {
        var service = new ActionPublicationService(connection);
        service.ValidateOnlyAsync(manifest).GetAwaiter().GetResult();
        var parsed = ManifestParser.Parse(File.ReadAllText(manifest));
        return WriteOk(new
        {
            resource = "action",
            operation = "validated",
            key = $"{parsed.Module}.{parsed.Action}",
            version = parsed.Version
        });
    }

    private static int RunPublish(string connection, string manifest)
    {
        var service = new ActionPublicationService(connection);
        service.PublishAsync(manifest).GetAwaiter().GetResult();
        var parsed = ManifestParser.Parse(File.ReadAllText(manifest));
        return WriteOk(new
        {
            resource = "action",
            operation = "published",
            key = $"{parsed.Module}.{parsed.Action}",
            version = parsed.Version
        });
    }

    private static int RunList(string connection)
    {
        var service = new ActionPublicationService(connection);
        var items = service.ListAsync().GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "action",
            operation = "listed",
            items = items.Select(item => new
            {
                module = item.Module,
                action = item.Action,
                version = item.Version,
                enabled = item.Enabled,
                isDefault = item.IsDefault
            })
        });
    }

    private static int RunActivate(string connection, string routeKey, string[] args)
    {
        var version = ParseVersion(args, "--version");
        var service = new ActionPublicationService(connection);
        service.ActivateAsync(routeKey, version).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "action",
            operation = "activated",
            key = routeKey,
            version
        });
    }

    private static int RunDisable(string connection, string routeKey, string[] args)
    {
        var version = ParseVersion(args, "--version");
        int? replacement = args.Contains("--replacement-version")
            ? ParseVersion(args, "--replacement-version")
            : null;
        var service = new ActionPublicationService(connection);
        service.DisableAsync(routeKey, version, replacement).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "action",
            operation = "disabled",
            key = routeKey,
            version
        });
    }

    private static int RunFlowValidate(string connection, string mapPath)
    {
        var service = new FlowPublicationService(connection);
        service.ValidateOnlyAsync(mapPath).GetAwaiter().GetResult();
        var parsed = FlowMapParser.ParseFileAsync(mapPath).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "flow",
            operation = "validated",
            key = parsed.FlowName,
            version = parsed.Version
        });
    }

    private static int RunFlowPublish(string connection, string mapPath)
    {
        var service = new FlowPublicationService(connection);
        service.PublishAsync(mapPath).GetAwaiter().GetResult();
        var parsed = FlowMapParser.ParseFileAsync(mapPath).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "flow",
            operation = "published",
            key = parsed.FlowName,
            version = parsed.Version
        });
    }

    private static int RunFlowList(string connection)
    {
        var service = new FlowPublicationService(connection);
        var items = service.ListAsync().GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "flow",
            operation = "listed",
            items = items.Select(item => new
            {
                flowName = item.FlowName,
                version = item.FlowVersion,
                isActive = item.IsActive,
                status = item.Status
            })
        });
    }

    private static int RunFlowActivate(string connection, string flowName, string[] args)
    {
        var version = ParseVersion(args, "--version");
        var service = new FlowPublicationService(connection);
        service.ActivateAsync(flowName, version).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "flow",
            operation = "activated",
            key = flowName,
            version
        });
    }

    private static int RunFlowStart(string connection, string[] args)
    {
        var flowName = args[2];
        var businessKey = ParseRequired(args, "--business-key");
        var dataPath = ParseRequired(args, "--data");
        using var document = JsonDocument.Parse(ReadJsonInput(dataPath));
        var service = new FlowRuntimeService(connection);
        var process = service.StartAsync(flowName, businessKey, document.RootElement).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "process",
            operation = "started",
            processId = process.ProcessId.ToString(),
            flowName = process.FlowName,
            flowVersion = process.FlowVersion,
            state = process.State
        });
    }

    private static int RunFlowGet(string connection, string processIdText)
    {
        if (!Guid.TryParse(processIdText, out var processId))
        {
            throw new InvalidOperationException("process id must be a uuid");
        }

        var service = new FlowRuntimeService(connection);
        var process = service.GetAsync(processId).GetAwaiter().GetResult();
        return WriteOk(new
        {
            resource = "process",
            operation = "fetched",
            processId = process.ProcessId.ToString(),
            flowName = process.FlowName,
            flowVersion = process.FlowVersion,
            state = process.State,
            currentStepKey = process.CurrentStepKey
        });
    }

    private static int RunFlowSignal(string connection, string[] args)
    {
        if (!Guid.TryParse(args[2], out var processId))
        {
            throw new InvalidOperationException("process id must be a uuid");
        }

        var signalType = ParseRequired(args, "--type");
        var messageId = ParseRequired(args, "--message-id");
        var payloadPath = ParseRequired(args, "--payload");
        using var document = JsonDocument.Parse(ReadJsonInput(payloadPath));
        var service = new FlowRuntimeService(connection);
        var result = service.SignalAsync(processId, signalType, messageId, document.RootElement)
            .GetAwaiter()
            .GetResult();
        return WriteOk(new
        {
            resource = "signal",
            operation = "delivered",
            processId = processId.ToString(),
            status = result.Status
        });
    }

    private static int RunFlowTestFinish(string connection, string[] args)
    {
        if (!Guid.TryParse(args[2], out var jobId))
        {
            throw new InvalidOperationException("job id must be a uuid");
        }

        var owner = ParseRequired(args, "--owner");
        var leaseVersion = ParseLong(args, "--lease-version");
        var outcome = ParseRequired(args, "--outcome");
        var resultPath = ParseRequired(args, "--result");
        using var document = JsonDocument.Parse(ReadJsonInput(resultPath));
        var service = new FlowRuntimeService(connection);
        service.TestFinishAsync(jobId, owner, leaseVersion, outcome, document.RootElement)
            .GetAwaiter()
            .GetResult();
        return WriteOk(new
        {
            resource = "job",
            operation = "finished",
            jobId = jobId.ToString()
        });
    }

    private static string ReadJsonInput(string path) =>
        path == "/dev/stdin" ? Console.In.ReadToEnd() : File.ReadAllText(path);

    private static string ParseRequired(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{flag} is required");
        }

        return args[index + 1];
    }

    private static int ParseVersion(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var version))
        {
            throw new InvalidOperationException("version is required");
        }

        return version;
    }

    private static long ParseLong(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length || !long.TryParse(args[index + 1], out var value))
        {
            throw new InvalidOperationException($"{flag} is required");
        }

        return value;
    }

    private static int WriteOk(object result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(CliResponse.Ok(result)));
        Console.Out.Flush();
        return 0;
    }

    private static int WriteError(string code, string message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(CliResponse.Error(code, message)));
        Console.Out.Flush();
        return 1;
    }
}
