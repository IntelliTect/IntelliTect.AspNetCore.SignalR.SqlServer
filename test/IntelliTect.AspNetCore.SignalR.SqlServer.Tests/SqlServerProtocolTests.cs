using IntelliTect.AspNetCore.SignalR.SqlServer.Internal;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using TUnit.Assertions.Enums;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Tests
{
    public class SqlServerProtocolTests
    {
        [Test]
        public async Task InvocationAllRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteInvocationAll("Method", new object[] { "s" }, new[] { "excluded" });
            var decoded = sut.ReadInvocationAll(encoded);

            var decodedMessage = await ParseMessage(decoded.Message);

            await Assert.That(sut.ReadMessageType(encoded)).IsEqualTo(MessageType.InvocationAll);
            await Assert.That(decoded.ExcludedConnectionIds).IsEquivalentTo(new[] { "excluded" }, CollectionOrdering.Matching);
            await Assert.That(decodedMessage.Target).IsEqualTo("Method");
            await Assert.That(decodedMessage.Arguments.Single()).IsEqualTo("s");
        }

        [Test]
        [Arguments(MessageType.InvocationConnection)]
        [Arguments(MessageType.InvocationGroup)]
        [Arguments(MessageType.InvocationUser)]
        internal async Task TargetedInvocationRoundTrips(MessageType type)
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteTargetedInvocation(type, "target", "Method", new object[] { "s" }, new[] { "excluded" });
            var decoded = sut.ReadTargetedInvocation(encoded);

            var decodedMessage = await ParseMessage(decoded.Invocation.Message);

            await Assert.That(sut.ReadMessageType(encoded)).IsEqualTo(type);
            await Assert.That(decoded.Invocation.ExcludedConnectionIds).IsEquivalentTo(new[] { "excluded" }, CollectionOrdering.Matching);
            await Assert.That(decoded.Target).IsEqualTo("target");
            await Assert.That(decodedMessage.Target).IsEqualTo("Method");
            await Assert.That(decodedMessage.Arguments.Single()).IsEqualTo("s");
        }

        [Test]
        internal async Task AckRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteAck(42, "server1");
            var decoded = sut.ReadAck(encoded);

            await Assert.That(sut.ReadMessageType(encoded)).IsEqualTo(MessageType.Ack);
            await Assert.That(decoded.Id).IsEqualTo(42);
            await Assert.That(decoded.ServerName).IsEqualTo("server1");
        }

        [Test]
        internal async Task GroupRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var command = new SqlServerGroupCommand(42, "server1", GroupAction.Add, "groupName", "connection");
            var encoded = sut.WriteGroupCommand(command);
            var decoded = sut.ReadGroupCommand(encoded);

            await Assert.That(sut.ReadMessageType(encoded)).IsEqualTo(MessageType.Group);
            await Assert.That(decoded).IsEqualTo(command);
        }

        private DefaultHubMessageSerializer CreateHubMessageSerializer()
        {
            var resolver = new HubProtocolResolver();
            return new DefaultHubMessageSerializer(
                resolver,
                resolver.AllProtocols.Select(p => p.Name).ToList(), hubSupportedProtocols: null);
        }


        private async Task<InvocationMessage> ParseMessage(SerializedHubMessage serialized)
        {
            var protocol = new MessagePackHubProtocol();
            var decodedMessage = new ReadOnlySequence<byte>(serialized.GetSerializedMessage(protocol));
            await Assert.That(protocol.TryParseMessage(ref decodedMessage, new DefaultInvocationBinder(), out var message)).IsTrue();
            return (await Assert.That(message).IsTypeOf<InvocationMessage>())!;
        }

        private class HubProtocolResolver : IHubProtocolResolver
        {
            public IReadOnlyList<IHubProtocol> AllProtocols => new List<IHubProtocol>() { new MessagePackHubProtocol() };

            public IHubProtocol GetProtocol(string protocolName, IReadOnlyList<string>? supportedProtocols)
            {
                return AllProtocols[0];
            }
        }

        private class DefaultInvocationBinder : IInvocationBinder
        {
            public IReadOnlyList<Type> GetParameterTypes(string methodName)
            {
                // TODO: Possibly support actual client methods
                return new[] { typeof(object) };
            }

            public Type GetReturnType(string invocationId)
            {
                return typeof(object);
            }

            public Type GetStreamItemType(string streamId)
            {
                throw new NotImplementedException();
            }
        }
    }

}
