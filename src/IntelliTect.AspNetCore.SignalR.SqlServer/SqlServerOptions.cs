// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer
{
    /// <summary>
    /// Options used to configure <see cref="SqlServerHubLifetimeManager{THub}"/>.
    /// </summary>
    public class SqlServerOptions
    {
        /// <summary>
        /// The SQL Server connection string to use.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// The number of tables to store messages in. Using more tables reduces lock contention and may increase throughput.
        /// This must be consistent between all nodes in the web farm.
        /// Defaults to 1.
        /// </summary>
        public int TableCount { get; set; } = 1;

        /// <summary>
        /// The name of the database schema to use for the underlying SQL Server Tables.
        /// </summary>
        public string SchemaName { get; set; } = "SignalR";
    }
}
