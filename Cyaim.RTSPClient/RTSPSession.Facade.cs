using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Exceptions;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Rtp;
using Cyaim.RTSPClient.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSPSession 高层 Facade：
    /// - <see cref="RTSPSession(RTSPSessionConfig)"/> 配置化构造
    /// - <see cref="StartAsync"/> 一键完成 OPTIONS/DESCRIBE/SETUP 全轨/PLAY
    /// - <see cref="GetRtpReader"/> / <see cref="GetMediaFrameReader"/> 按轨道获取数据流
    ///   （打通既有 VideoReceiver/AudioReceiver 解包管线）
    /// </summary>
    public partial class RTSPSession : IRTSPSession
    {
        private RTSPSessionConfig? _config;
        private bool _configUseBackchannel;
        private int _backchannelInterleavedChannel = -1;

        // 按轨道分发的 RTP 通道与解包接收器（GetRtpReader/GetMediaFrameReader 惰性创建）
        private readonly ConcurrentDictionary<int, Channel<RTPPacket>> _trackRtpChannels = new();
        private readonly ConcurrentDictionary<int, (IDisposable receiver, ChannelReader<MediaFrame> reader)> _trackFrameReaders = new();

        // 轨道 → SDP 媒体描述（StartAsync 时填充）
        private readonly ConcurrentDictionary<int, MediaDescription> _trackMedia = new();

        /// <summary>
        /// 服务器会话超时（秒），等价于 <see cref="Timeout"/>
        /// </summary>
        public int ServerTimeout => Timeout;

        /// <summary>
        /// User-Agent（可通过 <see cref="RTSPSessionConfig.UserAgent"/> 配置）
        /// </summary>
        public string UserAgent { get; set; } = "Cyaim RTSP Client 2.0";

        /// <summary>
        /// 使用配置构造会话
        /// </summary>
        public RTSPSession(RTSPSessionConfig config)
            : this(new Uri(config.Url ?? throw new ArgumentException("RTSPSessionConfig.Url is required", nameof(config))))
        {
            if (config.TransportMode == TransportMode.UdpMulticast)
            {
                throw new NotSupportedException("TransportMode.UdpMulticast is not supported yet.");
            }

            _config = config;
            UserName = config.Username;
            Password = config.Password;
            WaitResponseTimeout = (int)config.ResponseTimeout.TotalMilliseconds;
            ConnectTimeoutMs = (int)config.ConnectTimeout.TotalMilliseconds;
            AutoReconnect = config.AutoReconnect;
            MaxReconnectAttempts = config.MaxReconnectAttempts;
            ReconnectDelayMs = (int)config.ReconnectDelay.TotalMilliseconds;
            OnvifBackChannel = config.BackchannelRequire;
            _configUseBackchannel = config.UseBackchannel;
            UserAgent = config.UserAgent;
        }

        /// <summary>
        /// 一键开始拉流：连接 → OPTIONS → DESCRIBE（自动认证）→ SETUP 所有媒体轨 → PLAY。
        /// 成功后通过 <see cref="DataReceived"/>、<see cref="GetRtpReader"/> 或
        /// <see cref="GetMediaFrameReader"/> 消费数据。
        /// </summary>
        /// <exception cref="RTSPAuthenticationException">认证失败（检查用户名/密码）</exception>
        /// <exception cref="RTSPProtocolException">服务器返回非 2xx 或 SDP 无媒体轨</exception>
        public async Task StartAsync(CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("URI not set — construct with a URL or RTSPSessionConfig");

            if (!IsConnected)
                await ConnectAsync(ct).ConfigureAwait(false);

            var options = await OptionsAsync(ct).ConfigureAwait(false);
            EnsureSuccess(options, "OPTIONS");

            var describe = await DescribeAsync(_configUseBackchannel, ct).ConfigureAwait(false);
            EnsureSuccess(describe, "DESCRIBE");

            if (SDP == null || SDP.MediaDescriptions.Count == 0)
                throw new RTSPProtocolException("DESCRIBE succeeded but SDP contains no media descriptions");

            // SETUP 所有媒体轨（TCP interleaved，通道按序分配）
            _trackMedia.Clear();
            int channelPair = 0;
            foreach (var media in SDP.MediaDescriptions)
            {
                // ONVIF 回传轨只在 DESCRIBE 带 Require 头时才会出现在 SDP 中；
                // 未启用回传时一律按普通媒体轨处理（部分服务器给普通直播轨也标 a=sendonly）
                bool isBackchannel = _configUseBackchannel && media.IsBackChannel && media.IsAudio;

                string control = media.ControlUri ?? $"trackID={channelPair}";
                int trackId = TryParseTrackIdFromControl(control) ?? channelPair;
                _trackMedia[trackId] = media;

                int rtpChannel = channelPair * 2;

                RTSPResponse setup;
                if (_config?.TransportMode == TransportMode.UdpUnicast)
                {
                    setup = await SetupUdpAsync(control, ct).ConfigureAwait(false);
                }
                else
                {
                    string transport = $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtpChannel + 1}";
                    setup = await SetupInternalAsync(control, transport, recordHistory: true, isBackchannel, ct).ConfigureAwait(false);
                }
                EnsureSuccess(setup, $"SETUP {control}");

                if (isBackchannel)
                    _backchannelInterleavedChannel = rtpChannel;

                channelPair++;
            }

            if (channelPair == 0)
                throw new RTSPProtocolException("No media track was set up (SDP had only backchannel tracks?)");

            var play = await PlayAsync(ct: ct).ConfigureAwait(false);
            EnsureSuccess(play, "PLAY");
        }

        /// <summary>
        /// 获取指定轨道的 RTP 包读取器（惰性创建，独立于 DataReceived 事件）
        /// </summary>
        public ChannelReader<RTPPacket> GetRtpReader(int trackId)
        {
            return GetOrCreateTrackChannel(trackId).Reader;
        }

        /// <summary>
        /// 获取指定轨道解包后的媒体帧读取器。
        /// 视频轨输出完整 NAL（自动注入 SDP 参数集），音频轨输出音频帧。
        /// 需在 DESCRIBE/StartAsync 之后调用（依赖 SDP 编码信息）。
        /// </summary>
        public ChannelReader<MediaFrame> GetMediaFrameReader(int trackId)
        {
            if (_trackFrameReaders.TryGetValue(trackId, out var existing))
                return existing.reader;

            var media = ResolveTrackMedia(trackId)
                ?? throw new InvalidOperationException(
                    $"Unknown track {trackId} — call StartAsync() (or DESCRIBE+SETUP) first");

            var rtpReader = GetRtpReader(trackId);
            var codec = media.GetPrimaryCodec();

            if (media.IsVideo)
            {
                var receiver = codec?.VideoCodec == VideoCodec.H265
                    ? VideoReceiver.CreateH265Receiver(rtpReader, codec)
                    : VideoReceiver.CreateH264Receiver(rtpReader, codec);
                receiver.StartReceiving();
                var reader = receiver.GetFrameReader();
                _trackFrameReaders[trackId] = (receiver, reader);
                return reader;
            }
            else
            {
                AudioReceiver receiver = codec?.AudioCodec switch
                {
                    AudioCodec.PCMU => AudioReceiver.CreateG711UReceiver(rtpReader, codec.ClockRate > 0 ? codec.ClockRate : 8000, codec.Channels),
                    AudioCodec.AAC or AudioCodec.AAC_LD or AudioCodec.AAC_ELD or AudioCodec.MPEG4_GENERIC =>
                        new AudioReceiver(rtpReader, AACDepacketizer.CreateFromCodecInfo(codec), AudioCodec.AAC,
                            codec.ClockRate > 0 ? codec.ClockRate : 44100, codec.Channels),
                    _ => AudioReceiver.CreateG711AReceiver(rtpReader,
                            codec?.ClockRate > 0 ? codec!.ClockRate : 8000, codec?.Channels ?? 1)
                };
                receiver.StartReceiving();
                var reader = receiver.GetFrameReader();
                _trackFrameReaders[trackId] = (receiver, reader);
                return reader;
            }
        }

        /// <summary>
        /// 发送音频到 ONVIF 回传通道（G.711）
        /// </summary>
        public Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default)
        {
            byte channel = _backchannelInterleavedChannel >= 0 ? (byte)_backchannelInterleavedChannel : (byte)0;
            long ssrc = Guid.NewGuid().GetHashCode();
            return PlayAudio_G711(audio, fps: 50, sampleRate, codec, ssrc, channel, progress: null, ct);
        }

        #region IRTSPSession 显式对齐重载

        /// <summary>
        /// SETUP（枚举传输模式版本；通道自动按已 SETUP 轨道数分配）
        /// </summary>
        public Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default)
        {
            switch (mode)
            {
                case TransportMode.TcpInterleaved:
                    int channelPair;
                    lock (_setupHistory)
                    {
                        channelPair = _setupHistory.Count;
                    }
                    string transport = $"RTP/AVP/TCP;unicast;interleaved={channelPair * 2}-{channelPair * 2 + 1}";
                    return SetupAsync(trackUri, transport, false, ct);

                case TransportMode.UdpUnicast:
                    return SetupUdpAsync(trackUri, ct);

                default:
                    throw new NotSupportedException($"TransportMode.{mode} is not supported yet.");
            }
        }

        /// <inheritdoc cref="DescribeAsync(bool, CancellationToken)"/>
        public Task<RTSPResponse> DescribeAsync(CancellationToken ct)
            => DescribeAsync(false, ct);

        /// <inheritdoc cref="PlayAsync(string?, bool, CancellationToken)"/>
        public Task<RTSPResponse> PlayAsync(string? range, CancellationToken ct)
            => PlayAsync(range, false, ct);

        /// <inheritdoc cref="TeardownAsync(string?, bool, CancellationToken)"/>
        public Task<RTSPResponse> TeardownAsync(CancellationToken ct)
            => TeardownAsync(null, false, ct);

        #endregion

        #region 内部支撑

        /// <summary>
        /// 非 2xx 抛出类型化异常（401 → 认证异常，404 → 协议异常等）
        /// </summary>
        private static void EnsureSuccess(RTSPResponse response, string operation)
        {
            if (response.StatusCode == "200")
                return;

            if (response.StatusCode == "401" || response.StatusCode == "403")
            {
                throw new RTSPAuthenticationException(
                    $"{operation} failed with {response.StatusCode} {response.StatusMsg} (check username/password)");
            }

            throw new RTSPProtocolException(
                $"{operation} failed with {response.StatusCode} {response.StatusMsg}");
        }

        internal static int? TryParseTrackIdFromControl(string controlUri)
        {
            int index = controlUri.LastIndexOf("trackID=", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            int start = index + "trackID=".Length;
            int end = start;
            while (end < controlUri.Length && char.IsDigit(controlUri[end]))
                end++;

            return end > start && int.TryParse(controlUri.Substring(start, end - start), out int trackId)
                ? trackId
                : null;
        }

        private Channel<RTPPacket> GetOrCreateTrackChannel(int trackId)
        {
            return _trackRtpChannels.GetOrAdd(trackId, _ =>
                Channel.CreateBounded<RTPPacket>(new BoundedChannelOptions(Math.Max(64, _config?.RtpChannelBufferSize ?? 1024))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true
                }));
        }

        /// <summary>
        /// 泵线程调用：把包路由到对应轨道的读取通道（若有人订阅）
        /// </summary>
        private void RouteToTrackChannel(in RTPPacket packet)
        {
            if (_trackRtpChannels.TryGetValue(packet.TrackId, out var channel))
            {
                channel.Writer.TryWrite(packet);
            }
        }

        private MediaDescription? ResolveTrackMedia(int trackId)
        {
            if (_trackMedia.TryGetValue(trackId, out var media))
                return media;

            // 未经 StartAsync 的会话：按 SDP 顺序/控制属性推断
            if (SDP != null)
            {
                foreach (var m in SDP.MediaDescriptions)
                {
                    if (m.ControlUri != null && TryParseTrackIdFromControl(m.ControlUri) == trackId)
                        return m;
                }

                if (trackId >= 0 && trackId < SDP.MediaDescriptions.Count)
                    return SDP.MediaDescriptions[trackId];
            }

            return null;
        }

        /// <summary>
        /// 断开/释放时清理 Facade 资源
        /// </summary>
        private void CleanupFacade()
        {
            foreach (var entry in _trackFrameReaders.Values)
            {
                try { entry.receiver.Dispose(); } catch { }
            }
            _trackFrameReaders.Clear();

            foreach (var channel in _trackRtpChannels.Values)
            {
                channel.Writer.TryComplete();
            }
            _trackRtpChannels.Clear();
            _trackMedia.Clear();
            _backchannelInterleavedChannel = -1;
        }

        #endregion
    }
}
