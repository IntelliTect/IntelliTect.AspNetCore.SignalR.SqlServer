# IntelliTect.AspNetCore.SignalR.SqlServer

A Microsoft SQL Server backplane for ASP.NET Core SignalR.

[![Nuget](https://img.shields.io/nuget/v/IntelliTect.AspNetCore.SignalR.SqlServer)](https://www.nuget.org/packages/IntelliTect.AspNetCore.SignalR.SqlServer/)


This project is largely based off of a fork of the [SignalR Core Redis provider](https://github.com/dotnet/aspnetcore/tree/main/src/SignalR/server/StackExchangeRedis), reworked to use the underlying concepts of the [classic ASP.NET SignalR SQL Server backplane](https://github.com/SignalR/SignalR/tree/main/src/Microsoft.AspNet.SignalR.SqlServer). This means it supports subscription-based messaging via SQL Server Service Broker, falling back on periodic polling when not available.


## SQL Server Configuration

For optimal responsiveness and performance, [SQL Server Service Broker](https://docs.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-service-broker?view=sql-server-ver15) should be enabled on your database. NOTE: **Service Broker is not available on Azure SQL Database.** If you are running in Azure, consider a Redis or Azure SignalR Service backplane instead.

If Service Broker is not available, a fallback of periodic queries is used. This fallback querying happens very rapidly when messages are encountered, but slows down once traffic stops.

You can check if Service Broker is enabled with the following query:
``` sql
SELECT [name], [service_broker_guid], [is_broker_enabled]
FROM [master].[sys].[databases]
```

To enable it, execute the following against the database. Note that this requires an exclusive lock over the database:
``` sql
ALTER DATABASE [DatabaseName] SET ENABLE_BROKER WITH NO_WAIT;
```

If the above command does not work due to existing connections, try terminating existing sessions automatically using
``` sql 
ALTER DATABASE [DatabaseName] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE
```

You can also set `AutoEnableServiceBroker = true` when configuring in your `Startup.cs`, but this requires that the application have permissions to do so and has the same caveats that there can be no other active database sessions.

## Usage

1. Install the `IntelliTect.AspNetCore.SignalR.SqlServer` NuGet package.
2. In `ConfigureServices` in `Startup.cs`, configure SignalR with `.UseSqlServer()`:


Simple configuration:
``` cs
services
    .AddSignalR()
    .AddSqlServer(Configuration.GetConnectionString("Default"));
```

Advanced configuration:

``` cs 
services
    .AddSignalR()
    .AddSqlServer(o =>
    {
        o.ConnectionString = Configuration.GetConnectionString("Default");
        // See above - attempts to enable Service Broker on the database at startup
        // if not already enabled. Default false, as this can hang if the database has other sessions.
        o.AutoEnableServiceBroker = true;
        // Every hub has its own message table(s). 
        // This determines the part of the table named that is derived from the hub name.
        // IF THIS IS NOT UNIQUE AMONG ALL HUBS, YOUR HUBS WILL COLLIDE AND MESSAGES MIX.
        o.TableSlugGenerator = hubType => hubType.Name;
        // The number of tables per Hub to use. Adding a few extra could increase throughput
        // by reducing table contention, but all servers must agree on the number of tables used.
        // If you find that you need to increase this, it is probably a hint that you need to switch to Redis.
        o.TableCount = 1;
        // The SQL Server schema to use for the backing tables for this backplane.
        o.SchemaName = "SignalRCore";
    });
```

Alternatively, you may configure `IntelliTect.AspNetCore.SignalR.SqlServer.SqlServerOptions` with [the Options pattern](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-5.0).

``` cs
services.Configure<SqlServerOptions>(Configuration.GetSection("SignalR:SqlServer"));
```

## Caveats

As mentioned above, if SQL Server Service Broker is not available, messages will not always be transmitted immediately since a fallback of periodic querying must be used.

### Performance

This is not the right solution for applications with a need for very high throughput, or very high degrees of scale-out. Consider Redis or Azure SignalR Service instead for such cases. You should always do an appropriate amount of testing to determine if a particular solution is suitable for your application. 

The results of some ad-hoc performance testing yielded that you can expect about 1000 messages per second per table (setting `TableCount`). However, increasing table count does have diminishing returns; attempting to push 20,000 messages/sec with 20 tables had a throughput of only ~10,000 messages/sec. This was observed with a SQL Server instance and load generator running on the same machine, an i9 9900k with an SSD, with a message size of ~200 bytes. A Redis backplane on the same hardware sustained 20,000 messages per second without issue.

Do note that a broadcast message is considered a single message. Any call to `SendAsync` within a hub is a single message.

## License

[Apache 2.0](./LICENSE.txt). 

Credit to Microsoft for both Microsoft.AspNet.SignalR.SqlServer and Microsoft.AspNetCore.SignalR.StackExchangeRedis, upon which this project is based.
