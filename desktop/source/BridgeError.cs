using System;

namespace KasaDesktop
{
    internal sealed class BridgeError : Exception
    {
        internal int Status { get; }
        internal string Code { get; }
        internal string RequestId { get; }
        internal int? Row { get; }
        internal BridgeError(int status, string code, string requestId = null, int? row = null)
        {
            Status = status;
            Code = code;
            RequestId = requestId;
            Row = row;
        }
    }
}
