using System.Text.Json;
using Course.Shared;

namespace Course.Api;

public sealed class OpenApiService(ActionCatalogService catalog)
{
    public async Task<object?> BuildDefaultDocumentAsync(CancellationToken cancellationToken)
    {
        var actions = await catalog.ListEnabledDefaultsAsync(cancellationToken);
        return BuildDocument(actions);
    }

    public async Task<object?> BuildVersionDocumentAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        var definition = await catalog.GetExactVersionAsync(module, action, version, cancellationToken);
        return definition is null ? null : BuildDocument([definition]);
    }

    private static object BuildDocument(IReadOnlyList<ActionDefinition> actions)
    {
        var paths = new Dictionary<string, object>();
        foreach (var action in actions)
        {
            var path = $"/api/{action.Module}/{action.Action}";
            paths[path] = new
            {
                post = new
                {
                    operationId = $"{action.Module}_{action.Action}_v{action.Version}",
                    summary = $"{action.Module}.{action.Action} version {action.Version}",
                    parameters = new object[]
                    {
                        new
                        {
                            name = "X-Action-Version",
                            @in = "header",
                            required = false,
                            schema = new { type = "integer", example = action.Version }
                        },
                        new
                        {
                            name = "Idempotency-Key",
                            @in = "header",
                            required = action.IdempotencyMode == "required",
                            schema = new { type = "string" }
                        }
                    },
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = JsonSerializer.Deserialize<object>(action.RequestSchemaRaw)
                            }
                        }
                    },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Successful action execution",
                            content = new Dictionary<string, object>
                            {
                                ["application/json"] = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            status = new { type = "string" },
                                            outcome = new { type = "string" },
                                            result = JsonSerializer.Deserialize<object>(action.ResponseSchemaRaw),
                                            meta = new { type = "object" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        return new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "ModuleDev course-1 actions",
                version = ContractConstants.ContractVersion
            },
            paths
        };
    }
}
