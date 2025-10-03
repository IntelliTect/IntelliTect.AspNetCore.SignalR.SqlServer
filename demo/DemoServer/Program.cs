using System.Data.Common;
using System.Diagnostics;
using DemoServer;
using IntelliTect.AspNetCore.SignalR.SqlServer;
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
        .AddMeter("IntelliTect.AspNetCore.SignalR.SqlServer")
    )
    .WithTracing(tracing => tracing
        .AddSource(builder.Environment.ApplicationName)
        .AddSource("IntelliTect.AspNetCore.SignalR.SqlServer")
        .AddProcessor<SignalRMessagesNoiseFilterProcessor>()
        .AddAspNetCoreInstrumentation()
        .AddSqlClientInstrumentation()
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


internal sealed class SignalRMessagesNoiseFilterProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Status != ActivityStatusCode.Error &&
            activity.Duration.TotalMilliseconds < 100 &&
            (activity.GetTagItem("db.query.text") ?? activity.GetTagItem("db.statement")) is string command &&
            command.StartsWith("SELECT [PayloadId], [Payload], [InsertedOn] FROM [SignalR"))
        {
            // Sample out successful and fast SignalR queries
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}