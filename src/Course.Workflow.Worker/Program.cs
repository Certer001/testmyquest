using Course.Workflow.Worker;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

var workflowConnection = builder.Configuration.GetConnectionString("Workflow")
    ?? throw new InvalidOperationException("ConnectionStrings:Workflow is required");
var leaseOwner = builder.Configuration["WORKFLOW_LEASE_OWNER"]
    ?? throw new InvalidOperationException("WORKFLOW_LEASE_OWNER is required");
var testProfile = string.Equals(builder.Configuration["COURSE_TEST_PROFILE"], "1", StringComparison.Ordinal);

builder.Services.AddSingleton(NpgsqlDataSource.Create(workflowConnection));
builder.Services.AddSingleton(sp => new WorkflowConnectionFactory(
    sp.GetRequiredService<NpgsqlDataSource>(),
    testProfile));
builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddSingleton(sp => new JobProcessor(
    sp.GetRequiredService<WorkflowConnectionFactory>(),
    sp.GetRequiredService<SchemaValidator>(),
    leaseOwner));
builder.Services.AddHostedService<WorkflowWorkerHostedService>();

var host = builder.Build();
await host.RunAsync();
