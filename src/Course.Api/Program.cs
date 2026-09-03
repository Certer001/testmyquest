using System.Text.Json;
using Course.Api;
using Course.Shared;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var jwtSettings = new JwtSettings
{
    Issuer = builder.Configuration["COURSE_JWT_ISSUER"] ?? "moduledev-course",
    Audience = builder.Configuration["COURSE_JWT_AUDIENCE"] ?? "moduledev-api",
    SigningKey = builder.Configuration["COURSE_JWT_SIGNING_KEY"]
        ?? throw new InvalidOperationException("COURSE_JWT_SIGNING_KEY is required")
};

var runtimeConnection = builder.Configuration.GetConnectionString("Course")
    ?? throw new InvalidOperationException("ConnectionStrings:Course is required");
var migrationConnection = builder.Configuration.GetConnectionString("Migration")
    ?? throw new InvalidOperationException("ConnectionStrings:Migration is required");

var migrationDirectory = Path.Combine(AppContext.BaseDirectory, "migrations");
if (Directory.Exists(migrationDirectory))
{
    var migrationService = new MigrationService(migrationConnection);
    await ApplyMigrationsWithRetryAsync(migrationService, migrationDirectory);
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(runtimeConnection));
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<JwtAuthenticator>();
builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddSingleton<ActionCatalogService>();
builder.Services.AddSingleton<ActionExecutorService>();
builder.Services.AddSingleton<OpenApiService>();
builder.Services.AddSingleton(new InitializationState());

var app = builder.Build();
app.Services.GetRequiredService<InitializationState>().MarkReady();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (DependencyErrors.IsUnavailable(ex))
    {
        if (context.Response.HasStarted)
        {
            throw;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            HttpEnvelope.Error(
                "dependency.unavailable",
                "dependency is unavailable",
                Guid.NewGuid(),
                null));
    }
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, InitializationState state) =>
{
    if (!state.IsReady)
    {
        return Results.StatusCode(503);
    }

    try
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync();
        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

app.MapGet("/openapi/default.json", async (OpenApiService openApi, CancellationToken cancellationToken) =>
{
    var document = await openApi.BuildDefaultDocumentAsync(cancellationToken);
    return Results.Json(document, JsonDefaults.SerializerOptions);
});

app.MapGet("/openapi/actions/{module}/{action}/{version:int}.json", async (
    string module,
    string action,
    int version,
    OpenApiService openApi,
    CancellationToken cancellationToken) =>
{
    var document = await openApi.BuildVersionDocumentAsync(module, action, version, cancellationToken);
    return document is null ? Results.NotFound() : Results.Json(document, JsonDefaults.SerializerOptions);
});

app.MapPost("/api/{module}/{action}", async (
    HttpContext httpContext,
    string module,
    string action,
    JwtAuthenticator jwtAuthenticator,
    ActionExecutorService executor,
    CancellationToken cancellationToken) =>
{
    if (!jwtAuthenticator.TryAuthenticate(httpContext.Request.Headers.Authorization, out var principal, out _))
    {
        var correlationId = Guid.NewGuid();
        return Results.Json(
            HttpEnvelope.Error("auth.invalid", "authentication failed", correlationId, null),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    int? requestedVersion = null;
    if (httpContext.Request.Headers.TryGetValue("X-Action-Version", out var versionHeader))
    {
        if (!int.TryParse(versionHeader.ToString(), out var parsed) || parsed < 1)
        {
            var correlationId = Guid.NewGuid();
            return Results.Json(
                HttpEnvelope.Error("request.invalid", "invalid action version header", correlationId, null),
                statusCode: StatusCodes.Status400BadRequest);
        }

        requestedVersion = parsed;
    }

    JsonElement payload;
    try
    {
        using var document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }

        payload = document.RootElement.Clone();
    }
    catch
    {
        var correlationId = Guid.NewGuid();
        return Results.Json(
            HttpEnvelope.Error("request.invalid", "invalid JSON payload", correlationId, requestedVersion),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var idempotencyKey = httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyHeader)
        ? keyHeader.ToString()
        : null;

    var result = await executor.ExecuteAsync(
        module,
        action,
        requestedVersion,
        idempotencyKey,
        principal,
        payload,
        cancellationToken);

    if (result.StatusCode == 200)
    {
        return Results.Json(
            HttpEnvelope.Ok(
                result.Outcome!,
                JsonSerializer.Deserialize<object>(result.ResultJson)!,
                result.CorrelationId,
                result.ActionVersion!.Value),
            statusCode: StatusCodes.Status200OK);
    }

    return Results.Json(
        HttpEnvelope.Error(
            result.ErrorCode!,
            result.ErrorMessage!,
            result.CorrelationId,
            result.ActionVersion),
        statusCode: result.StatusCode);
});

app.Run();

static async Task ApplyMigrationsWithRetryAsync(MigrationService migrationService, string directory)
{
    var delays = new[] { 500, 1000, 2000, 3000, 5000, 5000, 5000, 5000 };
    Exception? lastError = null;

    foreach (var delay in delays)
    {
        try
        {
            await migrationService.ApplyAsync(directory);
            return;
        }
        catch (Exception ex) when (ex is NpgsqlException or MigrationConflictException)
        {
            lastError = ex;
            if (ex is MigrationConflictException)
            {
                throw;
            }

            await Task.Delay(delay);
        }
    }

    throw lastError ?? new InvalidOperationException("migration apply failed");
}

public sealed class InitializationState
{
    private int _ready;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public void MarkReady() => Volatile.Write(ref _ready, 1);
}
