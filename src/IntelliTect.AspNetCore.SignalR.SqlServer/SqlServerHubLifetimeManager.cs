// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Globalization;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages;
using Microsoft.AspNetCore.SignalR.Protocol;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;

[assembly: InternalsVisibleTo("IntelliTect.AspNetCore.SignalR.SqlServer.Tests")]

namespace IntelliTect.AspNetCore.SignalR.SqlServer
{
    /// <summary>
    /// The SQL Server scaleout provider for multi-server support.
    /// </summary>
    /// <typeparam name="THub">The type of <see cref="Hub"/> to manage connections for.</typeparam>
    public class SqlServerHubLifetimeManager<THub> : HubLifetimeManager<THub>, IDisposable where THub : Hub
    {
        private readonly HubConnectionStore _connections = new HubConnectionStore();
        private readonly SqlServerSubscriptionManager _groups = new SqlServerSubscriptionManager();
        private readonly SqlServerSubscriptionManager _users = new SqlServerSubscriptionManager();
        private readonly ILogger _logger;
        private readonly SqlServerOptions _options;
        private readonly string _serverName = GenerateServerName();
        private readonly SqlServerProtocol _protocol;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1);
        private readonly List<SqlStream> _streams = new List<SqlStream>();

        private readonly string _tableNamePrefix;

        private readonly AckHandler _ackHandler;
        private int _internalId;
        private bool _disposed;

        /// <summary>
        /// Constructs the <see cref="SqlServerHubLifetimeManager{THub}"/> with types from Dependency Injection.
        /// </summary>
        /// <param name="logger">The logger to write information about what the class is doing.</param>
        /// <param name="options">The <see cref="SqlServerOptions"/> that influence behavior of the SQL Server connection.</param>
        /// <param name="hubProtocolResolver">The <see cref="IHubProtocolResolver"/> to get an <see cref="IHubProtocol"/> instance when writing to connections.</param>
        /// <param name="globalHubOptions">The global <see cref="HubOptions"/>.</param>
        /// <param name="hubOptions">The <typeparamref name="THub"/> specific options.</param>
        /// <param name="lifetime">The host lifetime instance</param>
        public SqlServerHubLifetimeManager(
            ILogger<SqlServerHubLifetimeManager<THub>> logger,
            IOptions<SqlServerOptions> options,
            IHubProtocolResolver hubProtocolResolver,
            IOptions<HubOptions>? globalHubOptions,
            IOptions<HubOptions<THub>>? hubOptions,
            IHostApplicationLifetime lifetime
        )
        {
            _logger = logger;
            _options = options.Value;
            _ackHandler = new AckHandler();

            _tableNamePrefix = "Messages_" + _options.TableSlugGenerator(typeof(THub));

            if (globalHubOptions != null && hubOptions != null)
            {
                _protocol = new SqlServerProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, globalHubOptions.Value.SupportedProtocols, hubOptions.Value.SupportedProtocols));
            }
            else
            {
                var supportedProtocols = hubProtocolResolver.AllProtocols.Select(p => p.Name).ToList();
                _protocol = new SqlServerProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, supportedProtocols, null));
            }

            // Delay until startup so the application can have a chance to create the database
            // if the application does that in Program.cs before app.Run().
            lifetime.ApplicationStarted.Register(() => _ = EnsureSqlServerConnection());
        }

        /// <inheritdoc />
        public override async Task OnConnectedAsync(HubConnectionContext connection)
        {
            await EnsureSqlServerConnection();
            var feature = new SqlServerFeature();
            connection.Features.Set<ISqlServerFeature>(feature);

            var userTask = Task.CompletedTask;

            _connections.Add(connection);

            if (!string.IsNullOrEmpty(connection.UserIdentifier))
            {
                userTask = _users.AddSubscriptionAsync(connection.UserIdentifier, connection);
            }

            await userTask;
        }

        /// <inheritdoc />
        public override Task OnDisconnectedAsync(HubConnectionContext connection)
        {
            _connections.Remove(connection);

            var tasks = new List<Task>();

            var feature = connection.Features.Get<ISqlServerFeature>()!;
            if (feature is null) return Task.CompletedTask;

            var groupNames = feature.Groups;

            if (groupNames != null)
            {
                // Copy the groups to an array here because they get removed from this collection
                // in RemoveFromGroupAsync
                foreach (var group in groupNames.ToArray())
                {
                    // Use RemoveGroupAsyncCore because the connection is local and we don't want to
                    // accidentally go to other servers with our remove request.
                    tasks.Add(RemoveGroupAsyncCore(connection, group));
                }
            }

            if (!string.IsNullOrEmpty(connection.UserIdentifier))
            {
                tasks.Add(_users.RemoveSubscriptionAsync(connection.UserIdentifier!, connection));
            }

            return Task.WhenAll(tasks);
        }

        /// <inheritdoc />
        public override Task SendAllAsync(string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            var message = _protocol.WriteInvocationAll(methodName, args, null);
            return PublishAsync(MessageType.InvocationAll, message);
        }

        /// <inheritdoc />
        public override Task SendAllExceptAsync(string methodName, object?[]? args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        {
            var message = _protocol.WriteInvocationAll(methodName, args, excludedConnectionIds);
            return PublishAsync(MessageType.InvocationAll, message);
        }

        /// <inheritdoc />
        public override Task SendConnectionAsync(string connectionId, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            if (connectionId == null)
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            // If the connection is local we can skip sending the message through the bus since we require sticky connections.
            // This also saves serializing and deserializing the message!
            var connection = _connections[connectionId];
            if (connection != null)
            {
                return connection.WriteAsync(new InvocationMessage(methodName, args ?? Array.Empty<object[]>()), cancellationToken).AsTask();
            }

            var message = _protocol.WriteTargetedInvocation(MessageType.InvocationConnection, connectionId, methodName, args, null);
            return PublishAsync(MessageType.InvocationConnection, message);
        }

        /// <inheritdoc />
        public override Task SendGroupAsync(string groupName, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            if (groupName == null)
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            var message = _protocol.WriteTargetedInvocation(MessageType.InvocationGroup, groupName, methodName, args, null);
            return PublishAsync(MessageType.InvocationGroup, message);
        }

        /// <inheritdoc />
        public override Task SendGroupExceptAsync(string groupName, string methodName, object?[]? args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        {
            if (groupName == null)
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            var message = _protocol.WriteTargetedInvocation(MessageType.InvocationGroup, groupName, methodName, args, excludedConnectionIds);
            return PublishAsync(MessageType.InvocationGroup, message);
        }

        /// <inheritdoc />
        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (connectionId == null)
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            if (groupName == null)
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            var connection = _connections[connectionId];
            if (connection != null)
            {
                // short circuit if connection is on this server
                return AddGroupAsyncCore(connection, groupName);
            }

            return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Add);
        }

        /// <inheritdoc />
        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (connectionId == null)
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            if (groupName == null)
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            var connection = _connections[connectionId];
            if (connection != null)
            {
                // short circuit if connection is on this server
                return RemoveGroupAsyncCore(connection, groupName);
            }

            return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Remove);
        }

        /// <inheritdoc />
        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            if (connectionIds == null)
            {
                throw new ArgumentNullException(nameof(connectionIds));
            }

            var publishTasks = new List<Task>(connectionIds.Count);
            foreach (var connectionId in connectionIds)
            {
                publishTasks.Add(SendConnectionAsync(connectionId, methodName, args, cancellationToken));
            }

            return Task.WhenAll(publishTasks);
        }

        /// <inheritdoc />
        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            if (groupNames == null)
            {
                throw new ArgumentNullException(nameof(groupNames));
            }
            var publishTasks = new List<Task>(groupNames.Count);

            foreach (var groupName in groupNames)
            {
                if (!string.IsNullOrEmpty(groupName))
                {
                    publishTasks.Add(SendGroupAsync(groupName, methodName, args, cancellationToken));
                }
            }

            return Task.WhenAll(publishTasks);
        }

        /// <inheritdoc />
        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            if (userIds.Count == 0)
            {
                return Task.CompletedTask;
            }

            var publishTasks = new List<Task>(userIds.Count);
            foreach (var userId in userIds)
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    publishTasks.Add(SendUserAsync(userId, methodName, args, cancellationToken));
                }
            }

            return Task.WhenAll(publishTasks);
        }

        /// <inheritdoc />
        public override Task SendUserAsync(string userId, string methodName, object?[]? args, CancellationToken cancellationToken = default)
        {
            var message = _protocol.WriteTargetedInvocation(MessageType.InvocationUser, userId, methodName, args, null);
            return PublishAsync(MessageType.InvocationUser, message);
        }

        private async Task PublishAsync(MessageType type, byte[] payload)
        {
            using var activity = SqlServerOptions.ActivitySource.StartActivity("SignalR.SqlServer.Publish");
            if (activity != null)
            {
                activity.SetTag("signalr.hub", typeof(THub).FullName!);
                activity.SetTag("signalr.sql.message_type", type.ToString());
                activity.SetTag("signalr.sql.message_size_bytes", payload.Length);
            }

            await EnsureSqlServerConnection();
            _logger.Published(type.ToString());

            var streamIndex = new Random().Next(0, _streams.Count);
            await _streams[streamIndex].Send(payload);
        }

        private Task AddGroupAsyncCore(HubConnectionContext connection, string groupName)
        {
            var feature = connection.Features.Get<ISqlServerFeature>()!;
            var groupNames = feature.Groups;

            lock (groupNames)
            {
                // Connection already in group
                if (!groupNames.Add(groupName))
                {
                    return Task.CompletedTask;
                }
            }

            return _groups.AddSubscriptionAsync(groupName, connection);
        }

        /// <summary>
        /// This takes <see cref="HubConnectionContext"/> because we want to remove the connection from the
        /// _connections list in OnDisconnectedAsync and still be able to remove groups with this method.
        /// </summary>
        private async Task RemoveGroupAsyncCore(HubConnectionContext connection, string groupName)
        {
            await _groups.RemoveSubscriptionAsync(groupName, connection);

            var feature = connection.Features.Get<ISqlServerFeature>()!;
            var groupNames = feature.Groups;
            if (groupNames != null)
            {
                lock (groupNames)
                {
                    groupNames.Remove(groupName);
                }
            }
        }

        private async Task SendGroupActionAndWaitForAck(string connectionId, string groupName, GroupAction action)
        {
            var id = Interlocked.Increment(ref _internalId);
            var ack = _ackHandler.CreateAck(id);
            // Send Add/Remove Group to other servers and wait for an ack or timeout
            var message = _protocol.WriteGroupCommand(new SqlServerGroupCommand(id, _serverName, action, groupName, connectionId));
            await PublishAsync(MessageType.Group, message);

            await ack;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _disposed = true;
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
            _ackHandler.Dispose();
        }

        private async Task EnsureSqlServerConnection()
        {
            if (_streams.Count == 0)
            {
                await _connectionLock.WaitAsync();
                try
                {
                    if (_streams.Count == 0)
                    {
                        var installer = new SqlInstaller(_options, _logger, _tableNamePrefix, typeof(THub).FullName!);
                        await installer.Install();

                        for (var i = 0; i < _options.TableCount; i++)
                        {
                            var streamIndex = i;
                            var tableName = string.Format(CultureInfo.InvariantCulture, "{0}_{1}", _tableNamePrefix, streamIndex);
                            var tracePrefix = $"{typeof(THub).FullName}:{streamIndex}";

                            var stream = new SqlStream(
                                _options,
                                _logger,
                                streamIndex,
                                tableName,
                                tracePrefix
                            );

                            _streams.Add(stream);

                            StartReceiving();

                            void StartReceiving()
                            {
                                stream.StartReceiving((id, message) => OnReceived(message))
                                    .ContinueWith(async t =>
                                    {
                                        if (_disposed) return;

                                        _logger.LogError(t.Exception, "{0}The SQL Server SignalR message receiver encountered an error.", tracePrefix);

                                        // Try again in a little bit
                                        await Task.Delay(2000);
                                        StartReceiving();
                                    }, TaskContinuationOptions.OnlyOnFaulted);
                            }

                        }
                    }
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
        }

        private async Task OnReceived(byte[] message)
        {
            var messageType = _protocol.ReadMessageType(message);
            List<Task> tasks;
            HubConnectionStore? connections;
            SqlServerInvocation invocation;

            _logger.Received(messageType.ToString());

            switch (messageType)
            {
                case MessageType.InvocationAll:

                    invocation = _protocol.ReadInvocationAll(message);
                    connections = _connections;
                    goto multiInvocation;

                case MessageType.InvocationConnection:
                    var connectionInvocation = _protocol.ReadTargetedInvocation(message);
                    var userConnection = _connections[connectionInvocation.Target];
                    if (userConnection != null) await userConnection.WriteAsync(connectionInvocation.Invocation.Message);
                    return;

                case MessageType.InvocationUser:
                    var multiInvocation = _protocol.ReadTargetedInvocation(message);
                    connections = _users.Get(multiInvocation.Target);
                    invocation = multiInvocation.Invocation;
                    goto multiInvocation;

                case MessageType.InvocationGroup:
                    multiInvocation = _protocol.ReadTargetedInvocation(message);
                    connections = _groups.Get(multiInvocation.Target);
                    invocation = multiInvocation.Invocation;
                    goto multiInvocation;

                multiInvocation:
                    if (connections == null) return;

                    tasks = new List<Task>(connections.Count);
                    foreach (var connection in connections)
                    {
                        if (invocation.ExcludedConnectionIds?.Contains(connection.ConnectionId) == true)
                        {
                            continue;
                        }
                        tasks.Add(connection.WriteAsync(invocation.Message).AsTask());
                    }
                    await Task.WhenAll(tasks);
                    return;

                case MessageType.Ack:
                    var ack = _protocol.ReadAck(message);
                    if (ack.ServerName != _serverName) return;
                    _ackHandler.TriggerAck(ack.Id);
                    return;

                case MessageType.Group:
                    var groupMessage = _protocol.ReadGroupCommand(message);

                    userConnection = _connections[groupMessage.ConnectionId];
                    if (userConnection == null)
                    {
                        // user not on this server
                        return;
                    }

                    if (groupMessage.Action == GroupAction.Remove)
                    {
                        await RemoveGroupAsyncCore(userConnection, groupMessage.GroupName);
                    }

                    if (groupMessage.Action == GroupAction.Add)
                    {
                        await AddGroupAsyncCore(userConnection, groupMessage.GroupName);
                    }

                    // Send an ack to the server that sent the original command.
                    await PublishAsync(MessageType.Ack, _protocol.WriteAck(groupMessage.Id, groupMessage.ServerName));
                    return;
            }

            _logger.LogWarning($"Unknown message type {messageType}");
        }

        private static string GenerateServerName()
        {
            // Use the machine name for convenient diagnostics, but add a guid to make it unique.
            // Example: MyServerName_02db60e5fab243b890a847fa5c4dcb29
            return $"{Environment.MachineName}_{Guid.NewGuid():N}";
        }

        private interface ISqlServerFeature
        {
            HashSet<string> Groups { get; }
        }

        private class SqlServerFeature : ISqlServerFeature
        {
            public HashSet<string> Groups { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
