using IntelliTect.AspNetCore.SignalR.SqlServer.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using TUnit.Assertions.Enums;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Tests
{
    public class SqlServerEndToEndTests
    {
        // Per-runtime database: each target framework's test run executes in parallel.
        private static readonly string databaseName = "SignalRUnitTestsDb_net" + Environment.Version.Major;
        private static readonly string connectionString =
            "Server=localhost;Database=" + databaseName + ";Trusted_Connection=True;Timeout=5;TrustServerCertificate=True";

        [Test]
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

        [Test]
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


        [Test]
        public async Task CanSendAndReceivePayloads_WithServiceBroker_UnderHeavyLoad()
        {
            await CreateDatabaseAsync();

            var options = new SqlServerOptions
            {
                ConnectionString = connectionString,
                AutoEnableServiceBroker = true,
                Mode = SqlServerMessageMode.ServiceBroker
            };

            var prefix = nameof(CanSendAndReceivePayloads_WithServiceBroker_UnderHeavyLoad);
            var installer = new SqlInstaller(options, NullLogger.Instance, prefix, prefix);
            var receiver = new SqlReceiver(options, NullLogger.Instance, prefix + "_0", "TestHub");

            var receivedMessages = new ConcurrentBag<byte[]>();
            await installer.Install();
            var receiverTask = receiver.Start((_, message) =>
            {
                receivedMessages.Add(message);
                return Task.CompletedTask;
            });
            // Give the receiver time to reach a steady state (waiting).
            await Task.Delay(150, TestContext.Current!.Execution.CancellationToken);

            var cts = new CancellationTokenSource();

            // This is roughly analagous to number of connections, not number of servers.
            // The reasoning is that each connected client to the hub could be triggering
            // the hub to be sending messages.
            int numSenders = 100;
            int numSent = 0;
            var sender = new SqlSender(options, NullLogger.Instance, prefix + "_0", "TestHub");
            for (int i = 0; i < numSenders; i++)
            {
                _ = Task.Run(async () =>
                  {
                      var random = new Random();
                      while (!cts.IsCancellationRequested)
                      {
                          var payload = new byte[255];
                          random.NextBytes(payload);
                          await sender.Send(payload);
                          Interlocked.Increment(ref numSent);
                      }
                  }, cts.Token);
            }

            var payload = new byte[255];
            new Random().NextBytes(payload);
            await Task.Delay(10000, TestContext.Current!.Execution.CancellationToken);
            cts.Cancel();

            // Give the receiver time to reach a steady state (waiting).
            await Task.Delay(1000, TestContext.Current!.Execution.CancellationToken);

            await Assert.That(receivedMessages.Count).IsEqualTo(numSent);

            receiver.Dispose();
            await receiverTask;
        }

        private async Task RunCore(SqlServerOptions options, string prefix)
        {
            var installer = new SqlInstaller(options, NullLogger.Instance, prefix, prefix);
            var sender = new SqlSender(options, NullLogger.Instance, prefix + "_0", "TestHub");
            var receiver = new SqlReceiver(options, NullLogger.Instance, prefix + "_0", "TestHub");

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
            await Task.Delay(150, TestContext.Current!.Execution.CancellationToken);

            var payload = new byte[255];
            new Random().NextBytes(payload);
            await sender.Send(payload);

            await receivedEvent.WaitAsync();
            var message = await Assert.That(receivedMessages).HasSingleItem();
            await Assert.That(message).IsEquivalentTo(payload, CollectionOrdering.Matching);

            // Give the receiver time to reach a steady state (waiting).
            await Task.Delay(50, TestContext.Current!.Execution.CancellationToken);

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
            catch (SqlException ex) when (
                ex.Number == 53 
                || ex.Message.Contains("Could not open a connection to SQL Server")
                || ex.Message.Contains("The server was not found or was not accessible")
            )
            {
                Skip.Test(ex.Message);
            }
        }
    }
}
