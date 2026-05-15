using System;
using Cyaim.RTSPClient.Rtp;

namespace Cyaim.RTSPClient.Events
{
    /// <summary>
    /// 连接状态变更事件参数
    /// </summary>
    public class RTSPConnectionStateChangedEventArgs : EventArgs
    {
        public RTSPConnectionStateChangedEventArgs(RTSPConnectionState oldState, RTSPConnectionState newState, string? reason = null)
        {
            OldState = oldState;
            NewState = newState;
            Reason = reason;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// 之前的状态
        /// </summary>
        public RTSPConnectionState OldState { get; }

        /// <summary>
        /// 新状态
        /// </summary>
        public RTSPConnectionState NewState { get; }

        /// <summary>
        /// 状态变更原因
        /// </summary>
        public string? Reason { get; }

        /// <summary>
        /// 发生时间
        /// </summary>
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// RTP数据接收事件参数
    /// </summary>
    public class RtpDataReceivedEventArgs : EventArgs
    {
        public RtpDataReceivedEventArgs(RTPPacket packet)
        {
            Packet = packet;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// 接收到的RTP包
        /// </summary>
        public RTPPacket Packet { get; }

        /// <summary>
        /// 接收时间
        /// </summary>
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// RTSP响应事件参数
    /// </summary>
    public class RTSPResponseEventArgs : EventArgs
    {
        public RTSPResponseEventArgs(RTSPResponse response)
        {
            Response = response;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// RTSP响应
        /// </summary>
        public RTSPResponse Response { get; }

        /// <summary>
        /// 接收时间
        /// </summary>
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// 错误事件参数
    /// </summary>
    public class RTSPErrorEventArgs : EventArgs
    {
        public RTSPErrorEventArgs(Exception exception, string? message = null)
        {
            Exception = exception;
            Message = message ?? exception.Message;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// 异常对象
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? Message { get; }

        /// <summary>
        /// 发生时间
        /// </summary>
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// Keep-Alive事件参数
    /// </summary>
    public class KeepAliveEventArgs : EventArgs
    {
        public KeepAliveEventArgs(bool success, int roundTripMs)
        {
            Success = success;
            RoundTripMs = roundTripMs;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// 往返时间(毫秒)
        /// </summary>
        public int RoundTripMs { get; }

        /// <summary>
        /// 发生时间
        /// </summary>
        public DateTime Timestamp { get; }
    }
}
