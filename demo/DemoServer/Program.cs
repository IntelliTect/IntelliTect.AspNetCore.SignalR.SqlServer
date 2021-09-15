using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DemoServer
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>()
                    .ConfigureAppConfiguration((builder, config) => config
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .AddJsonFile("appsettings.localhost.json", optional: true, reloadOnChange: true)
                        .AddEnvironmentVariables()
                    )
                    .ConfigureLogging(builder =>
                    {
                        builder.AddSimpleConsole(options =>
                        {
                            options.TimestampFormat = "hh:mm:ss ";
                            options.SingleLine = true;
                        });
                    });
                });
    }
}
