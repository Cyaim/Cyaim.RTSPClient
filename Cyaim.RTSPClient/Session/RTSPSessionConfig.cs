using System;

namespace Cyaim.RTSPClient.Session
{
    /// <summary>
    /// RTSP会话配置
    /// </summary>
    public class RTSPSessionConfig
    {
        /// <summary>
        /// RTSP服务器地址，例如 rtsp://192.168.1.127:554
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// 认证用户名
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// 认证密码
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// 传输模式，默认为TCP交织（RTP over TCP）
        /// </summary>
        public TransportMode TransportMode { get; set; } = TransportMode.TcpInterleaved;

        /// <summary>
        /// RTSP响应超时时间，默认10秒
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// TCP连接超时时间，默认5秒
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 是否启用自动重连，默认启用
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// 最大重连尝试次数，默认3次
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 3;

        /// <summary>
        /// 重连等待延迟时间，默认2秒
        /// </summary>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Keep-Alive保活方法，默认使用OPTIONS
        /// </summary>
        public string KeepAliveMethod { get; set; } = "OPTIONS";

        /// <summary>
        /// 用户代理标识
        /// </summary>
        public string UserAgent { get; set; } = "Cyaim RTSP Client 2.0";

        /// <summary>
        /// 是否启用ONVIF反向通道（语音对讲）
        /// </summary>
        public bool UseBackchannel { get; set; }

        /// <summary>
        /// ONVIF反向通道Require头的值
        /// </summary>
        public string BackchannelRequire { get; set; } = "www.onvif.org/ver20/backchannel";

        /// <summary>
        /// RTP通道缓冲区大小（字节），默认1024
        /// </summary>
        public int RtpChannelBufferSize { get; set; } = 1024;
    }
}
