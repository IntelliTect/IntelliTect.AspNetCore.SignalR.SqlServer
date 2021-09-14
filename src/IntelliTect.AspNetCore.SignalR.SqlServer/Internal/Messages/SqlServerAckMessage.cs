using IntelliTect.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages
{
    internal readonly struct SqlServerAckMessage
    {
        public int Id { get; }

        public string ServerName { get; }

        public SqlServerAckMessage(int id, string serverName)
        {
            Id = id;
            ServerName = serverName;
        }
    }
}
