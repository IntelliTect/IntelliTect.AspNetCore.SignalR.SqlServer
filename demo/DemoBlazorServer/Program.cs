using DemoBlazorServer.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.localhost.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddSignalR()
    .AddSqlServer(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Default");
        o.AutoEnableServiceBroker = true;
        o.TableSlugGenerator = hubType => hubType.Name;
        o.TableCount = 1;
        o.SchemaName = "SignalRCore";
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapHub<ChatHubA>("/chatHubA");
app.MapHub<ChatHubB>("/chatHubB");
app.MapFallbackToPage("/_Host");

app.Run();
