namespace Cyaim.RTSPServer.Config;

/// <summary>
/// RTSP 服务器配置
/// </summary>
public class RtspServerOptions
{
    /// <summary>
    /// 监听端口
    /// </summary>
    public int Port { get; set; } = 554;

    /// <summary>
    /// 监听地址
    /// </summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>
    /// 最大并发连接数
    /// </summary>
    public int MaxConnections { get; set; } = 10000;

    /// <summary>
    /// 会话超时（秒）
    /// </summary>
    public int SessionTimeout { get; set; } = 60;

    /// <summary>
    /// RTP 端口范围起始
    /// </summary>
    public int RtpPortRangeStart { get; set; } = 10000;

    /// <summary>
    /// RTP 端口范围结束
    /// </summary>
    public int RtpPortRangeEnd { get; set; } = 60000;

    /// <summary>
    /// 启用认证
    /// </summary>
    public bool EnableAuthentication { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// 密码
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 流配置列表
    /// </summary>
    public List<StreamConfig> Streams { get; set; } = [];
}

/// <summary>
/// 流配置
/// </summary>
public class StreamConfig
{
    /// <summary>
    /// 流路径（如 /live/camera1）
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// 流名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 流描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 媒体源类型
    /// </summary>
    public MediaSourceType SourceType { get; set; }

    /// <summary>
    /// 源地址（文件路径、RTSP URL、摄像头ID等）
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 视频编码
    /// </summary>
    public VideoCodecType VideoCodec { get; set; } = VideoCodecType.H264;

    /// <summary>
    /// 音频编码
    /// </summary>
    public AudioCodecType AudioCodec { get; set; } = AudioCodecType.PCMA;

    /// <summary>
    /// 视频宽度
    /// </summary>
    public int Width { get; set; } = 1920;

    /// <summary>
    /// 视频高度
    /// </summary>
    public int Height { get; set; } = 1080;

    /// <summary>
    /// 帧率
    /// </summary>
    public int Framerate { get; set; } = 25;

    /// <summary>
    /// 启用音频
    /// </summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>
    /// 循环播放（文件源）
    /// </summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// 启用录制
    /// </summary>
    public bool EnableRecording { get; set; }

    /// <summary>
    /// 录制路径
    /// </summary>
    public string? RecordingPath { get; set; }
}

/// <summary>
/// 媒体源类型
/// </summary>
public enum MediaSourceType
{
    /// <summary>
    /// 文件
    /// </summary>
    File,

    /// <summary>
    /// RTSP 拉流
    /// </summary>
    RtspPull,

    /// <summary>
    /// RTMP 推流
    /// </summary>
    RtmpPush,

    /// <summary>
    /// 摄像头
    /// </summary>
    Camera,

    /// <summary>
    /// 屏幕捕获
    /// </summary>
    Screen,

    /// <summary>
    /// 测试图案
    /// </summary>
    TestPattern
}

/// <summary>
/// 视频编码类型
/// </summary>
public enum VideoCodecType
{
    H264,
    H265,
    MJPEG,
    VP8,
    VP9
}

/// <summary>
/// 音频编码类型
/// </summary>
public enum AudioCodecType
{
    None,
    PCMA,
    PCMU,
    AAC,
    OPUS,
    G722
}
