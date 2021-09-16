using IntelliTect.AspNetCore.SignalR.SqlServer.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Tests
{
    public class SqlServerEndToEndTests
    {
        private const string databaseName = "SignalRUnitTestsDb";
        private const string connectionString = 
            "Server=lsocalhost;Database=" + databaseName + ";Trusted_Connection=True;";

        [SkippableFact]
        public async Task CanSendAndReceivePayloads_WithServiceBroker()
        {
            await CreateDatabaseAsync();

            var options = new SqlServerOptions
            {
                ConnectionString = connectionString,
                AutoEnableServiceBroker = true,
                Mode = SqlServerMessageMode.ServiceBroker
            };

            var prefix = nameof(CanSendAndReceivePayloads_WithServiceBroker);
            await RunCore(options, prefix);
        }

        [SkippableFact]
        public async Task CanSendAndReceivePayloads_WithPolling()
        {
            await CreateDatabaseAsync();

            var options = new SqlServerOptions
            {
                ConnectionString = connectionString,
                Mode = SqlServerMessageMode.Polling
            };

            var prefix = nameof(CanSendAndReceivePayloads_WithPolling);
            await RunCore(options, prefix);
        }

        private static async Task RunCore(SqlServerOptions options, string prefix)
        {
            var installer = new SqlInstaller(options, NullLogger.Instance, prefix);
            var sender = new SqlSender(options, NullLogger.Instance, prefix + "_0");
            var receiver = new SqlReceiver(options, NullLogger.Instance, prefix + "_0", "");

            var receivedMessages = new ConcurrentBag<byte[]>();
            var receivedEvent = new SemaphoreSlim(0);
            await installer.Install();
            var receiverTask = receiver.Start((_, message) =>
            {
                receivedMessages.Add(message);
                receivedEvent.Release();
                return Task.CompletedTask;
            });
            // Give the receiver time to reach a steady state (waiting).
            await Task.Delay(150);

            var payload = new byte[255];
            new Random().NextBytes(payload);
            await sender.Send(payload);

            await receivedEvent.WaitAsync();
            var message = Assert.Single(receivedMessages);
            Assert.Equal(payload, message);

            // Give the receiver time to reach a steady state (waiting).
            await Task.Delay(50);

            receiver.Dispose();
            await receiverTask;
        }

        private static async Task CreateDatabaseAsync()
        {
            try
            {
                using var connection = new SqlConnection(connectionString.Replace(databaseName, "master"));
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = $@"IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = '{databaseName}')
                    BEGIN CREATE DATABASE {databaseName}; END";
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number == 53 || ex.Message.Contains("Could not open a connection to SQL Server"))
            {
                Skip.If(true, "SQL Server not available on localhost");
            }
        }
    }
}
