using DemoServer;
using IntelliTect.AspNetCore.SignalR.SqlServer;
using IntelliTect.AspNetCore.SignalR.SqlServer.OpenTelemetry;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure app configuration
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.localhost.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Configure logging
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "hh:mm:ss ";
    options.SingleLine = false;
});

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

builder.Services.AddOpenTelemetry()
    .UseOtlpExporter()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddSqlClientInstrumentation()
    )
    .WithTracing(tracing => tracing
        .AddSource(builder.Environment.ApplicationName)
        .AddAspNetCoreInstrumentation()
       // .AddSignalRSqlServerInstrumentation()
        .AddSqlClientInstrumentation(tracing =>
        {
            tracing.SetDbStatementForText = true;
            tracing.Enrich = (activity, _, cmd) => activity.SetCustomProperty("sqlCommand", cmd);
        })
);


// Configure services
string connectionString = builder.Configuration.GetConnectionString("Default")!;

builder.Services.AddSingleton<IUserIdProvider, UserIdProvider>();
builder.Services.AddRazorPages();
builder.Services.AddSignalR()
    .AddSqlServer(o =>
    {
        o.ConnectionString = connectionString;
        o.AutoEnableServiceBroker = true;
        o.TableSlugGenerator = hubType => hubType.Name;
        o.TableCount = 1;
        o.SchemaName = "SignalRCore";
    });

builder.Services.AddOptions<SqlServerOptions>().Configure<IConfiguration>((o, config) =>
{
    o.ConnectionString = connectionString;
});

// Build the app
var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<ChatHubA>("/chatHubA");
app.MapHub<ChatHubB>("/chatHubB");

// Minimal API endpoint that uses HubContext
app.MapPost("/api/broadcast", async (IHubContext<ChatHubA> hubContext, BroadcastRequest request) =>
{
    await hubContext.Clients.All.SendAsync("BroadcastMessage", "Server", request.Message);
    return Results.Ok(new { success = true, message = "Message broadcasted successfully" });
});

app.Run();

public record BroadcastRequest(string Message);
