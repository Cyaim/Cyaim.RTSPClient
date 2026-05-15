using System;

namespace Cyaim.RTSPClient.Exceptions
{
    /// <summary>
    /// RTSP协议异常基类
    /// </summary>
    public class RTSPException : Exception
    {
        public RTSPException(string message) : base(message) { }
        public RTSPException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 连接异常
    /// </summary>
    public class RTSPConnectionException : RTSPException
    {
        public RTSPConnectionException(string message) : base(message) { }
        public RTSPConnectionException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 认证异常
    /// </summary>
    public class RTSPAuthenticationException : RTSPException
    {
        public RTSPAuthenticationException(string message) : base(message) { }
        public RTSPAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 超时异常
    /// </summary>
    public class RTSPTimeoutException : RTSPException
    {
        public RTSPTimeoutException(string message) : base(message) { }
        public RTSPTimeoutException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 协议异常
    /// </summary>
    public class RTSPProtocolException : RTSPException
    {
        public RTSPProtocolException(string message) : base(message) { }
        public RTSPProtocolException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// 传输异常
    /// </summary>
    public class RTSPTransportException : RTSPException
    {
        public RTSPTransportException(string message) : base(message) { }
        public RTSPTransportException(string message, Exception innerException) : base(message, innerException) { }
    }
}
