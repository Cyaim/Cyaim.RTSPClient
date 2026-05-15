using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Events;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Rtp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Session
{
    /// <summary>
    /// RTSP会话主接口
    /// </summary>
    public interface IRTSPSession : IDisposable
    {
        #region 属性

        /// <summary>
        /// 当前连接状态
        /// </summary>
        RTSPConnectionState State { get; }

        /// <summary>
        /// RTSP URI
        /// </summary>
        Uri Uri { get; }

        /// <summary>
        /// SDP会话描述
        /// </summary>
        SDP SDP { get; }

        /// <summary>
        /// RTSP会话ID
        /// </summary>
        string SessionId { get; }

        /// <summary>
        /// 服务器超时时间(秒)
        /// </summary>
        int ServerTimeout { get; }

        /// <summary>
        /// 是否支持ONVIF回传通道
        /// </summary>
        bool HasBackChannelSupported { get; }

        #endregion

        #region 事件

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        event EventHandler<RTSPConnectionStateChangedEventArgs> StateChanged;

        /// <summary>
        /// RTP数据接收事件
        /// </summary>
        event EventHandler<RtpDataReceivedEventArgs> DataReceived;

        /// <summary>
        /// 错误事件
        /// </summary>
        event EventHandler<RTSPErrorEventArgs> Error;

        /// <summary>
        /// Keep-Alive事件
        /// </summary>
        event EventHandler<KeepAliveEventArgs> KeepAlive;

        #endregion

        #region 生命周期方法

        /// <summary>
        /// 连接到RTSP服务器
        /// </summary>
        /// <param name="ct">取消令牌</param>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>
        /// 断开连接
        /// </summary>
        /// <param name="ct">取消令牌</param>
        Task DisconnectAsync(CancellationToken ct = default);

        #endregion

        #region RTSP信令方法

        /// <summary>
        /// 查询服务器支持的方法
        /// </summary>
        Task<RTSPResponse> OptionsAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取SDP描述
        /// </summary>
        Task<RTSPResponse> DescribeAsync(CancellationToken ct = default);

        /// <summary>
        /// 设置媒体传输通道
        /// </summary>
        /// <param name="trackUri">Track URI (如 "trackID=1")</param>
        /// <param name="mode">传输模式</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default);

        /// <summary>
        /// 开始播放
        /// </summary>
        /// <param name="range">播放范围 (如 "npt=0.000-")</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> PlayAsync(string? range = null, CancellationToken ct = default);

        /// <summary>
        /// 暂停播放
        /// </summary>
        Task<RTSPResponse> PauseAsync(CancellationToken ct = default);

        /// <summary>
        /// 关闭媒体通道
        /// </summary>
        Task<RTSPResponse> TeardownAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取参数
        /// </summary>
        /// <param name="parameters">参数名列表</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> GetParameterAsync(string[]? parameters = null, CancellationToken ct = default);

        /// <summary>
        /// 设置参数
        /// </summary>
        /// <param name="parameters">参数字典</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> SetParameterAsync(System.Collections.Generic.Dictionary<string, string> parameters, CancellationToken ct = default);

        /// <summary>
        /// ANNOUNCE (用于推送场景)
        /// </summary>
        /// <param name="sdpContent">SDP内容</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> AnnounceAsync(string sdpContent, CancellationToken ct = default);

        /// <summary>
        /// RECORD (用于录制)
        /// </summary>
        /// <param name="range">录制范围</param>
        /// <param name="ct">取消令牌</param>
        Task<RTSPResponse> RecordAsync(string? range = null, CancellationToken ct = default);

        #endregion

        #region 媒体流方法

        /// <summary>
        /// 获取指定track的RTP数据读取器
        /// </summary>
        /// <param name="trackId">Track ID</param>
        /// <returns>RTP包读取器</returns>
        System.Threading.Channels.ChannelReader<RTPPacket> GetRtpReader(int trackId);

        /// <summary>
        /// 获取指定track的媒体帧读取器 (解包后)
        /// </summary>
        /// <param name="trackId">Track ID</param>
        /// <returns>媒体帧读取器</returns>
        System.Threading.Channels.ChannelReader<MediaFrame> GetMediaFrameReader(int trackId);

        /// <summary>
        /// 发送音频数据 (用于ONVIF回传通道)
        /// </summary>
        /// <param name="audio">音频数据</param>
        /// <param name="sampleRate">采样率</param>
        /// <param name="codec">编码类型</param>
        /// <param name="ct">取消令牌</param>
        Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default);

        #endregion
    }
}
