namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSP连接状态
    /// </summary>
    public enum RTSPConnectionState
    {
        /// <summary>
        /// 未连接
        /// </summary>
        Disconnected,

        /// <summary>
        /// 正在连接
        /// </summary>
        Connecting,

        /// <summary>
        /// 已连接（TCP已建立）
        /// </summary>
        Connected,

        /// <summary>
        /// 就绪（已完成OPTIONS/DESCRIBE）
        /// </summary>
        Ready,

        /// <summary>
        /// 已设置（SETUP完成）
        /// </summary>
        Setup,

        /// <summary>
        /// 播放中
        /// </summary>
        Playing,

        /// <summary>
        /// 已暂停
        /// </summary>
        Paused,

        /// <summary>
        /// 断开中
        /// </summary>
        Disconnecting
    }

    /// <summary>
    /// 传输模式
    /// </summary>
    public enum TransportMode
    {
        /// <summary>
        /// RTP over TCP (interleaved)
        /// </summary>
        TcpInterleaved,

        /// <summary>
        /// RTP over UDP
        /// </summary>
        UdpUnicast,

        /// <summary>
        /// RTP over UDP Multicast
        /// </summary>
        UdpMulticast
    }

    /// <summary>
    /// RTP流类型
    /// </summary>
    public enum StreamType
    {
        /// <summary>
        /// 视频流
        /// </summary>
        Video,

        /// <summary>
        /// 音频流
        /// </summary>
        Audio,

        /// <summary>
        /// 应用数据
        /// </summary>
        Application,

        /// <summary>
        /// 文本
        /// </summary>
        Text
    }

    /// <summary>
    /// 视频编码类型
    /// </summary>
    public enum VideoCodec
    {
        Unknown,
        H264,
        H265,
        MPEG4,
        MJPEG,
        VP8,
        VP9
    }

    /// <summary>
    /// 音频编码类型
    /// </summary>
    public enum AudioCodec
    {
        Unknown,
        PCMA,    // G.711A
        PCMU,    // G.711U
        G726,
        AAC,
        OPUS,
        MPEG4_GENERIC
    }
}
