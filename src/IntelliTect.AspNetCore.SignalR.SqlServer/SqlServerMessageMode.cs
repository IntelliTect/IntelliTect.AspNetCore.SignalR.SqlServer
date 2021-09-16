// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer
{
    /// <summary>
    /// Specifies the allowed modes of acquiring scaleout messages from SQL Server.
    /// </summary>
    [Flags]
    public enum SqlServerMessageMode
    {
        /// <summary>
        /// Use SQL Server Service Broker for dicovering when new messages are available.
        /// </summary>
        ServiceBroker = 1 << 0,

        /// <summary>
        /// Use perioid polling to discover when new messages are available.
        /// </summary>
        Polling = 1 << 1,

        /// <summary>
        /// Use the most suitable mode for acquring messages.
        /// </summary>
        Auto = ServiceBroker | Polling,
    }
}
