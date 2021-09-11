// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.AspNet.SignalR.SqlServer
{
    internal class DbOperation
    {
        private List<IDataParameter> _parameters = new List<IDataParameter>();
        private readonly SqlClientFactory _dbProviderFactory;
        protected readonly ILogger _logger;

        public DbOperation(string connectionString, string commandText, ILogger logger)
            : this(connectionString, commandText, logger, SqlClientFactory.Instance)
        {
        }

        public DbOperation(string connectionString, string commandText, ILogger logger, SqlClientFactory dbProviderFactory)
        {
            ConnectionString = connectionString;
            CommandText = commandText;
            _logger = logger;
            _dbProviderFactory = dbProviderFactory;
        }

        public DbOperation(string connectionString, string commandText, ILogger logger, params IDataParameter[] parameters)
            : this(connectionString, commandText, logger)
        {
            if (parameters != null)
            {
                _parameters.AddRange(parameters);
            }
        }

        public string? TracePrefix { get; set; }

        public IList<IDataParameter> Parameters
        {
            get { return _parameters; }
        }

        protected string ConnectionString { get; private set; }

        protected string CommandText { get; private set; }

        public virtual object? ExecuteScalar()
        {
            return Execute(cmd => cmd.ExecuteScalar());
        }

        public virtual int ExecuteNonQuery()
        {
            return Execute(cmd => cmd.ExecuteNonQuery());
        }

        public virtual Task<int> ExecuteNonQueryAsync()
        {
            return Execute(cmd => cmd.ExecuteNonQueryAsync());
        }

        public virtual int ExecuteReader(Action<IDataRecord, DbOperation> processRecord)
        {
            return ExecuteReader(processRecord, null);
        }

        protected virtual int ExecuteReader(Action<IDataRecord, DbOperation> processRecord, Action<IDbCommand>? commandAction)
        {
            return Execute(cmd =>
            {
                if (commandAction != null)
                {
                    commandAction(cmd);
                }

                var reader = cmd.ExecuteReader();
                var count = 0;

                while (reader.Read())
                {
                    count++;
                    processRecord(reader, this);
                }

                return count;
            });
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "It's the caller's responsibility to dispose as the command is returned"),
         SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "General purpose SQL utility command")]
        protected virtual SqlCommand CreateCommand(SqlConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = CommandText;

            if (Parameters != null && Parameters.Count > 0)
            {
                for (var i = 0; i < Parameters.Count; i++)
                {
                    command.Parameters.Add(Parameters[i].Clone(_dbProviderFactory));
                }
            }

            return command;
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "False positive?")]
        private T? Execute<T>(Func<SqlCommand, T> commandFunc)
        {
            T? result = default(T);
            SqlConnection? connection = null;

            try
            {
                connection = (SqlConnection)_dbProviderFactory.CreateConnection();
                connection.ConnectionString = ConnectionString;
                var command = CreateCommand(connection);
                connection.Open();
                TraceCommand(command);
                result = commandFunc(command);
            }
            finally
            {
                if (connection != null)
                {
                    connection.Dispose();
                }
            }

            return result;
        }

        private void TraceCommand(IDbCommand command)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Created DbCommand: CommandType={0}, CommandText={1}, Parameters={2}", command.CommandType, command.CommandText,
                    command.Parameters.Cast<IDataParameter>()
                        .Aggregate(string.Empty, (msg, p) => string.Format(CultureInfo.InvariantCulture, "{0} [Name={1}, Value={2}]", msg, p.ParameterName, p.Value))
                );
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "Disposed in async Finally block"),
         SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed in async Finally block")]
        private async Task<T> Execute<T>(Func<SqlCommand, Task<T>> commandFunc)
        {
            using var connection = (SqlConnection)_dbProviderFactory.CreateConnection();
            connection.ConnectionString = ConnectionString;
            using var command = CreateCommand(connection);

            connection.Open();

            return await commandFunc(command);
        }
    }
}
