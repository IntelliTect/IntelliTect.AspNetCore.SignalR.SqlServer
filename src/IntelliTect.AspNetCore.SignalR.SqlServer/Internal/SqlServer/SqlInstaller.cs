// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlInstaller(SqlServerOptions options, ILogger logger, string messagesTableNamePrefix, string tracePrefix)
    {
        private const int SchemaVersion = 1;

        public async Task Install()
        {
            if (!options.AutoEnableServiceBroker && !options.AutoInstallSchema)
            {
                logger.LogInformation("{HubName}: Skipping install of SignalR SQL objects", tracePrefix);
                return;
            }

            await options.InstallLock.WaitAsync();
            logger.LogInformation("{HubName}: Start installing SignalR SQL objects", tracePrefix);
            try
            {
                using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();

                if (options.AutoEnableServiceBroker)
                {
                    try
                    {
                        command.CommandText = GetType().Assembly.StringResource("enable-broker.sql");
                        await command.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unable to automatically enable SQL Server Service Broker.");
                    }
                }

                if (options.AutoInstallSchema)
                {
                    var script = GetType().Assembly.StringResource("install.sql");

                    script = script.Replace("SET @SCHEMA_NAME = 'SignalR';", "SET @SCHEMA_NAME = '" + options.SchemaName + "';");
                    script = script.Replace("SET @TARGET_SCHEMA_VERSION = 1;", "SET @TARGET_SCHEMA_VERSION = " + SchemaVersion + ";");
                    script = script.Replace("SET @MESSAGE_TABLE_COUNT = 1;", "SET @MESSAGE_TABLE_COUNT = " + options.TableCount + ";");
                    script = script.Replace("SET @MESSAGE_TABLE_NAME = 'Messages_YourHubName';", "SET @MESSAGE_TABLE_NAME = '" + messagesTableNamePrefix + "';");

                    command.CommandText = script;
                    await command.ExecuteNonQueryAsync();

                    logger.LogInformation("{HubName}: SignalR SQL objects installed", messagesTableNamePrefix);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{HubName}: Unable to install SignalR SQL objects", messagesTableNamePrefix);
                throw;
            }
            finally
            {
                options.InstallLock.Release();
            }
        }
    }
}
