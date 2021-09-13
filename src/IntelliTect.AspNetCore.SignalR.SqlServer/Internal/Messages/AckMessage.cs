using IntelliTect.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelliTect.SignalR.SqlServer.Internal.Messages
{
    internal readonly struct AckMessage
    {
        public int Id { get; }

        public string ServerName { get; }

        public AckMessage(int id, string serverName)
        {
            Id = id;
            ServerName = serverName;
        }
    }
}
