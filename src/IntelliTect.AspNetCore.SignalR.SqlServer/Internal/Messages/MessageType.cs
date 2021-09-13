using IntelliTect.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal.Messages
{
    // The size of the enum is defined by the protocol. Do not change it. If you need more than 255 items,
    // add an additional enum.
    internal enum MessageType : byte
    {
        // These numbers are used by the protocol, do not change them and always use explicit assignment
        // when adding new items to this enum. 0 is intentionally omitted
        Ack = 1,
        Group = 2,
        InvocationAll = 11,
        InvocationGroup = 12,
        InvocationConnection = 13,
        InvocationUser = 14,
    }
}
