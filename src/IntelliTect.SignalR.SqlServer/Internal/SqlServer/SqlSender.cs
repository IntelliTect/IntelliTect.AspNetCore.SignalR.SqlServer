// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using IntelliTect.SignalR.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNet.SignalR.SqlServer
{
    internal class SqlSender
    {
        private readonly string _connectionString;
        private readonly string _insertDml;
        private readonly ILogger _logger;
        private readonly SqlServerOptions _options;

        public SqlSender(SqlServerOptions options, ILogger logger, string tableName)
        {
            _options = options;
            _connectionString = options.ConnectionString;
            _insertDml = BuildInsertString(tableName);
            _logger = logger;
        }

        private string BuildInsertString(string tableName)
        {
            var insertDml = GetType().Assembly.StringResource("send.sql");

            return insertDml.Replace("[SignalR]", String.Format(CultureInfo.InvariantCulture, "[{0}]", _options.SchemaName))
                            .Replace("[Messages_0", String.Format(CultureInfo.InvariantCulture, "[{0}", tableName));
        }

        public Task Send(byte[] message)
        {
            var parameter = SqlClientFactory.Instance.CreateParameter();
            parameter.ParameterName = "Payload";
            parameter.DbType = DbType.Binary;
            parameter.Value = message;

            var operation = new DbOperation(_connectionString, _insertDml, _logger, parameter);

            return operation.ExecuteNonQueryAsync();
        }
    }
}
