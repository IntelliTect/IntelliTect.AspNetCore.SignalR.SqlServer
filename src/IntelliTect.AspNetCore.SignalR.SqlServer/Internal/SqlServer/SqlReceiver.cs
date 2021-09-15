// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlReceiver : IDisposable
    {
        private readonly Tuple<int, int>[] _updateLoopRetryDelays = new[] {
            Tuple.Create(0, 3),    // 0ms x 3
            Tuple.Create(10, 3),   // 10ms x 3
            Tuple.Create(50, 2),   // 50ms x 2
            Tuple.Create(100, 2),  // 100ms x 2
            Tuple.Create(200, 2),  // 200ms x 2
            Tuple.Create(1000, 2), // 1000ms x 2
            Tuple.Create(1500, 2), // 1500ms x 2
            Tuple.Create(3000, 1)  // 3000ms x 1
        };
        private readonly static TimeSpan _dependencyTimeout = TimeSpan.FromSeconds(60);

        private CancellationTokenSource _cts = new();
        private bool _notificationsDisabled;

        private readonly SqlServerOptions _options;
        private readonly string _tableName;
        private readonly ILogger _logger;
        private readonly string _tracePrefix;

        private long? _lastPayloadId = null;
        private Func<long, byte[], Task>? _onReceived = null;
        private bool _disposed;
        private readonly string _maxIdSql = "SELECT [PayloadId] FROM [{0}].[{1}_Id]";
        private readonly string _selectSql = "SELECT [PayloadId], [Payload], [InsertedOn] FROM [{0}].[{1}] WHERE [PayloadId] > @PayloadId";

        public SqlReceiver(SqlServerOptions options, ILogger logger, string tableName, string tracePrefix)
        {
            _options = options;
            _tableName = tableName;
            _tracePrefix = tracePrefix;
            _logger = logger;

            _maxIdSql = String.Format(CultureInfo.InvariantCulture, _maxIdSql, _options.SchemaName, _tableName);
            _selectSql = String.Format(CultureInfo.InvariantCulture, _selectSql, _options.SchemaName, _tableName);
        }

        public Task Start(Func<long, byte[], Task> onReceived)
        {
            if (_disposed) throw new ObjectDisposedException(null);

            _cts.Cancel();
            _cts.Dispose();
            _cts = new();

            _onReceived = onReceived;

            return Task.Factory.StartNew(StartLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);
        }

        private async Task StartLoop()
        {
            if (_cts.IsCancellationRequested) return;

            if (!_lastPayloadId.HasValue)
            {
                _lastPayloadId = await GetLastPayloadId();
            }

            if (_cts.IsCancellationRequested) return;

            Func<CancellationToken, Task> loop = StartSqlDependencyListener()
                ? NotificationLoop
                : PollingLoop;

            await loop(_cts.Token);
        }

        /// <summary>
        /// Loop until cancelled, using SQL service broker notifications to watch for new rows.
        /// </summary>
        private async Task NotificationLoop(CancellationToken cancellationToken)
        {
            while (StartSqlDependencyListener())
            {
                if (cancellationToken.IsCancellationRequested) return;

                try
                {
                    _logger.LogDebug("{0}Setting up SQL notification", _tracePrefix);

                    var tcs = new TaskCompletionSource<SqlNotificationEventArgs>();
                    var recordCount = await ReadRows(command =>
                    {
                        var dependency = new SqlDependency(command, null, (int)_dependencyTimeout.TotalSeconds);
                        dependency.OnChange += (o, e) => tcs.SetResult(e);
                    });

                    if (recordCount > 0)
                    {
                        // If we got one message, there might be more coming right after.
                        // Keep consuming as fast as we can until we stop getting more messages.
                        while (recordCount > 0)
                        {
                            recordCount = await ReadRows(null);
                        }

                        _logger.LogDebug("{0}Records were returned by the command that sets up the SQL notification, restarting the receive loop", _tracePrefix);
                        continue;
                    }

                    _logger.LogTrace("{0}No records received while setting up SQL notification", _tracePrefix);

                    var depResult = await tcs.Task;
                    if (cancellationToken.IsCancellationRequested) return;

                    // Check notification args for issues
                    switch (depResult.Type)
                    {
                        case SqlNotificationType.Change when depResult.Info is SqlNotificationInfo.Update:
                            // Typically means new records are available. (TODO: verify this?).
                            // Loop again to pick them up by performing another query.
                            _logger.LogTrace("{0}SQL notification details: Type={1}, Source={2}, Info={3}", _tracePrefix, depResult.Type, depResult.Source, depResult.Info);

                            // Read rows immediately, since on the next loop we know for certain there will be rows.
                            // There's no point doing a full loop that includes setting up a SqlDependency that
                            // will go unused due to the fact that we pulled back rows.
                            do
                            {
                                recordCount = await ReadRows(null);
                            } while (recordCount > 0);
                            continue;

                        case SqlNotificationType.Change when depResult.Source is SqlNotificationSource.Timeout:
                            // Expected while there is no activity. We put a timeout on our SqlDependency so they're not running infinitely.
                            _logger.LogTrace("{0}SQL notification timed out", _tracePrefix);
                            break;

                        case SqlNotificationType.Change:
                            throw new InvalidOperationException($"Unexpected SQL notification Type={depResult.Type}, Source={depResult.Source}, Info={depResult.Info}");

                        case SqlNotificationType.Subscribe:
                            _logger.LogError("{0}SQL notification subscription error: Type={1}, Source={2}, Info={3}", _tracePrefix, depResult.Type, depResult.Source, depResult.Info);

                            if (depResult.Info == SqlNotificationInfo.TemplateLimit)
                            {
                                // We've hit a subscription limit, pause for a bit then start again
                                await Task.Delay(2000, cancellationToken);
                            }
                            else
                            {
                                // Unknown subscription error, let's stop using query notifications
                                _notificationsDisabled = true;
                                await StartLoop();
                                return;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{0}Error in SQL notification loop", _tracePrefix);

                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
            }

            _logger.LogDebug("{0}SQL notification loop fell out", _tracePrefix);
            await StartLoop();
        }

        /// <summary>
        /// Loop until cancelled, using periodic queries to watch for new rows.
        /// </summary>
        private async Task PollingLoop(CancellationToken cancellationToken)
        {
            var delays = _updateLoopRetryDelays;
            for (var retrySetIndex = 0; retrySetIndex < delays.Length; retrySetIndex++)
            {
                Tuple<int, int> retry = delays[retrySetIndex];
                var retryDelay = retry.Item1;
                var numRetries = retry.Item2;

                for (var retryIndex = 0; retryIndex < numRetries; retryIndex++)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    if (retryDelay > 0)
                    {
                        _logger.LogTrace("{0}Waiting {1}ms before checking for messages again", _tracePrefix, retryDelay);

                        await Task.Delay(retryDelay, cancellationToken);
                    }

                    var recordCount = 0;
                    try
                    {
                        recordCount = await ReadRows(null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{0}Error in SQL polling loop", _tracePrefix);
                    }

                    if (recordCount > 0)
                    {
                        _logger.LogDebug("{0}{1} records received", _tracePrefix, recordCount);

                        // We got records so start the retry loop again
                        // at the lowest delay.
                        retrySetIndex = -1;
                        break;
                    }

                    _logger.LogTrace("{0}No records received", _tracePrefix);

                    var isLastRetry = retrySetIndex == delays.Length - 1 && retryIndex == numRetries - 1;

                    if (isLastRetry)
                    {
                        // Last retry loop so just stay looping on the last retry delay
                        retryIndex--;
                    }
                }
            }

            _logger.LogDebug("{0}SQL polling loop fell out", _tracePrefix);
            await StartLoop();
        }

        /// <summary>
        /// Fetch the starting payloadID that will be used to query for newer messages.
        /// </summary>
        private async Task<long> GetLastPayloadId()
        {
            try
            {
                using var connection = new SqlConnection(_options.ConnectionString);
                using var command = new SqlCommand
                {
                    Connection = connection,
                    CommandText = _maxIdSql,
                };
                await connection.OpenAsync();

                var id = await command.ExecuteScalarAsync();
                if (id is null) throw new Exception($"Unable to retrieve the starting payload ID for {_tableName}");
                return (long)id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{0}SqlReceiver error starting", _tracePrefix);
                throw;
            }
        }

        /// <summary>
        /// Execute a query against the database to look for rows newer than <see cref="_lastPayloadId"/>
        /// </summary>
        /// <param name="beforeExecute"></param>
        /// <returns></returns>
        private async Task<int> ReadRows(Action<SqlCommand>? beforeExecute)
        {
            using var connection = new SqlConnection(_options.ConnectionString);
            using var command = new SqlCommand
            {
                Connection = connection,
                CommandText = _selectSql,
            };
            command.Parameters.Add(new SqlParameter("PayloadId", _lastPayloadId));
            await connection.OpenAsync();

            beforeExecute?.Invoke(command);

            // Fetch any rows that might already be available after the last PayloadId.
            var reader = await command.ExecuteReaderAsync();
            var recordCount = 0;

            while (reader.Read())
            {
                recordCount++;
                await ProcessRecord(reader);
            }

            return recordCount;
        }

        /// <summary>
        /// Process a message row received from the database.
        /// </summary>
        /// <param name="record"></param>
        private async Task ProcessRecord(IDataRecord record)
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

            _logger.LogTrace("{0}Updated receive reader initial payload ID parameter={1}", _tracePrefix, _lastPayloadId);

            _logger.LogTrace("{0}Payload {1} received", _tracePrefix, id);

            await _onReceived!.Invoke(id, (byte[]?)payload ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Attempt to start SQL Dependency listening for notification-based polling,
        /// returning a boolean indicating success. If false, SQL notifications cannot be used.
        /// </summary>
        /// <returns></returns>
        private bool StartSqlDependencyListener()
        {
            if (_notificationsDisabled)
            {
                return false;
            }

            _logger.LogTrace("{0}Starting SQL notification listener", _tracePrefix);
            try
            {
                if (SqlDependency.Start(_options.ConnectionString))
                {
                    _logger.LogTrace("{0}SQL notification listener started", _tracePrefix);
                }
                else
                {
                    _logger.LogTrace("{0}SQL notification listener was already running", _tracePrefix);
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("{0}SQL Service Broker is disabled. Falling back on periodic polling.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (NullReferenceException)
            {
                // Workaround for https://github.com/dotnet/SqlClient/issues/1264

                _logger.LogWarning("{0}SQL Service Broker is disabled. Falling back on periodic polling.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (SqlException ex) when (ex.Number == 40510 || ex.Message.Contains("not supported"))
            {
                // Workaround for https://github.com/dotnet/SqlClient/issues/1264.
                // Specifically that Azure SQL Database reports that service broker is enabled,
                // even though it is entirely unsupported.

                _logger.LogInformation("{0}SQL Service Broker is unsupported by the target database. Falling back on periodic polling.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{0}Error starting SQL notification listener", _tracePrefix);

                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
