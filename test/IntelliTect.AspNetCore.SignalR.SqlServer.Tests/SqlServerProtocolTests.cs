using IntelliTect.AspNetCore.SignalR.SqlServer.Internal;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Tests
{
    public class SqlServerProtocolTests
    {
        [Fact]
        public void InvocationAllRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteInvocationAll("Method", new object[] { "s" }, new[] { "excluded" });
            var decoded = sut.ReadInvocationAll(encoded);

            var decodedMessage = ParseMessage(decoded.Message);

            Assert.Equal(MessageType.InvocationAll, sut.ReadMessageType(encoded));
            Assert.Equal(new[] { "excluded" }, decoded.ExcludedConnectionIds);
            Assert.Equal("Method", decodedMessage.Target);
            Assert.Equal("s", decodedMessage.Arguments.Single());
        }

        [Theory]
        [InlineData(MessageType.InvocationConnection)]
        [InlineData(MessageType.InvocationGroup)]
        [InlineData(MessageType.InvocationUser)]
        internal void TargetedInvocationRoundTrips(MessageType type)
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteTargetedInvocation(type, "target", "Method", new object[] { "s" }, new[] { "excluded" });
            var decoded = sut.ReadTargetedInvocation(encoded);

            var decodedMessage = ParseMessage(decoded.Invocation.Message);

            Assert.Equal(type, sut.ReadMessageType(encoded));
            Assert.Equal(new[] { "excluded" }, decoded.Invocation.ExcludedConnectionIds);
            Assert.Equal("target", decoded.Target);
            Assert.Equal("Method", decodedMessage.Target);
            Assert.Equal("s", decodedMessage.Arguments.Single());
        }

        [Fact]
        internal void AckRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var encoded = sut.WriteAck(42, "server1");
            var decoded = sut.ReadAck(encoded);

            Assert.Equal(MessageType.Ack, sut.ReadMessageType(encoded));
            Assert.Equal(42, decoded.Id);
            Assert.Equal("server1", decoded.ServerName);
        }

        [Fact]
        internal void GroupRoundTrips()
        {
            var sut = new SqlServerProtocol(CreateHubMessageSerializer());
            var command = new SqlServerGroupCommand(42, "server1", GroupAction.Add, "groupName", "connection");
            var encoded = sut.WriteGroupCommand(command);
            var decoded = sut.ReadGroupCommand(encoded);

            Assert.Equal(MessageType.Group, sut.ReadMessageType(encoded));
            Assert.Equal(command, decoded);
        }

        private DefaultHubMessageSerializer CreateHubMessageSerializer()
        {
            var resolver = new HubProtocolResolver();
            return new DefaultHubMessageSerializer(
                resolver,
                resolver.AllProtocols.Select(p => p.Name).ToList(), hubSupportedProtocols: null);
        }


        private InvocationMessage ParseMessage(SerializedHubMessage serialized)
        {
            var protocol = new MessagePackHubProtocol();
            var decodedMessage = new ReadOnlySequence<byte>(serialized.GetSerializedMessage(protocol));
            Assert.True(protocol.TryParseMessage(ref decodedMessage, new DefaultInvocationBinder(), out var message));
            return Assert.IsType<InvocationMessage>(message);
        }

        private class HubProtocolResolver : IHubProtocolResolver
        {
            public IReadOnlyList<IHubProtocol> AllProtocols => new List<IHubProtocol>() { new MessagePackHubProtocol() };

            public IHubProtocol GetProtocol(string protocolName, IReadOnlyList<string> supportedProtocols)
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
