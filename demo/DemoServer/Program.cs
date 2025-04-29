using DemoServer;
using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.AspNetCore.SignalR;

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

app.Run();
