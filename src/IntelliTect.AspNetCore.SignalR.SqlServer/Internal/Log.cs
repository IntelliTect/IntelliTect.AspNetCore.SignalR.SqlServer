// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace IntelliTect.SignalR.SqlServer.Internal
{
    internal static class Log
    {
        //private static readonly LogDefineOptions SkipEnabledCheckLogOptions = new() { SkipEnabledCheck = true };

        private static readonly Action<ILogger, string, Exception?> _received =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(4, "ReceivedFromSqlServer"), "Received {Type} message from SQL Server.");

        private static readonly Action<ILogger, string, Exception?> _publish =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(5, "PublishToSqlServer"), "Publishing {Type} message to SQL Server.");

        private static readonly Action<ILogger, Exception> _internalMessageFailed =
            LoggerMessage.Define(LogLevel.Warning, new EventId(11, "InternalMessageFailed"), "Error processing message for internal server message.");


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
    }
}
