// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages;
using MessagePack;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlServerProtocol
    {
        private readonly DefaultHubMessageSerializer _messageSerializer;

        public SqlServerProtocol(DefaultHubMessageSerializer messageSerializer)
        {
            _messageSerializer = messageSerializer;
        }

        // The SQL Server Protocol:
        // * Mirrored after the Redis protocol.
        // * The message type is the first byte of the payload. (enum MessageType). 
        // * See the Write[type] methods for a description of the protocol for each in-depth.
        // * The "Variable length integer" is the length-prefixing format used by BinaryReader/BinaryWriter:
        //   * https://docs.microsoft.com/dotnet/api/system.io.binarywriter.write?view=netcore-2.2
        // * The "Length prefixed string" is the string format used by BinaryReader/BinaryWriter:
        //   * A 7-bit variable length integer encodes the length in bytes, followed by the encoded string in UTF-8.


        public MessageType ReadMessageType(ReadOnlyMemory<byte> data)
        {
            // See WriteInvocation for the format
            var reader = new MessagePackReader(data);
            return (MessageType)reader.ReadByte();
        }

        public byte[] WriteInvocationAll(string methodName, object?[]? args, IReadOnlyList<string>? excludedConnectionIds)
        {
            // Written as a MessagePack 'arr' containing at least these items:
            // * A MessagePack 'arr' of 'str's representing the excluded ids
            // * [The output of WriteSerializedHubMessage, which is an 'arr']
            // Any additional items are discarded.

            var memoryBufferWriter = MemoryBufferWriter.Get();
            try
            {
                var writer = new MessagePackWriter(memoryBufferWriter);
                writer.WriteUInt8((byte)MessageType.InvocationAll);
                writer.WriteArrayHeader(2);
                WriteInvocationCore(ref writer, methodName, args, excludedConnectionIds);
                writer.Flush();

                return memoryBufferWriter.ToArray();
            }
            finally
            {
                MemoryBufferWriter.Return(memoryBufferWriter);
            }
        }

        public byte[] WriteTargetedInvocation(MessageType type, string target, string methodName, object?[]? args, IReadOnlyList<string>? excludedConnectionIds)
        {
            // Written as a MessagePack 'arr' containing at least these items:
            // * A MessagePack 'arr' of 'str's representing the excluded ids
            // * [The output of WriteSerializedHubMessage, which is an 'arr']
            // Any additional items are discarded.

            var memoryBufferWriter = MemoryBufferWriter.Get();
            try
            {
                var writer = new MessagePackWriter(memoryBufferWriter);
                writer.WriteUInt8((byte)type);
                writer.WriteArrayHeader(3);
                writer.Write(target);
                WriteInvocationCore(ref writer, methodName, args, excludedConnectionIds);
                writer.Flush();

                return memoryBufferWriter.ToArray();
            }
            finally
            {
                MemoryBufferWriter.Return(memoryBufferWriter);
            }
        }

        private void WriteInvocationCore(ref MessagePackWriter writer, string methodName, object?[]? args, IReadOnlyList<string>? excludedConnectionIds)
        {
            // Written as a MessagePack 'arr' containing at least these items:
            // * A MessagePack 'arr' of 'str's representing the excluded ids
            // * [The output of WriteSerializedHubMessage, which is an 'arr']
            // Any additional items are discarded.

            if (excludedConnectionIds != null && excludedConnectionIds.Count > 0)
            {
                writer.WriteArrayHeader(excludedConnectionIds.Count);
                foreach (var id in excludedConnectionIds)
                {
                    writer.Write(id);
                }
            }
            else
            {
                writer.WriteArrayHeader(0);
            }

            WriteHubMessage(ref writer, new InvocationMessage(methodName, args));
        }

        public SqlServerInvocation ReadInvocationAll(ReadOnlyMemory<byte> data)
        {
            // See WriteInvocation for the format
            var reader = new MessagePackReader(data);
            reader.ReadByte(); // Skip header
            ValidateArraySize(ref reader, 2, "Invocation");

            return ReadInvocationCore(ref reader);
        }

        private SqlServerInvocation ReadInvocationCore(ref MessagePackReader reader)
        {
            // Read excluded Ids
            IReadOnlyList<string>? excludedConnectionIds = null;
            var idCount = reader.ReadArrayHeader();
            if (idCount > 0)
            {
                var ids = new string[idCount];
                for (var i = 0; i < idCount; i++)
                {
                    ids[i] = reader.ReadString();
                }

                excludedConnectionIds = ids;
            }

            // Read payload
            var message = ReadSerializedHubMessage(ref reader);
            return new SqlServerInvocation(message, excludedConnectionIds);
        }

        public SqlServerTargetedInvocation ReadTargetedInvocation(ReadOnlyMemory<byte> data)
        {
            // See WriteInvocation for the format
            var reader = new MessagePackReader(data);
            reader.ReadByte(); // Skip header
            ValidateArraySize(ref reader, 3, "TargetedInvocation");

            // Read target
            var target = reader.ReadString();

            var invocation = ReadInvocationCore(ref reader);

            return new SqlServerTargetedInvocation(target, invocation);
        }

        public byte[] WriteAck(int messageId, string serverName)
        {
            // Written as a MessagePack 'arr' containing at least these items:
            // * An 'int': The Id of the command being acknowledged.
            // Any additional items are discarded.

            var memoryBufferWriter = MemoryBufferWriter.Get();
            try
            {
                var writer = new MessagePackWriter(memoryBufferWriter);
                writer.WriteUInt8((byte)MessageType.Ack);
                writer.WriteArrayHeader(2);
                writer.Write(messageId);
                writer.Write(serverName);
                writer.Flush();

                return memoryBufferWriter.ToArray();
            }
            finally
            {
                MemoryBufferWriter.Return(memoryBufferWriter);
            }
        }

        public SqlServerAckMessage ReadAck(ReadOnlyMemory<byte> data)
        {
            var reader = new MessagePackReader(data);

            // See WriteAck for format
            reader.ReadByte(); // Skip header
            ValidateArraySize(ref reader, 2, "Ack");
            return new SqlServerAckMessage(reader.ReadInt32(), reader.ReadString());
        }


        public byte[] WriteGroupCommand(SqlServerGroupCommand command)
        {
            // Written as a MessagePack 'arr' containing at least these items:
            // * An 'int': the Id of the command
            // * A 'str': The server name
            // * An 'int': The action (likely less than 0x7F and thus a single-byte fixnum)
            // * A 'str': The group name
            // * A 'str': The connection Id
            // Any additional items are discarded.

            var memoryBufferWriter = MemoryBufferWriter.Get();
            try
            {
                var writer = new MessagePackWriter(memoryBufferWriter);
                writer.WriteUInt8((byte)MessageType.Group);

                writer.WriteArrayHeader(5);
                writer.Write(command.Id);
                writer.Write(command.ServerName);
                writer.Write((byte)command.Action);
                writer.Write(command.GroupName);
                writer.Write(command.ConnectionId);
                writer.Flush();

                return memoryBufferWriter.ToArray();
            }
            finally
            {
                MemoryBufferWriter.Return(memoryBufferWriter);
            }
        }

        public SqlServerGroupCommand ReadGroupCommand(ReadOnlyMemory<byte> data)
        {
            var reader = new MessagePackReader(data);

            reader.ReadByte(); // Skip header

            // See WriteGroupCommand for format.
            ValidateArraySize(ref reader, 5, "GroupCommand");

            var id = reader.ReadInt32();
            var serverName = reader.ReadString();
            var action = (GroupAction)reader.ReadByte();
            var groupName = reader.ReadString();
            var connectionId = reader.ReadString();

            return new SqlServerGroupCommand(id, serverName, action, groupName, connectionId);
        }


        private void WriteHubMessage(ref MessagePackWriter writer, HubMessage message)
        {
            // Written as a MessagePack 'map' where the keys are the name of the protocol (as a MessagePack 'str')
            // and the values are the serialized blob (as a MessagePack 'bin').

            var serializedHubMessages = _messageSerializer.SerializeMessage(message);

            writer.WriteMapHeader(serializedHubMessages.Count);

            foreach (var serializedMessage in serializedHubMessages)
            {
                writer.Write(serializedMessage.ProtocolName);

                var isArray = MemoryMarshal.TryGetArray(serializedMessage.Serialized, out var array);
                Debug.Assert(isArray);
                writer.Write(array);
            }
        }

        public static SerializedHubMessage ReadSerializedHubMessage(ref MessagePackReader reader)
        {
            var count = reader.ReadMapHeader();
            var serializations = new SerializedMessage[count];
            for (var i = 0; i < count; i++)
            {
                var protocol = reader.ReadString();
                var serialized = reader.ReadBytes()?.ToArray() ?? Array.Empty<byte>();

                serializations[i] = new SerializedMessage(protocol, serialized);
            }

            return new SerializedHubMessage(serializations);
        }

        private static void ValidateArraySize(ref MessagePackReader reader, int expectedLength, string messageType)
        {
            var length = reader.ReadArrayHeader();

            if (length < expectedLength)
            {
                throw new InvalidDataException($"Insufficient items in {messageType} array.");
            }
        }
    }
}
