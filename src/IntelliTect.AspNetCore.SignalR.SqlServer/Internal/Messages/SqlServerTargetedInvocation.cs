using IntelliTect.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelliTect.SignalR.SqlServer.Internal.Messages
{

    internal readonly struct SqlServerTargetedInvocation
    {
        public string Target { get; }

        public SqlServerInvocation Invocation { get; }

        public SqlServerTargetedInvocation(string target, SqlServerInvocation invocation)
        {
            Target = target;
            Invocation = invocation;
        }
    }
}
