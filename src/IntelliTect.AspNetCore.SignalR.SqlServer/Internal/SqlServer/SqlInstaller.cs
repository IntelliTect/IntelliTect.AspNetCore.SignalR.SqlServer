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
    internal class SqlInstaller
    {
        private const int SchemaVersion = 1;

        private readonly string _messagesTableNamePrefix;
        private readonly ILogger _logger;
        private readonly SqlServerOptions _options;

        public SqlInstaller(SqlServerOptions options, ILogger logger, string messagesTableNamePrefix)
        {
            _logger = logger;
            _options = options;
            _messagesTableNamePrefix = messagesTableNamePrefix;
        }

        public async Task Install()
        {
            _logger.LogInformation("Start installing SignalR SQL objects");
            try
            {
                using var connection = new SqlConnection(_options.ConnectionString);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();

                if (_options.AutoEnableServiceBroker)
                {
                    try
                    {
                        command.CommandText = GetType().Assembly.StringResource("enable-broker.sql");
                        await command.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to automatically enable SQL Server Service Broker.");
                    }
                }

                var script = GetType().Assembly.StringResource("install.sql");

                script = script.Replace("SET @SCHEMA_NAME = 'SignalR';", "SET @SCHEMA_NAME = '" + _options.SchemaName + "';");
                script = script.Replace("SET @TARGET_SCHEMA_VERSION = 1;", "SET @TARGET_SCHEMA_VERSION = " + SchemaVersion + ";");
                script = script.Replace("SET @MESSAGE_TABLE_COUNT = 1;", "SET @MESSAGE_TABLE_COUNT = " + _options.TableCount + ";");
                script = script.Replace("SET @MESSAGE_TABLE_NAME = 'Messages';", "SET @MESSAGE_TABLE_NAME = '" + _messagesTableNamePrefix + "';");

                command.CommandText = script;
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("SignalR SQL objects installed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to install SignalR SQL objects");
                throw;
            }

        }
    }
}
