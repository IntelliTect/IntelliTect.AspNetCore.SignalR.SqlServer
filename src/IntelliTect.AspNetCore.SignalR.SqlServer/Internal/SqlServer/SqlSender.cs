// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlSender
    {
        private static readonly Counter<long> _rowsWrittenCounter = SqlServerOptions.Meter.CreateCounter<long>(
            "signalr.sqlserver.rows_written_total",
            "rows",
            "Total number of message rows written to SQL Server");
            
        private readonly string _insertDml;
        private readonly ILogger _logger;
        private readonly SqlServerOptions _options;
        private readonly string _hubName;

        public SqlSender(SqlServerOptions options, ILogger logger, string tableName, string hubName)
        {
            _options = options;
            _insertDml = BuildInsertString(tableName);
            _logger = logger;
            _hubName = hubName;
        }

        private string BuildInsertString(string tableName)
        {
            var insertDml = GetType().Assembly.StringResource("send.sql");

            return insertDml.Replace("[SignalR]", String.Format(CultureInfo.InvariantCulture, "[{0}]", _options.SchemaName))
                            .Replace("[Messages_0", String.Format(CultureInfo.InvariantCulture, "[{0}", tableName));
        }

        public async Task Send(byte[] message)
        {
            using var connection = new SqlConnection(_options.ConnectionString);
            using var command = new SqlCommand
            {
                Connection = connection,
                CommandText = _insertDml,
            };
            command.Parameters.Add(new SqlParameter("Payload", message));
            await connection.OpenAsync();

            await command.ExecuteNonQueryAsync();
            
            // Record rows written metric
            _rowsWrittenCounter.Add(1, new KeyValuePair<string, object?>("signalr.hub", _hubName));
        }
    }
}
