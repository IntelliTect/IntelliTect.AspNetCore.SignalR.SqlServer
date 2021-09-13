// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal static class Log
    {
        //private static readonly LogDefineOptions SkipEnabledCheckLogOptions = new() { SkipEnabledCheck = true };

        private static readonly Action<ILogger, string, Exception?> _received =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(1, "ReceivedFromSqlServer"), 
                "Received {Type} message from SQL Server.");

        private static readonly Action<ILogger, string, Exception?> _publish =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(2, "PublishToSqlServer"), 
                "Publishing {Type} message to SQL Server.");

        private static readonly Action<ILogger, Exception> _internalMessageFailed =
            LoggerMessage.Define(LogLevel.Warning, new EventId(3, "InternalMessageFailed"), 
                "Error processing message for internal server message.");

        private static readonly Action<ILogger, byte, string, string, Exception?> _unexpectedNotificationType =
            LoggerMessage.Define<byte, string, string>(LogLevel.Warning, new EventId(4, "UnexpectedNotificationType"), 
                "An unexpected SqlNotificationType was received. Details: Type={Type}, Source={Source}, Info={Info}.");


        public static void Received(this ILogger logger, string messageType)
        {
            _received(logger, messageType, null);
        }

        public static void Published(this ILogger logger, string messageType)
        {
            _publish(logger, messageType, null);
        }

        public static void InternalMessageFailed(this ILogger logger, Exception exception)
        {
            _internalMessageFailed(logger, exception);
        }

        public static void UnexpectedNotificationType(this ILogger logger, byte messageType, string source, string info)
        {
            _unexpectedNotificationType(logger, messageType, source, info, null);
        }
    }
}
