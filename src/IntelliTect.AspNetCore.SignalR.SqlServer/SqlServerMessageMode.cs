// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer
{

    public enum SqlServerMessageMode
    {
        ServiceBroker = 1 << 0,
        Polling = 1 << 1,

        Auto = ServiceBroker | Polling,
    }
}
