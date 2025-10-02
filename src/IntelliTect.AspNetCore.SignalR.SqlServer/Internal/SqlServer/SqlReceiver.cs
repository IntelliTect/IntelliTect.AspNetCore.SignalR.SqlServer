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
        private readonly TimeSpan _activityMaxDuration = TimeSpan.FromMinutes(10);
            

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

            return Task.Factory
                .StartNew(StartLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach)
                .Unwrap();
        }

        private async Task StartLoop()
        {
            if (_cts.IsCancellationRequested) return;

            bool useBroker = _options.Mode.HasFlag(SqlServerMessageMode.ServiceBroker);

            using (var activity = SqlServerOptions.ActivitySource.StartActivity("SignalR.SqlServer.Start"))
            {
                activity?.SetTag("signalr.hub", _tracePrefix);
                activity?.SetTag("signalr.sql.mode", _options.Mode.ToString());

                if (!_lastPayloadId.HasValue)
                {
                    _lastPayloadId = await GetLastPayloadId();
                }
                if (useBroker)
                {
                    useBroker = StartSqlDependencyListener();
                }
            }

            if (_cts.IsCancellationRequested) return;

            if (useBroker)
            {
                await NotificationLoop(_cts.Token);
            }
            else if (_options.Mode.HasFlag(SqlServerMessageMode.Polling))
            {
                await PollingLoop(_cts.Token);
            }
            else
            {
                throw new InvalidOperationException("None of the configured SqlServerMessageMode are suitable for use.");
            }
        }

        /// <summary>
        /// Loop until cancelled, using SQL service broker notifications to watch for new rows.
        /// </summary>
        private async Task NotificationLoop(CancellationToken cancellationToken)
        {
            while (!_notificationsDisabled)
            {
                if (cancellationToken.IsCancellationRequested) return;

                using var activity = SqlServerOptions.ActivitySource.StartActivity("SignalR.SqlServer.Listen");
                activity?.SetTag("signalr.hub", _tracePrefix);

                try
                {
                    _logger.LogDebug("{HubStream}: Setting up SQL notification", _tracePrefix);

                    var tcs = new TaskCompletionSource<SqlNotificationEventArgs>();
                    var cancelReg = cancellationToken.Register((t) => ((TaskCompletionSource<SqlNotificationEventArgs>)t!).TrySetCanceled(), tcs);
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

                        _logger.LogDebug("{HubStream}: Records were returned by the command that sets up the SQL notification, restarting the receive loop", _tracePrefix);
                        continue;
                    }

                    _logger.LogTrace("{HubStream}: No records received while setting up SQL notification", _tracePrefix);

                    var depResult = await tcs.Task;
                    cancelReg.Dispose();
                    if (cancellationToken.IsCancellationRequested) return;

                    // Check notification args for issues
                    switch (depResult.Type)
                    {
                        case SqlNotificationType.Change when depResult.Info is SqlNotificationInfo.Update:
                            // Typically means new records are available. (TODO: verify this?).
                            // Loop again to pick them up by performing another query.
                            _logger.LogTrace("{HubStream}: SQL notification details: Type={1}, Source={2}, Info={3}", _tracePrefix, depResult.Type, depResult.Source, depResult.Info);

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
                            _logger.LogTrace("{HubStream}: SQL notification timed out (this is expected every {1} seconds)", _tracePrefix, _dependencyTimeout.TotalSeconds);
                            break;

                        case SqlNotificationType.Change:
                            throw new InvalidOperationException($"Unexpected SQL notification Type={depResult.Type}, Source={depResult.Source}, Info={depResult.Info}");

                        case SqlNotificationType.Subscribe:
                            _logger.LogError("{HubStream}: SQL notification subscription error: Type={1}, Source={2}, Info={3}", _tracePrefix, depResult.Type, depResult.Source, depResult.Info);

                            if (depResult.Info == SqlNotificationInfo.TemplateLimit)
                            {
                                // We've hit a subscription limit, pause for a bit then start again
                                await Task.Delay(2000, cancellationToken);
                            }
                            else
                            {
                                // Unknown subscription error, let's stop using query notifications
                                activity?.SetStatus(ActivityStatusCode.Error);
                                // Dispose so this activity doesn't become the parent of the next loop.
                                activity?.Dispose();
                                _notificationsDisabled = true;
                                await StartLoop();
                                return;
                            }
                            break;
                    }
                }
                catch (TaskCanceledException) { return; }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    _logger.LogError(ex, "{HubStream}: Error in SQL notification loop", _tracePrefix);

                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
            }

            _logger.LogDebug("{HubStream}: SQL notification loop fell out", _tracePrefix);
            await StartLoop();
        }

        /// <summary>
        /// Loop until cancelled, using periodic queries to watch for new rows.
        /// </summary>
        private async Task PollingLoop(CancellationToken cancellationToken)
        {
            var activity = SqlServerOptions.ActivitySource.StartActivity("SignalR.SqlServer.Poll");
            activity?.SetTag("signalr.hub", _tracePrefix);

            try
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

                        // Restart activity every 10 minutes to prevent long-running activities
                        if (activity != null && DateTime.UtcNow - activity.StartTimeUtc > _activityMaxDuration)
                        {
                            activity?.Dispose();
                            activity = SqlServerOptions.ActivitySource.StartActivity("SignalR.SqlServer.Poll");
                            activity?.SetTag("signalr.hub", _tracePrefix);
                        }

                        var recordCount = 0;
                        try
                        {
                            if (retryDelay > 0)
                            {
                                _logger.LogTrace("{HubStream}: Waiting {Delay}ms before checking for messages again", _tracePrefix, retryDelay);

                                await Task.Delay(retryDelay, cancellationToken);
                            }

                            recordCount = await ReadRows(null);
                        }
                        catch (TaskCanceledException) { return; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "{HubStream}: Error in SQL polling loop", _tracePrefix);
                        }

                        if (recordCount > 0)
                        {
                            _logger.LogDebug("{HubStream}: {RecordCount} records received", _tracePrefix, recordCount);

                            // We got records so start the retry loop again
                            // at the lowest delay.
                            retrySetIndex = -1;
                            break;
                        }

                        _logger.LogTrace("{HubStream}: No records received", _tracePrefix);

                        var isLastRetry = retrySetIndex == delays.Length - 1 && retryIndex == numRetries - 1;

                        if (isLastRetry)
                        {
                            // Last retry loop so just stay looping on the last retry delay
                            retryIndex--;
                        }
                    }
                }
            }
            finally
            {
                activity?.Dispose();
            }

            _logger.LogDebug("{HubStream}: SQL polling loop fell out", _tracePrefix);
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
                _logger.LogError(ex, "{HubStream}: SqlReceiver error starting", _tracePrefix);
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

            _logger.LogTrace("{HubStream}: SqlReceiver last payload ID={1}, new payload ID={2}", _tracePrefix, _lastPayloadId, id);

            if (id > _lastPayloadId + 1)
            {
                _logger.LogError("{HubStream}: Missed message(s) from SQL Server. Expected payload ID {1} but got {2}.", _tracePrefix, _lastPayloadId + 1, id);
            }
            else if (id <= _lastPayloadId)
            {
                _logger.LogInformation("{HubStream}: Duplicate message(s) or payload ID reset from SQL Server. Last payload ID {1}, this payload ID {2}", _tracePrefix, _lastPayloadId, id);
            }

            _lastPayloadId = id;

            _logger.LogTrace("{HubStream}: Updated receive reader initial payload ID parameter={1}", _tracePrefix, _lastPayloadId);

            _logger.LogTrace("{HubStream}: Payload {1} received", _tracePrefix, id);

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

            _logger.LogTrace("{HubStream}: Starting SQL notification listener", _tracePrefix);
            try
            {
                if (SqlDependency.Start(_options.ConnectionString))
                {
                    _logger.LogTrace("{HubStream}: SQL notification listener started", _tracePrefix);
                }
                else
                {
                    _logger.LogTrace("{HubStream}: SQL notification listener was already running", _tracePrefix);
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("{HubStream}: SQL Service Broker is disabled on the target database.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (NullReferenceException)
            {
                // Workaround for https://github.com/dotnet/SqlClient/issues/1264

                _logger.LogWarning("{HubStream}: SQL Service Broker is disabled or unsupported by the target database.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (SqlException ex) when (ex.Number == 40510 || ex.Message.Contains("not supported"))
            {
                // Workaround for https://github.com/dotnet/SqlClient/issues/1264.
                // Specifically that Azure SQL Database reports that service broker is enabled,
                // even though it is entirely unsupported.

                _logger.LogWarning("{HubStream}: SQL Service Broker is unsupported by the target database.", _tracePrefix);
                _notificationsDisabled = true;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{HubStream}: Error starting SQL notification listener", _tracePrefix);

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
