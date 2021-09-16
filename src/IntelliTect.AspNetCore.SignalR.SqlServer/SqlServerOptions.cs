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
    /// Options used to configure <see cref="SqlServerHubLifetimeManager{THub}"/>.
    /// </summary>
    public class SqlServerOptions
    {
        /// <summary>
        /// The SQL Server connection string to use.
        /// </summary>
        [Required]
        public string ConnectionString { get; set; } = "";

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

        /// <summary>
        /// Function that determines the part of the SQL Server table name that identifies the Hub.
        /// It should be assumed that 15 characters of SQL Server's 128 character max are not available for use.
        /// By default, uses the Hub's unqualified type name.
        /// </summary>
        public Func<Type, string> TableSlugGenerator { get; set; } = type => type.Name;

        /// <summary>
        /// If true, on startup the application will attempt to automatically enable SQL Server Service Broker.
        /// Service Broker allows for more performant operation. It can be manually enabled on the server with
        /// "ALTER DATABASE [DatabaseName] SET ENABLE_BROKER". It requires an exclusive lock on the database.
        /// </summary>
        public bool AutoEnableServiceBroker { get; set; } = false;

        public SqlServerMessageMode Mode { get; set; } = SqlServerMessageMode.Auto;
    }
}
