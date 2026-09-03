using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
var apiBaseUrl = builder.Configuration["API_BASE_URL"] ?? "http://api:8080";
builder.Services.AddHttpClient("api", client => client.BaseAddress = new Uri(apiBaseUrl));

var app = builder.Build();
var clientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async () =>
{
    var client = clientFactory.CreateClient("api");
    try
    {
        using var response = await client.GetAsync("/health/ready");
        return response.IsSuccessStatusCode
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(503);
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (!IsAllowedPath(path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var client = clientFactory.CreateClient("api");
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path + context.Request.QueryString);

    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Content-Type"))
    {
        request.Content = new StreamContent(context.Request.Body);
        if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
        {
            request.Content.Headers.ContentType = mediaType;
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    HttpResponseMessage response;
    try
    {
        response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);
    }
    catch
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = "error",
            code = "dependency.unavailable",
            message = "dependency is unavailable",
            retryable = false,
            details = new { },
            meta = new { correlationId = Guid.NewGuid().ToString(), actionVersion = (int?)null }
        });
        return;
    }

    using (response)
    {
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
});

app.Run();

static bool IsAllowedPath(string path) =>
    path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/openapi/default.json", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/openapi/actions/", StringComparison.OrdinalIgnoreCase);
