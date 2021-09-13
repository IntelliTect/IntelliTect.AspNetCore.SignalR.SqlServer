// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Microsoft.AspNet.SignalR.SqlServer
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Query doesn't come from user code")]
        public void Install()
        {
            _logger.LogInformation("Start installing SignalR SQL objects");

            string script;
            DbOperation operation;

            if (_options.AutoEnableServiceBroker)
            {
                try
                {
                    script = GetType().Assembly.StringResource("enable-broker.sql");
                    operation = new DbOperation(_options.ConnectionString, script, _logger);
                    operation.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to automatically enable SQL Server Service Broker.");
                }
            }

            script = GetType().Assembly.StringResource("install.sql");

            script = script.Replace("SET @SCHEMA_NAME = 'SignalR';", "SET @SCHEMA_NAME = '" + _options.SchemaName + "';");
            script = script.Replace("SET @TARGET_SCHEMA_VERSION = 1;", "SET @TARGET_SCHEMA_VERSION = " + SchemaVersion + ";");
            script = script.Replace("SET @MESSAGE_TABLE_COUNT = 1;", "SET @MESSAGE_TABLE_COUNT = " + _options.TableCount + ";");
            script = script.Replace("SET @MESSAGE_TABLE_NAME = 'Messages';", "SET @MESSAGE_TABLE_NAME = '" + _messagesTableNamePrefix + "';");

            operation = new DbOperation(_options.ConnectionString, script, _logger);
            operation.ExecuteNonQuery();

            _logger.LogInformation("SignalR SQL objects installed");
        }
    }
}
