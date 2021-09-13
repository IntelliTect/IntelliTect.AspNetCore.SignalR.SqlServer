﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;

namespace Microsoft.AspNet.SignalR.SqlServer
{
    /// <summary>
    /// A DbOperation that continues to execute over and over as new results arrive.
    /// Will attempt to use SQL Query Notifications, otherwise falls back to a polling receive loop.
    /// </summary>
    internal class ObservableDbOperation : DbOperation, IDisposable, IDbBehavior
    {
        private readonly Tuple<int, int>[] _updateLoopRetryDelays = new [] {
            Tuple.Create(0, 3),    // 0ms x 3
            Tuple.Create(10, 3),   // 10ms x 3
            Tuple.Create(50, 2),   // 50ms x 2
            Tuple.Create(100, 2),  // 100ms x 2
            Tuple.Create(200, 2),  // 200ms x 2
            Tuple.Create(1000, 2), // 1000ms x 2
            Tuple.Create(1500, 2), // 1500ms x 2
            Tuple.Create(3000, 1)  // 3000ms x 1
        };
        private readonly object _stopLocker = new object();
        private readonly ManualResetEventSlim _stopHandle = new ManualResetEventSlim(true);
        private readonly IDbBehavior _dbBehavior;

        private string? _dependencyQueue = null;// Guid.NewGuid().ToString();

        private volatile bool _disposing;
        private long _notificationState;

        public ObservableDbOperation(string connectionString, string commandText, ILogger logger, params IDataParameter[] parameters)
            : base(connectionString, commandText, logger, parameters)
        {
            _dbBehavior = this;

            Faulted += _ => { };
            Queried += () => { };
            Changed += () => { };
        }

        /// <summary>
        /// For use from tests only.
        /// </summary>
        internal long CurrentNotificationState
        {
            get { return _notificationState; }
            set { _notificationState = value; }
        }

        public event Action Queried;

        public event Action Changed;

        public event Action<Exception> Faulted;

        /// <summary>
        /// Note this blocks the calling thread until a SQL Query Notification can be set up
        /// </summary>
        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "Needs refactoring"),
         SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Errors are reported via the callback")]
        public void ExecuteReaderWithUpdates(Action<IDataRecord, DbOperation> processRecord)
        {
            lock (_stopLocker)
            {
                if (_disposing)
                {
                    return;
                }
                _stopHandle.Reset();
            }

            var useNotifications = _dbBehavior.StartSqlDependencyListener();

            var delays = _dbBehavior.UpdateLoopRetryDelays;

            for (var i = 0; i < delays.Count; i++)
            {
                if (i == 0 && useNotifications)
                {
                    // Reset the state to ProcessingUpdates if this is the start of the loop.
                    // This should be safe to do here without Interlocked because the state is protected
                    // in the other two cases using Interlocked, i.e. there should only be one instance of
                    // this running at any point in time.
                    _notificationState = NotificationState.ProcessingUpdates;
                }

                Tuple<int, int> retry = delays[i];
                var retryDelay = retry.Item1;
                var retryCount = retry.Item2;

                for (var j = 0; j < retryCount; j++)
                {
                    if (_disposing)
                    {
                        Stop(null);
                        return;
                    }

                    if (retryDelay > 0)
                    {
                        _logger.LogTrace("{0}Waiting {1}ms before checking for messages again", TracePrefix, retryDelay);

                        Thread.Sleep(retryDelay);
                    }

                    var recordCount = 0;
                    try
                    {
                        recordCount = ExecuteReader(processRecord);

                        Queried();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{0}Error in SQL receive loop", TracePrefix);

                        Faulted(ex);
                    }

                    if (recordCount > 0)
                    {
                        _logger.LogDebug("{0}{1} records received", TracePrefix, recordCount);

                        // We got records so start the retry loop again
                        i = -1;
                        break;
                    }

                    _logger.LogTrace("{0}No records received", TracePrefix);

                    var isLastRetry = i == delays.Count - 1 && j == retryCount - 1;

                    if (isLastRetry)
                    {
                        // Last retry loop iteration
                        if (!useNotifications)
                        {
                            // Last retry loop and we're not using notifications so just stay looping on the last retry delay
                            j = j - 1;
                        }
                        else
                        {
                            // No records after all retries, set up a SQL notification
                            try
                            {
                                _logger.LogDebug("{0}Setting up SQL notification", TracePrefix);

                                recordCount = ExecuteReader(processRecord, command =>
                                {
                                    _dbBehavior.AddSqlDependency(command, e => SqlDependency_OnChange(e, processRecord));
                                });

                                Queried();

                                if (recordCount > 0)
                                {
                                    _logger.LogDebug("{0}Records were returned by the command that sets up the SQL notification, restarting the receive loop", TracePrefix);

                                    i = -1;
                                    break; // break the inner for loop
                                }
                                else
                                {
                                    var previousState = Interlocked.CompareExchange(ref _notificationState, NotificationState.AwaitingNotification,
                                        NotificationState.ProcessingUpdates);

                                    if (previousState == NotificationState.AwaitingNotification)
                                    {
                                        _logger.LogError("{0}A SQL notification was already running. Overlapping receive loops detected, this should never happen. BUG!", TracePrefix);

                                        return;
                                    }

                                    if (previousState == NotificationState.NotificationReceived)
                                    {
                                        // Failed to change _notificationState from ProcessingUpdates to AwaitingNotification, it was already NotificationReceived

                                        _logger.LogDebug("{0}The SQL notification fired before the receive loop returned, restarting the receive loop", TracePrefix);

                                        i = -1;
                                        break; // break the inner for loop
                                    }
                                    
                                }

                                _logger.LogTrace("{0}No records received while setting up SQL notification", TracePrefix);

                                // We're in a wait state for a notification now so check if we're disposing
                                lock (_stopLocker)
                                {
                                    if (_disposing)
                                    {
                                        _stopHandle.Set();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "{0}Error in SQL receive loop", TracePrefix);
                                Faulted(ex);

                                // Re-enter the loop on the last retry delay
                                j = j - 1;

                                if (retryDelay > 0)
                                {
                                    _logger.LogTrace("{0}Waiting {1}ms before checking for messages again", TracePrefix, retryDelay);

                                    Thread.Sleep(retryDelay);
                                }
                            }
                        }
                    }
                }
            }

            _logger.LogTrace("{0}Receive loop exiting", TracePrefix);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Disposing")]
        public void Dispose()
        {
            lock (_stopLocker)
            {
                _disposing = true;
            }

            if (_notificationState != NotificationState.Disabled)
            {
                try
                {
                    SqlDependency.Stop(ConnectionString, _dependencyQueue);
                }
                catch (Exception) { }
            }

            if (Interlocked.Read(ref _notificationState) == NotificationState.ProcessingUpdates)
            {
                _stopHandle.Wait();
            }
            _stopHandle.Dispose();
        }

        protected virtual void AddSqlDependency(IDbCommand command, Action<SqlNotificationEventArgs> callback)
        {
            command.AddSqlDependency(e => callback(e));
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "On a background thread and we report exceptions asynchronously"),
         SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "sender", Justification = "Event handler")]
        protected virtual void SqlDependency_OnChange(SqlNotificationEventArgs e, Action<IDataRecord, DbOperation> processRecord)
        {
            _logger.LogDebug("{0}SQL notification change fired", TracePrefix);

            lock (_stopLocker)
            {
                if (_disposing)
                {
                    return;
                }
            }

            var previousState = Interlocked.CompareExchange(ref _notificationState,
                NotificationState.NotificationReceived, NotificationState.ProcessingUpdates);

            if (previousState == NotificationState.NotificationReceived)
            {
                _logger.LogError("{0}Overlapping SQL change notifications received, this should never happen, BUG!", TracePrefix);
                
                return;
            }
            if (previousState == NotificationState.ProcessingUpdates)
            {
                // We're still in the original receive loop

                // New updates will be retreived by the original reader thread
                _logger.LogTrace("{0}Original reader processing is still in progress and will pick up the changes", TracePrefix);

                return;
            }

            // _notificationState wasn't ProcessingUpdates (likely AwaitingNotification)

            // Check notification args for issues
            if (e.Type == SqlNotificationType.Change)
            {
                if (e.Info == SqlNotificationInfo.Update)
                {
                    _logger.LogTrace("{0}SQL notification details: Type={1}, Source={2}, Info={3}", TracePrefix, e.Type, e.Source, e.Info);
                }
                else if (e.Source == SqlNotificationSource.Timeout)
                {
                    _logger.LogTrace("{0}SQL notification timed out", TracePrefix);
                }
                else
                {
                    _logger.LogError("{0}Unexpected SQL notification details: Type={1}, Source={2}, Info={3}", TracePrefix, e.Type, e.Source, e.Info);

                    Faulted(new SqlMessageBusException($"Unexpected SQL notification Type={e.Type}, Source={e.Source}, Info={e.Info}"));
                }
            }
            else if (e.Type == SqlNotificationType.Subscribe)
            {
                Debug.Assert(e.Info != SqlNotificationInfo.Invalid, "Ensure the SQL query meets the requirements for query notifications at http://msdn.microsoft.com/en-US/library/ms181122.aspx");

                _logger.LogError("{0}SQL notification subscription error: Type={1}, Source={2}, Info={3}", TracePrefix, e.Type, e.Source, e.Info);

                if (e.Info == SqlNotificationInfo.TemplateLimit)
                {
                    // We've hit a subscription limit, pause for a bit then start again
                    Thread.Sleep(2000);
                }
                else
                {
                    // Unknown subscription error, let's stop using query notifications
                    _notificationState = NotificationState.Disabled;
                    try
                    {
                        SqlDependency.Stop(ConnectionString, _dependencyQueue);
                    }
                    catch (Exception) { }
                }
            }

            Changed();
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "I need to")]
        protected virtual bool StartSqlDependencyListener()
        {
            lock (_stopLocker)
            {
                if (_disposing)
                {
                    return false;
                }
            }

            if (_notificationState == NotificationState.Disabled)
            {
                return false;
            }

            _logger.LogTrace("{0}Starting SQL notification listener", TracePrefix);
            try
            {
                if (SqlDependency.Start(ConnectionString, _dependencyQueue))
                {
                    _logger.LogTrace("{0}SQL notification listener started", TracePrefix);
                }
                else
                {
                    _logger.LogTrace("{0}SQL notification listener was already running", TracePrefix);
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("{0}SQL Service Broker is disabled. Falling back on periodic polling.", TracePrefix);
                _notificationState = NotificationState.Disabled;
                return false;
            }
            catch (NullReferenceException)
            {
                // Workaround for https://github.com/dotnet/SqlClient/issues/1264

                _logger.LogWarning("{0}SQL Service Broker is disabled. Falling back on periodic polling.", TracePrefix);
                _notificationState = NotificationState.Disabled;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{0}Error starting SQL notification listener", TracePrefix);

                return false;
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Stopping is a terminal state on a bg thread")]
        protected virtual void Stop(Exception? ex)
        {
            if (ex != null)
            {
                Faulted(ex);
            }

            if (_notificationState != NotificationState.Disabled)
            {
                try
                {
                    _logger.LogTrace("{0}Stopping SQL notification listener", TracePrefix);
                    SqlDependency.Stop(ConnectionString, _dependencyQueue);
                    _logger.LogTrace("{0}SQL notification listener stopped", TracePrefix);
                }
                catch (Exception stopEx)
                {
                    _logger.LogError(stopEx, "{0}Error occurred while stopping SQL notification listener: {1}", TracePrefix);
                }
            }

            lock (_stopLocker)
            {
                if (_disposing)
                {
                    _stopHandle.Set();
                }
            }
        }

        internal static class NotificationState
        {
            public const long Enabled = 0;
            public const long ProcessingUpdates = 1;
            public const long AwaitingNotification = 2;
            public const long NotificationReceived = 3;
            public const long Disabled = 4;
        }

        bool IDbBehavior.StartSqlDependencyListener()
        {
            return StartSqlDependencyListener();
        }

        IList<Tuple<int, int>> IDbBehavior.UpdateLoopRetryDelays
        {
            get { return _updateLoopRetryDelays; }
        }

        void IDbBehavior.AddSqlDependency(IDbCommand command, Action<SqlNotificationEventArgs> callback)
        {
            AddSqlDependency(command, callback);
        }
    }
}
