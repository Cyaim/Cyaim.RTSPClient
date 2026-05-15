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
    /// 音频编码类型 (RFC 3551 + 常见动态载荷)
    /// </summary>
    public enum AudioCodec
    {
        Unknown,

        // ===== RFC 3551 静态载荷类型 =====
        
        /// <summary>
        /// G.711 μ-law (PCMU), 8kHz, 64kbps
        /// </summary>
        PCMU = 0,
        
        /// <summary>
        /// GSM 06.10, 8kHz, 13kbps
        /// </summary>
        GSM = 3,
        
        /// <summary>
        /// G.723.1, 8kHz, 5.3/6.3kbps
        /// </summary>
        G723 = 4,
        
        /// <summary>
        /// DVI4, 8kHz, 32kbps
        /// </summary>
        DVI4_8K = 5,
        
        /// <summary>
        /// DVI4, 16kHz, 64kbps
        /// </summary>
        DVI4_16K = 6,
        
        /// <summary>
        /// LPC (Linear Predictive Coding), 8kHz
        /// </summary>
        LPC = 7,
        
        /// <summary>
        /// G.711 A-law (PCMA), 8kHz, 64kbps
        /// </summary>
        PCMA = 8,
        
        /// <summary>
        /// G.722, 16kHz, 48/56/64kbps
        /// </summary>
        G722 = 9,
        
        /// <summary>
        /// L16 立体声, 44.1kHz
        /// </summary>
        L16_STEREO = 10,
        
        /// <summary>
        /// L16 单声道, 44.1kHz
        /// </summary>
        L16_MONO = 11,
        
        /// <summary>
        /// QCELP, 8kHz
        /// </summary>
        QCELP = 12,
        
        /// <summary>
        /// Comfort Noise, 8kHz
        /// </summary>
        CN = 13,
        
        /// <summary>
        /// MPEG-1/2 Audio (MPA), 90kHz
        /// </summary>
        MPA = 14,
        
        /// <summary>
        /// G.728, 8kHz, 16kbps (LD-CELP)
        /// </summary>
        G728 = 15,
        
        /// <summary>
        /// DVI4, 11.025kHz
        /// </summary>
        DVI4_11025 = 16,
        
        /// <summary>
        /// DVI4, 22.05kHz
        /// </summary>
        DVI4_22050 = 17,
        
        /// <summary>
        /// G.729, 8kHz, 8kbps (CS-ACELP)
        /// </summary>
        G729 = 18,

        // ===== 常见动态载荷类型 (96-127) =====
        
        /// <summary>
        /// G.726, 8kHz, 16/24/32/40kbps (ADPCM)
        /// </summary>
        G726 = 96,
        
        /// <summary>
        /// AAC-LD (Low Delay), MPEG-4
        /// </summary>
        AAC_LD = 97,
        
        /// <summary>
        /// AAC-ELD (Enhanced Low Delay)
        /// </summary>
        AAC_ELD = 98,
        
        /// <summary>
        /// AAC (Generic), MPEG-4
        /// </summary>
        AAC = 99,
        
        /// <summary>
        /// AMR (Adaptive Multi-Rate), 8kHz
        /// </summary>
        AMR = 100,
        
        /// <summary>
        /// AMR-WB (Wideband), 16kHz
        /// </summary>
        AMR_WB = 101,
        
        /// <summary>
        /// Opus, 48kHz (RFC 7587)
        /// </summary>
        OPUS = 102,
        
        /// <summary>
        /// Vorbis (RFC 5215)
        /// </summary>
        VORBIS = 103,
        
        /// <summary>
        /// Speex, 8/16/32kHz (RFC 5574)
        /// </summary>
        SPEEX = 104,
        
        /// <summary>
        /// G.729.1, 8-32kHz (scalable)
        /// </summary>
        G7291 = 105,
        
        /// <summary>
        /// AC-3 (Dolby Digital)
        /// </summary>
        AC3 = 106,
        
        /// <summary>
        /// E-AC-3 (Dolby Digital Plus)
        /// </summary>
        EAC3 = 107,
        
        /// <summary>
        /// DTS (Digital Theater Systems)
        /// </summary>
        DTS = 108,
        
        /// <summary>
        /// MPEG-4 Generic
        /// </summary>
        MPEG4_GENERIC = 109,
        
        /// <summary>
        /// FLAC (Free Lossless Audio Codec)
        /// </summary>
        FLAC = 110
    }
}
