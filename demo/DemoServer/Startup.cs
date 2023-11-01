using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DemoServer
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            string connectionString = Configuration.GetConnectionString("Default")!;

            services.AddSingleton<IUserIdProvider, UserIdProvider>();

            services.AddRazorPages();
            services.AddSignalR()
                .AddSqlServer(o =>
                {
                    o.ConnectionString = connectionString;
                    o.AutoEnableServiceBroker = true;
                    o.TableSlugGenerator = hubType => hubType.Name;
                    o.TableCount = 1;
                    o.SchemaName = "SignalRCore";
                });

            // Example of using DI to configure options:
            services.AddOptions<SqlServerOptions>().Configure<IConfiguration>((o, config) =>
            {
                o.ConnectionString = connectionString;
            });

            // Register specific hubs with specific backplanes:
            //services.AddSingleton<HubLifetimeManager<ChatHubB>, DefaultHubLifetimeManager<ChatHubB>>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapHub<ChatHubA>("/chatHubA");
                endpoints.MapHub<ChatHubB>("/chatHubB");
            });
        }
    }
}
