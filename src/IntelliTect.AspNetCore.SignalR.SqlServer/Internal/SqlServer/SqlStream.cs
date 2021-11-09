// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages;
using IntelliTect.AspNetCore.SignalR.SqlServer;
using Microsoft.Extensions.Logging;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlStream : IDisposable
    {
        private readonly int _streamIndex;
        private readonly ILogger _logger;
        private readonly SqlSender _sender;
        private readonly SqlReceiver _receiver;
        private readonly string _tracePrefix;

        public SqlStream(SqlServerOptions options, ILogger logger, int streamIndex, string tableName, string tracePrefix)
        {
            _streamIndex = streamIndex;
            _logger = logger;
            _tracePrefix = tracePrefix;

            _sender = new SqlSender(options, logger, tableName);
            _receiver = new SqlReceiver(options, logger, tableName, _tracePrefix);
        }

        public Task StartReceiving(Func<long, byte[], Task> onReceived)
        {
            return _receiver.Start(onReceived);
        }

        public Task Send(byte[] message)
        {
            _logger.LogTrace("{0}Saving payload to SQL server", _tracePrefix, _streamIndex);

            return _sender.Send(message);
        }

        public void Dispose()
        {
            _logger.LogTrace("{0}Disposing stream {1}", _tracePrefix, _streamIndex);

            _receiver.Dispose();
        }
    }
}
