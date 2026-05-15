using System;

namespace Cyaim.RTSPClient.Exceptions
{
    /// <summary>
    /// RTP包解析异常
    /// </summary>
    public class RTPParseException : RTSPException
    {
        public RTPParseException(string message) : base(message) { }
        public RTPParseException(string message, Exception innerException) : base(message, innerException) { }
    }
}
