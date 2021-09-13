﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.SignalR.SqlServer;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace Microsoft.AspNet.SignalR.SqlServer
{
    internal class SqlReceiver : IDisposable
    {
        private readonly SqlServerOptions _options;
        private readonly string _tableName;
        private readonly ILogger _logger;
        private readonly string _tracePrefix;

        private long? _lastPayloadId = null;
        private string _maxIdSql = "SELECT [PayloadId] FROM [{0}].[{1}_Id]";
        private string _selectSql = "SELECT [PayloadId], [Payload], [InsertedOn] FROM [{0}].[{1}] WHERE [PayloadId] > @PayloadId";
        private ObservableDbOperation? _dbOperation;
        private volatile bool _disposed;
        private CancellationTokenSource _receiveCancellation;

        public SqlReceiver(SqlServerOptions options, ILogger logger, string tableName, string tracePrefix)
        {
            _options = options;
            _tableName = tableName;
            _tracePrefix = tracePrefix;
            _logger = logger;

            Queried += () => { };
            Received += (_, __) => { };
            Faulted += _ => { };

            _maxIdSql = String.Format(CultureInfo.InvariantCulture, _maxIdSql, _options.SchemaName, _tableName);
            _selectSql = String.Format(CultureInfo.InvariantCulture, _selectSql, _options.SchemaName, _tableName);
        }

        public event Action Queried;

        public event Action<ulong, byte[]> Received;

        public event Action<Exception> Faulted;

        public Task StartReceiving()
        {
            var tcs = new TaskCompletionSource<object?>();

            Task.Factory.StartNew(Receive, tcs, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);

            return tcs.Task;
        }

        public void Dispose()
        {
            lock (this)
            {
                if (_dbOperation != null)
                {
                    _dbOperation.Dispose();
                }
                _disposed = true;
                _logger.LogInformation("{0}SqlReceiver disposed", _tracePrefix);
            }
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Class level variable"),
         SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "On a background thread with explicit error processing")]
        private void Receive(object? state)
        {
            var tcs = (TaskCompletionSource<object?>)state!;

            if (!_lastPayloadId.HasValue)
            {
                var lastPayloadIdOperation = new DbOperation(_options.ConnectionString, _maxIdSql, _logger)
                {
                    TracePrefix = _tracePrefix
                };

                try
                {
                    _lastPayloadId = (long?)lastPayloadIdOperation.ExecuteScalar();
                    Queried();

                    _logger.LogTrace("{0}SqlReceiver started, initial payload id={1}", _tracePrefix, _lastPayloadId);

                    // Complete the StartReceiving task as we've successfully initialized the payload ID
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{0}SqlReceiver error starting", _tracePrefix);

                    tcs.TrySetException(ex);
                    return;
                }
            }

            // NOTE: This is called from a BG thread so any uncaught exceptions will crash the process
            lock (this)
            {
                if (_disposed)
                {
                    return;
                }

                var parameter = SqlClientFactory.Instance.CreateParameter();
                parameter.ParameterName = "PayloadId";
                parameter.Value = _lastPayloadId.GetValueOrDefault();

                _dbOperation = new ObservableDbOperation(_options.ConnectionString, _selectSql, _logger, parameter)
                {
                    TracePrefix = _tracePrefix
                };
            }

            _dbOperation.Queried += () => Queried();
            _dbOperation.Faulted += ex => Faulted(ex);
            _dbOperation.Changed += () =>
            {
                _logger.LogInformation("{0}Starting receive loop again to process updates", _tracePrefix);

                _dbOperation.ExecuteReaderWithUpdates(ProcessRecord);
            };

            _logger.LogTrace("{0}Executing receive reader, initial payload ID parameter={1}", _tracePrefix, _dbOperation.Parameters[0].Value);

            _dbOperation.ExecuteReaderWithUpdates(ProcessRecord);

            _logger.LogInformation("{0}SqlReceiver.Receive returned", _tracePrefix);
        }

        private void ProcessRecord(IDataRecord record, DbOperation dbOperation)
        {
            var id = record.GetInt64(0);
            var payload = ((SqlDataReader)record).GetSqlBinary(1);

            _logger.LogTrace("{0}SqlReceiver last payload ID={1}, new payload ID={2}", _tracePrefix, _lastPayloadId, id);

            if (id > _lastPayloadId + 1)
            {
                _logger.LogError("{0}Missed message(s) from SQL Server. Expected payload ID {1} but got {2}.", _tracePrefix, _lastPayloadId + 1, id);
            }
            else if (id <= _lastPayloadId)
            {
                _logger.LogInformation("{0}Duplicate message(s) or payload ID reset from SQL Server. Last payload ID {1}, this payload ID {2}", _tracePrefix, _lastPayloadId, id);
            }

            _lastPayloadId = id;

            // Update the Parameter with the new payload ID
            dbOperation.Parameters[0].Value = _lastPayloadId;

            _logger.LogTrace("{0}Updated receive reader initial payload ID parameter={1}", _tracePrefix, _dbOperation?.Parameters[0].Value);

            _logger.LogTrace("{0}Payload {1} received", _tracePrefix, id);

            Received((ulong)id, (byte[]?)payload ?? Array.Empty<byte>());
        }
    }
}
