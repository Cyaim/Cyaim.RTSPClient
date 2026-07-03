using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Events;
using Cyaim.RTSPClient.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSP 会话管理类
    /// 支持完整的 RTSP 协议操作
    /// </summary>
    public partial class RTSPSession : IDisposable, IAsyncDisposable
    {
        #region 字段

        private TcpClient? _client;
        private NetworkStream? _tcpStream;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<RTSPResponse>> _pendingRequests = new();
        private readonly ConcurrentDictionary<int, InterleavedChannelInfo> _channelMap = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);  // 序列化对 NetworkStream 的并发写入
        private int _cseq;
        private int _disposed;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;

        // 已 SETUP 的轨道记录（重连时按此恢复）
        private readonly List<(string channelUri, string transport)> _setupHistory = new();
        private string? _playRange;
        private volatile bool _userDisconnect;

        // DESCRIBE 响应中的 Content-Base/Content-Location（RFC 2326 控制 URI 解析基址）
        private string? _contentBase;

        /// <summary>
        /// TCP interleaved 通道注册信息（SETUP 时记录）
        /// </summary>
        private readonly struct InterleavedChannelInfo
        {
            public InterleavedChannelInfo(int trackId, StreamType streamType, bool isRtcp)
            {
                TrackId = trackId;
                StreamType = streamType;
                IsRtcp = isRtcp;
            }

            public int TrackId { get; }
            public StreamType StreamType { get; }
            public bool IsRtcp { get; }
        }

        #endregion

        #region 属性

        /// <summary>
        /// RTSP URI
        /// </summary>
        public Uri? Uri { get; private set; }

        /// <summary>
        /// 最后一次异常
        /// </summary>
        public Exception? LastException { get; private set; }

        /// <summary>
        /// 连接状态
        /// </summary>
        public RTSPConnectionState State { get; private set; } = RTSPConnectionState.Disconnected;

        /// <summary>
        /// SDP 会话描述
        /// </summary>
        public SDPSession? SDP { get; private set; }

        /// <summary>
        /// RTSP 会话 ID
        /// </summary>
        public string? SessionId { get; private set; }

        /// <summary>
        /// 服务器支持的方法
        /// </summary>
        public string? Public { get; private set; }

        /// <summary>
        /// 服务器超时时间（秒）
        /// </summary>
        public int Timeout { get; private set; }

        /// <summary>
        /// 等待响应超时（毫秒）
        /// </summary>
        public int WaitResponseTimeout { get; set; } = 10000;

        /// <summary>
        /// 认证用户名
        /// </summary>
        public string? UserName { get; set; }

        /// <summary>
        /// 认证密码
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Digest 认证 Realm
        /// </summary>
        public string? Realm { get; private set; }

        /// <summary>
        /// Digest 认证 Nonce
        /// </summary>
        public string? Nonce { get; private set; }

        /// <summary>
        /// Authorization 头
        /// </summary>
        public string? Authorization { get; private set; }

        /// <summary>
        /// ONVIF 回传通道标识
        /// </summary>
        public string OnvifBackChannel { get; set; } = "www.onvif.org/ver20/backchannel";

        /// <summary>
        /// 是否支持回传通道
        /// </summary>
        public bool HasBackChannelSupported { get; private set; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => State >= RTSPConnectionState.Connected;

        /// <summary>
        /// TCP 连接超时（毫秒），默认 10 秒
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// 是否自动发送 keep-alive（按服务器 Session timeout 的一半间隔发送 GET_PARAMETER）。
        /// PLAY 成功后自动启动，默认开启。
        /// </summary>
        public bool AutoKeepAlive { get; set; } = true;

        /// <summary>
        /// 是否在接收循环意外断开后自动重连并恢复 SETUP/PLAY，默认关闭
        /// </summary>
        public bool AutoReconnect { get; set; }

        /// <summary>
        /// 自动重连最大尝试次数（0 = 无限重试）
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 5;

        /// <summary>
        /// 自动重连初始间隔（毫秒），按指数退避增长，上限 30 秒
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 2000;

        /// <summary>
        /// 是否自动发送 RTCP Receiver Report（每 5 秒，部分服务器/相机依赖 RTCP 保活），默认开启
        /// </summary>
        public bool EnableRtcp { get; set; } = true;

        /// <summary>
        /// RTP 包分发队列容量。消费者处理不及时时丢弃最旧的包而不是阻塞 TCP 读取。
        /// </summary>
        public int ReceiveQueueCapacity { get; set; } = 4096;

        /// <summary>
        /// 因消费者处理过慢而被丢弃的 RTP 包计数
        /// </summary>
        public long PacketsDropped => Interlocked.Read(ref _packetsDropped);

        #endregion

        #region 事件

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        public event EventHandler<RTSPConnectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// RTP 数据接收事件
        /// </summary>
        public event EventHandler<RtpDataReceivedEventArgs>? DataReceived;

        /// <summary>
        /// 错误事件
        /// </summary>
        public event EventHandler<RTSPErrorEventArgs>? Error;

        /// <summary>
        /// Keep-Alive 结果事件
        /// </summary>
        public event EventHandler<KeepAliveEventArgs>? KeepAlive;

        #endregion

        #region 构造函数

        public RTSPSession() { }

        public RTSPSession(Uri uri)
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        }

        public RTSPSession(string url) : this(new Uri(url)) { }

        #endregion

        #region 连接管理

        /// <summary>
        /// 连接到 RTSP 服务器
        /// </summary>
        public static RTSPSession Connect(string url)
        {
            var session = new RTSPSession(url);
            session.ConnectInternal();
            return session;
        }

        /// <summary>
        /// 异步连接到 RTSP 服务器
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("URI not set");

            await ConnectInternalAsync(ct);
        }

        private void ConnectInternal()
        {
            ConnectInternalAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task ConnectInternalAsync(CancellationToken ct)
        {
            if (Uri == null)
                throw new InvalidOperationException("URI not set");

            try
            {
                SetState(RTSPConnectionState.Connecting);
                _userDisconnect = false;

                var client = new TcpClient { NoDelay = true };
                _client = client;

                // TcpClient.ConnectAsync(host, port) 在 netstandard2.1 上不接受取消令牌，
                // 用超时 + 调用方 ct 联合控制，避免卡在 OS SYN 超时（约 20 秒）
                var connectTask = client.ConnectAsync(Uri.Host, Uri.Port <= 0 ? 554 : Uri.Port);
                var delayTask = Task.Delay(Math.Max(1000, ConnectTimeoutMs), ct);
                var finished = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);

                if (finished != connectTask)
                {
                    try { client.Dispose(); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new Exceptions.RTSPTimeoutException(
                        $"Connect to {Uri.Host}:{Uri.Port} timed out after {ConnectTimeoutMs}ms");
                }

                await connectTask.ConfigureAwait(false); // 传播连接异常

                _tcpStream = client.GetStream();
                SetState(RTSPConnectionState.Connected);
                StartReceiveLoop();
            }
            catch (Exception ex)
            {
                LastException = ex;
                SetState(RTSPConnectionState.Disconnected);
                OnError(ex);
                throw;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            if (State == RTSPConnectionState.Disconnected)
                return;

            _userDisconnect = true;  // 主动断开，不触发自动重连
            SetState(RTSPConnectionState.Disconnecting);

            StopKeepAlive();
            StopRtcp();

            try
            {
                // 尝试发送 TEARDOWN（限时 2 秒，不让优雅关闭拖成长阻塞）
                if (Uri != null && SessionId != null && _tcpStream != null)
                {
                    try
                    {
                        using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        teardownCts.CancelAfter(2000);
                        await TeardownAsync(ct: teardownCts.Token).ConfigureAwait(false);
                    }
                    catch { }
                }

                await StopReceiveLoopAsync().ConfigureAwait(false);
                _tcpStream?.Close();
                _client?.Close();
            }
            finally
            {
                _tcpStream = null;
                _client = null;
                SessionId = null;
                SDP = null;
                _contentBase = null;
                _channelMap.Clear();
                _rtpTrackers.Clear();
                lock (_setupHistory)
                {
                    _setupHistory.Clear();
                }
                _playRange = null;
                CleanupFacade();
                CleanupUdpTransports();
                SetState(RTSPConnectionState.Disconnected);
            }
        }

        /// <summary>
        /// 重连
        /// </summary>
        public async Task ReconnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct);
            await ConnectInternalAsync(ct);

            // 重新登录
            if (UserName != null && Password != null && Uri != null)
            {
                await LoginDigestAsync(UserName, Password, Uri.AbsoluteUri, HasBackChannelSupported, ct);
            }
        }

        #endregion

        #region 接收循环

        private void StartReceiveLoop()
        {
            _receiveCts = new CancellationTokenSource();
            var ct = _receiveCts.Token;
            var channel = StartPacketPump(ct);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(channel, ct));
        }

        private void StopReceiveLoop()
        {
            _receiveCts?.Cancel();
            try
            {
                _receiveTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) { }
            _receiveCts?.Dispose();
            _receiveCts = null;
        }

        private async Task StopReceiveLoopAsync()
        {
            _receiveCts?.Cancel();
            var task = _receiveTask;
            if (task != null)
            {
                try
                {
                    await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false);
                }
                catch { }
            }
            _receiveCts?.Dispose();
            _receiveCts = null;
        }

        /// <summary>
        /// 让所有等待响应的请求立即失败（连接已断，无需干等超时）
        /// </summary>
        private void FailPendingRequests(Exception reason)
        {
            foreach (var key in _pendingRequests.Keys)
            {
                if (_pendingRequests.TryRemove(key, out var tcs))
                {
                    tcs.TrySetException(reason);
                }
            }
        }

        private async Task ReceiveLoopAsync(Channel<Rtp.RTPPacket> myChannel, CancellationToken ct)
        {
            if (_tcpStream == null) return;

            // 累积缓冲：TCP 是字节流，interleaved RTP 帧和 RTSP 响应都可能跨多次
            // Read 到达，或一次 Read 到达多帧（旧实现按“一次 Read 一条消息”处理，
            // 会大量丢弃 RTP 包，音频尤其严重）
            var buffer = new byte[64 * 1024];
            int buffered = 0;

            try
            {
                while (!ct.IsCancellationRequested && _tcpStream != null)
                {
                    if (buffered == buffer.Length)
                    {
                        // 单条消息超过缓冲区，扩容（上限 1MB，超过视为协议错误）
                        if (buffer.Length >= 1024 * 1024)
                            throw new InvalidOperationException("RTSP message exceeds 1MB buffer limit");
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    int bytesRead = await _tcpStream.ReadAsync(buffer, buffered, buffer.Length - buffered, ct).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        // 服务器关闭连接
                        break;
                    }

                    buffered += bytesRead;
                    int offset = 0;

                    while (offset < buffered)
                    {
                        if (buffer[offset] == 0x24)
                        {
                            // TCP interleaved RTP/RTCP 帧
                            if (buffered - offset < 4)
                                break; // 头不完整，等待更多数据

                            byte channel = buffer[offset + 1];
                            int length = (buffer[offset + 2] << 8) | buffer[offset + 3];

                            if (buffered - offset < 4 + length)
                                break; // 帧不完整，等待更多数据

                            var rtpData = new byte[length];
                            Array.Copy(buffer, offset + 4, rtpData, 0, length);
                            OnDataReceived(rtpData, channel);

                            offset += 4 + length;
                        }
                        else
                        {
                            // RTSP 响应文本：先找头部结束，再按 Content-Length 取内容体
                            int headerEnd = FindHeaderEnd(buffer, offset, buffered, out int markerLength);
                            if (headerEnd < 0)
                                break; // 头部不完整，等待更多数据

                            string headerText = Encoding.UTF8.GetString(buffer, offset, headerEnd - offset);
                            int contentLength = ParseContentLength(headerText);
                            int totalLength = (headerEnd - offset) + markerLength + contentLength;

                            if (buffered - offset < totalLength)
                                break; // 内容体不完整，等待更多数据

                            string msg = Encoding.UTF8.GetString(buffer, offset, totalLength);
                            var rawBytes = new byte[totalLength];
                            Array.Copy(buffer, offset, rawBytes, 0, totalLength);

                            // 单条畸形响应只丢弃该条消息，不终止整个会话
                            try
                            {
                                var response = new RTSPResponse(msg, rawBytes);

                                // 完成等待的请求
                                if (_pendingRequests.TryRemove(response.CSeq, out var tcs))
                                {
                                    tcs.TrySetResult(response);
                                }
                            }
                            catch (Exception ex)
                            {
                                LastException = ex;
                            }

                            offset += totalLength;
                        }
                    }

                    // 移动剩余数据到缓冲区开头
                    if (offset > 0 && offset < buffered)
                    {
                        Buffer.BlockCopy(buffer, offset, buffer, 0, buffered - offset);
                        buffered -= offset;
                    }
                    else if (offset >= buffered)
                    {
                        buffered = 0;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LastException = ex;
                OnError(ex);
            }
            finally
            {
                // 关闭本循环自己的泵通道
                myChannel.Writer.TryComplete();

                // 重连场景下新循环可能已经接管：过期循环不得清理全局状态
                //（否则会关掉新泵、误置 Disconnected、误杀新连接的挂起请求）
                if (ReferenceEquals(myChannel, _packetChannel))
                {
                    bool wasStreaming = State == RTSPConnectionState.Playing || State == RTSPConnectionState.Setup;

                    // 连接已断：让所有等待响应的调用立即失败，而不是干等超时
                    FailPendingRequests(new Exceptions.RTSPConnectionException("Connection closed"));
                    StopKeepAlive();
                    StopRtcp();

                    if (State != RTSPConnectionState.Disconnecting &&
                        State != RTSPConnectionState.Disconnected)
                    {
                        SetState(RTSPConnectionState.Disconnected);
                    }

                    // 意外断开且启用自动重连时，尝试恢复会话（含 SETUP/PLAY）
                    if (AutoReconnect && wasStreaming && !_userDisconnect && _disposed == 0)
                    {
                        _ = Task.Run(() => AutoReconnectLoopAsync());
                    }
                }
            }
        }

        private static readonly byte[] CrlfCrlf = { 0x0D, 0x0A, 0x0D, 0x0A };
        private static readonly byte[] LfLf = { 0x0A, 0x0A };

        /// <summary>
        /// 字节级查找头部结束标记（\r\n\r\n 或 \n\n）。
        /// 使用 Span.IndexOf（运行时内部 SIMD 向量化）替代逐字节扫描。
        /// </summary>
        /// <returns>头部结束位置（buffer 内绝对偏移），未找到返回 -1</returns>
        private static int FindHeaderEnd(byte[] buffer, int offset, int end, out int markerLength)
        {
            var span = new ReadOnlySpan<byte>(buffer, offset, end - offset);

            int crlfIndex = span.IndexOf(CrlfCrlf);
            int lflfIndex = span.IndexOf(LfLf);

            // 取更早出现的标记（\n\n 是 \r\n\r\n 的子串，位置相同时取 4 字节标记）
            if (crlfIndex >= 0 && (lflfIndex < 0 || crlfIndex <= lflfIndex - 1))
            {
                markerLength = 4;
                return offset + crlfIndex;
            }
            if (lflfIndex >= 0)
            {
                markerLength = 2;
                return offset + lflfIndex;
            }

            markerLength = 0;
            return -1;
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(trimmed.Substring("Content-Length:".Length).Trim(), out int length)
                        ? length : 0;
                }
            }
            return 0;
        }

        private void OnDataReceived(byte[] data, byte channel)
        {
            int trackId;
            StreamType streamType;

            if (_channelMap.TryGetValue(channel, out var info))
            {
                if (info.IsRtcp)
                {
                    HandleRtcpFrame(channel, info.TrackId, data);
                    return; // RTCP 帧不作为 RTP 数据抛出
                }
                trackId = info.TrackId;
                streamType = info.StreamType;
            }
            else
            {
                // 未注册的通道按约定推断：偶数=RTP，奇数=RTCP；track0=视频，其余=音频
                if (channel % 2 != 0)
                {
                    HandleRtcpFrame(channel, channel / 2, data);
                    return;
                }
                trackId = channel / 2;
                streamType = trackId == 0 ? StreamType.Video : StreamType.Audio;
            }

            Rtp.RTPPacket packet;
            try
            {
                packet = Rtp.RTPPacketParser.Parse(data, trackId, streamType);
            }
            catch (Exceptions.RTPParseException)
            {
                return; // 丢弃无法解析的包
            }

            // 更新 RTCP 接收统计（RR 报告用）
            UpdateReceptionStats(channel, in packet);

            // 写入有界队列由泵线程分发：
            // 1) 消费者慢时丢最旧的包而不是让 TCP 窗口填满拖垮整条连接
            // 2) 消费者异常被隔离，不会杀死接收循环
            EnqueuePacket(in packet);
        }

        #endregion

        #region 发送方法

        /// <summary>
        /// 发送原始数据（与 RTSP 请求共用发送锁，避免并发写坏 TCP 流）
        /// </summary>
        public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
        {
            var stream = _tcpStream ?? throw new InvalidOperationException("Not connected");

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 发送 RTSP 请求并等待响应。
        /// 收到 401 且已配置凭据时自动携带认证信息重试一次（Digest/Basic）。
        /// </summary>
        public async Task<RTSPResponse> SendRequestAsync(RTSPRequest request, CancellationToken ct = default)
        {
            var response = await SendRequestOnceAsync(request, ct).ConfigureAwait(false);

            // 401 自动重试：解析质询、更新 Authorization、原请求重发一次
            if (response.StatusCode == "401" && UserName != null && Password != null)
            {
                HandleAuthChallenge(response);
                UpdateAuthorization(request.Method, request.URI);
                if (Authorization != null)
                {
                    request.CSeq = Interlocked.Increment(ref _cseq);
                    request.Authorization = Authorization;
                    response = await SendRequestOnceAsync(request, ct).ConfigureAwait(false);
                }
            }

            return response;
        }

        private async Task<RTSPResponse> SendRequestOnceAsync(RTSPRequest request, CancellationToken ct)
        {
            var stream = _tcpStream ?? throw new InvalidOperationException("Not connected");

            // 创建 TCS 用于等待响应
            var tcs = new TaskCompletionSource<RTSPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[request.CSeq] = tcs;

            try
            {
                // 发送请求（含内容体，GetRequest 会自动补齐 UTF-8 字节长度的 Content-Length）
                string req = RTSPRequest.GetRequest(request);
                byte[] data = Encoding.UTF8.GetBytes(req);

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                // 等待响应（带超时）
                using var timeoutCts = new CancellationTokenSource(WaitResponseTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new Exceptions.RTSPTimeoutException($"RTSP response timeout for CSeq {request.CSeq}");
                }
            }
            finally
            {
                // 任何退出路径（超时/取消/发送异常）都清理挂起表，避免泄漏
                _pendingRequests.TryRemove(request.CSeq, out _);
            }
        }

        #endregion

        #region RTSP 方法

        /// <summary>
        /// OPTIONS - 查询服务器支持的方法
        /// </summary>
        public async Task<RTSPResponse> OptionsAsync(CancellationToken ct = default)
        {
            var request = CreateRequest("OPTIONS");

            // 已有质询信息时带上认证（部分服务器在 OPTIONS 上也要求认证）
            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == "200")
            {
                Public = GetHeader(response, "Public");
            }

            return response;
        }

        /// <summary>
        /// DESCRIBE - 获取 SDP 描述
        /// </summary>
        public async Task<RTSPResponse> DescribeAsync(bool useBackchannel = false, CancellationToken ct = default)
        {
            var request = CreateRequest("DESCRIBE");
            request.Accept = "application/sdp";
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == "200")
            {
                // RFC 2326：相对控制 URI 的解析基址优先级 Content-Base > Content-Location > 请求 URI
                _contentBase = GetHeader(response, "Content-Base") ?? GetHeader(response, "Content-Location");

                try
                {
                    SDP = SDPParser.Parse(response.Response);
                }
                catch (Exception ex)
                {
                    throw new Exceptions.RTSPProtocolException("Failed to parse SDP from DESCRIBE response", ex);
                }

                if (useBackchannel)
                {
                    HasBackChannelSupported = SDP.GetBackChannel() != null;
                }
            }
            else if (response.StatusCode == "401")
            {
                // SendRequestAsync 已自动重试过；仍 401 说明凭据错误或未设置，记录质询供手动处理
                HandleAuthChallenge(response);
            }

            return response;
        }

        /// <summary>
        /// 大小写不敏感地读取响应头
        /// </summary>
        private static string? GetHeader(RTSPResponse response, string name)
        {
            foreach (var header in response.Headers)
            {
                if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return header.Value;
            }
            return null;
        }

        /// <summary>
        /// SETUP - 设置媒体传输通道
        /// </summary>
        public Task<RTSPResponse> SetupAsync(string channelUri, string transport, bool useBackchannel = false, CancellationToken ct = default)
        {
            return SetupInternalAsync(channelUri, transport, recordHistory: true, useBackchannel, ct);
        }

        private async Task<RTSPResponse> SetupInternalAsync(string channelUri, string transport, bool recordHistory, bool useBackchannel, CancellationToken ct)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("SETUP");
            request.URI = BuildSetupUri(channelUri);
            request.Transport = transport;
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method, request.URI);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct).ConfigureAwait(false);
            UpdateSessionFromResponse(response);

            if (response.StatusCode == "200")
            {
                RegisterInterleavedChannels(channelUri, transport, response);

                if (recordHistory)
                {
                    // 记录 SETUP 参数，自动重连时按序重放
                    lock (_setupHistory)
                    {
                        _setupHistory.RemoveAll(x => x.channelUri == channelUri);
                        _setupHistory.Add((channelUri, transport));
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// 拼接 SETUP URI（RFC 2326 C.1.1）：
        /// - a=control:* 表示使用聚合基址本身
        /// - 绝对 URL 直接使用
        /// - 相对路径基于 Content-Base > Content-Location > 请求 URI 解析
        /// </summary>
        private string BuildSetupUri(string channelUri)
        {
            string baseUri = _contentBase ?? Uri!.AbsoluteUri;

            if (string.IsNullOrEmpty(channelUri) || channelUri == "*")
                return baseUri;

            // 绝对 URL 直接使用
            if (channelUri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                channelUri.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
                return channelUri;

            if (baseUri.EndsWith("/") || channelUri.StartsWith("/"))
                return baseUri + channelUri;
            return baseUri + "/" + channelUri;
        }

        /// <summary>
        /// SETUP 成功后注册 interleaved 通道 → 轨道/媒体类型映射
        /// （旧实现按 channel%2 猜测音视频，把音频 RTP(通道2)误判为视频、把 RTCP 当 RTP 抛出）
        /// </summary>
        private void RegisterInterleavedChannels(string channelUri, string requestedTransport, RTSPResponse response)
        {
            // 服务器响应的 Transport 优先，回退到请求值
            string transport = response.Headers.FirstOrDefault(x =>
                x.Key.Equals("Transport", StringComparison.OrdinalIgnoreCase)).Value ?? requestedTransport;

            int rtpChannel = -1, rtcpChannel = -1;
            foreach (var part in transport.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase))
                {
                    var channels = trimmed.Substring("interleaved=".Length).Split('-');
                    if (channels.Length >= 1 && int.TryParse(channels[0], out int start))
                        rtpChannel = start;
                    if (channels.Length >= 2 && int.TryParse(channels[1], out int end))
                        rtcpChannel = end;
                }
            }

            if (rtpChannel < 0)
                return; // 非 interleaved 模式

            // 解析 trackID
            int trackId = rtpChannel / 2;
            int trackIndex = channelUri.LastIndexOf("trackID=", StringComparison.OrdinalIgnoreCase);
            if (trackIndex >= 0)
            {
                int start = trackIndex + "trackID=".Length;
                int end = start;
                while (end < channelUri.Length && char.IsDigit(channelUri[end]))
                    end++;
                if (end > start && int.TryParse(channelUri.Substring(start, end - start), out int parsed))
                    trackId = parsed;
            }

            // 从 SDP 确定媒体类型：优先匹配控制属性，回退到 track0=视频 约定
            StreamType streamType = trackId == 0 ? StreamType.Video : StreamType.Audio;
            var media = SDP?.MediaDescriptions?.FirstOrDefault(m =>
                m.ControlUri != null &&
                (m.ControlUri.EndsWith(channelUri, StringComparison.OrdinalIgnoreCase) ||
                 channelUri.EndsWith(m.ControlUri, StringComparison.OrdinalIgnoreCase)));
            if (media != null)
            {
                streamType = media.IsAudio ? StreamType.Audio : StreamType.Video;
            }

            _channelMap[rtpChannel] = new InterleavedChannelInfo(trackId, streamType, isRtcp: false);
            if (rtcpChannel >= 0)
                _channelMap[rtcpChannel] = new InterleavedChannelInfo(trackId, streamType, isRtcp: true);

            // 注册 RTCP 接收统计上下文（时钟频率取自 SDP，视频默认 90000）
            int clockRate = media?.GetPrimaryCodec()?.ClockRate ?? 0;
            if (clockRate <= 0)
                clockRate = streamType == StreamType.Video ? 90000 : 8000;
            RegisterRtpTracker((byte)rtpChannel, (byte)(rtcpChannel >= 0 ? rtcpChannel : rtpChannel + 1), trackId, clockRate);
        }

        /// <summary>
        /// PLAY - 开始播放
        /// </summary>
        public async Task<RTSPResponse> PlayAsync(string? range = null, bool useBackchannel = false, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("PLAY");
            request.Session = SessionId;
            if (range != null)
                request.Range = range;
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct).ConfigureAwait(false);
            UpdateSessionFromResponse(response);

            if (response.StatusCode == "200")
            {
                _playRange = range;
                SetState(RTSPConnectionState.Playing);

                // 自动保活与 RTCP RR（可通过 AutoKeepAlive/EnableRtcp 关闭）
                StartKeepAlive();
                StartRtcp();
            }

            return response;
        }

        /// <summary>
        /// PAUSE - 暂停播放
        /// </summary>
        public async Task<RTSPResponse> PauseAsync(CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("PAUSE");
            request.Session = SessionId;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct);

            if (response.StatusCode == "200")
            {
                SetState(RTSPConnectionState.Paused);
            }

            return response;
        }

        /// <summary>
        /// TEARDOWN - 关闭媒体通道
        /// </summary>
        public async Task<RTSPResponse> TeardownAsync(string? channelUri = null, bool useBackchannel = false, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("TEARDOWN");
            request.URI = channelUri == null ? (_contentBase ?? Uri.AbsoluteUri) : BuildSetupUri(channelUri);
            request.Session = SessionId;
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method, request.URI);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct).ConfigureAwait(false);

            // 会话已拆除：清理重连恢复所需的状态
            if (channelUri == null)
            {
                lock (_setupHistory)
                {
                    _setupHistory.Clear();
                }
                _playRange = null;
            }

            return response;
        }

        /// <summary>
        /// GET_PARAMETER - 获取参数（也用作心跳）
        /// </summary>
        public async Task<RTSPResponse> GetParameterAsync(string[]? parameters = null, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("GET_PARAMETER");
            request.Session = SessionId;

            if (parameters != null && parameters.Length > 0)
            {
                request.ContentType = "text/parameters";
                // 内容体由 GetRequest 统一追加并按 UTF-8 字节数计算 Content-Length
                var sb = new StringBuilder();
                foreach (var p in parameters)
                {
                    sb.Append(p).Append("\r\n");
                }
                request.Content = sb.ToString();
            }

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// SET_PARAMETER - 设置参数
        /// </summary>
        public async Task<RTSPResponse> SetParameterAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("SET_PARAMETER");
            request.Session = SessionId;
            request.ContentType = "text/parameters";

            var sb = new StringBuilder();
            foreach (var kvp in parameters)
            {
                sb.Append(kvp.Key).Append(": ").Append(kvp.Value).Append("\r\n");
            }
            request.Content = sb.ToString();

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// ANNOUNCE - 发布媒体描述（用于推送）
        /// </summary>
        public async Task<RTSPResponse> AnnounceAsync(string sdpContent, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("ANNOUNCE");
            request.ContentType = "application/sdp";
            request.Content = sdpContent;  // 旧实现只发 Content-Length 不发 SDP 本体，服务器会等 body 卡死

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// RECORD - 开始录制
        /// </summary>
        public async Task<RTSPResponse> RecordAsync(string? range = null, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("RECORD");
            request.Session = SessionId;
            if (range != null)
                request.Range = range;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct);
        }

        /// <summary>
        /// 登录（Digest 认证）
        /// </summary>
        public async Task<RTSPResponse> LoginDigestAsync(string username, string password, string uri, bool useBackchannel = false, CancellationToken ct = default)
        {
            UserName = username;
            Password = password;

            // 1. OPTIONS（SendRequestAsync 对 401 已自动携带凭据重试，Digest/Basic 均支持）
            var optionsResponse = await OptionsAsync(ct).ConfigureAwait(false);
            if (optionsResponse.StatusCode == "401")
            {
                throw new Exceptions.RTSPAuthenticationException(
                    "Authentication failed for OPTIONS (check username/password)");
            }
            if (optionsResponse.StatusCode != "200")
            {
                throw new Exceptions.RTSPProtocolException(
                    $"OPTIONS request failed with status {optionsResponse.StatusCode} {optionsResponse.StatusMsg}");
            }

            // 2. DESCRIBE
            var describeResponse = await DescribeAsync(useBackchannel, ct).ConfigureAwait(false);
            if (describeResponse.StatusCode == "401")
            {
                throw new Exceptions.RTSPAuthenticationException(
                    "Authentication failed for DESCRIBE (check username/password)");
            }

            if (useBackchannel)
            {
                HasBackChannelSupported = SDP?.GetBackChannel() != null;
            }

            return describeResponse;
        }

        /// <summary>
        /// 发送 Keep-Alive 心跳
        /// </summary>
        public async Task<bool> SendKeepAliveAsync(CancellationToken ct = default)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                // 优先 GET_PARAMETER，服务器不支持时退回 OPTIONS（同样能刷新会话超时）
                RTSPResponse response;
                if (Public == null || Public.IndexOf("GET_PARAMETER", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    response = await GetParameterAsync(ct: ct).ConfigureAwait(false);
                }
                else
                {
                    response = await OptionsAsync(ct).ConfigureAwait(false);
                }

                sw.Stop();

                bool ok = response.StatusCode == "200";
                KeepAlive?.Invoke(this, new KeepAliveEventArgs(ok, (int)sw.ElapsedMilliseconds));
                return ok;
            }
            catch (Exception ex)
            {
                KeepAlive?.Invoke(this, new KeepAliveEventArgs(false, 0));
                OnError(ex);
                return false;
            }
        }

        #endregion

        #region 辅助方法

        private RTSPRequest CreateRequest(string method)
        {
            return new RTSPRequest
            {
                Method = method,
                URI = Uri?.AbsoluteUri ?? "",
                Version = "RTSP/1.0",
                UserAgent = UserAgent,
                CSeq = Interlocked.Increment(ref _cseq)
            };
        }

        // Digest 质询参数（RFC 2617/7616）
        private string? _authQop;
        private string? _authOpaque;
        private string? _authAlgorithm;
        private bool _useBasicAuth;
        private int _nonceCount;

        private void HandleAuthChallenge(RTSPResponse response)
        {
            // 可能同时存在 Digest 和 Basic 两条质询，优先 Digest
            string? digestChallenge = null;
            string? basicChallenge = null;

            foreach (var header in response.Headers)
            {
                if (!header.Key.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) || header.Value == null)
                    continue;

                if (header.Value.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                    digestChallenge ??= header.Value;
                else if (header.Value.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
                    basicChallenge ??= header.Value;
            }

            if (digestChallenge != null)
            {
                _useBasicAuth = false;
                var p = ParseChallengeParams(digestChallenge.Substring("Digest".Length));
                Realm = p.TryGetValue("realm", out var realm) ? realm : "RTSP SERVER";
                if (p.TryGetValue("nonce", out var nonce))
                {
                    // 新 nonce 重置 nc 计数
                    if (nonce != Nonce)
                        _nonceCount = 0;
                    Nonce = nonce;
                }
                _authQop = p.TryGetValue("qop", out var qop) ? qop : null;
                _authOpaque = p.TryGetValue("opaque", out var opaque) ? opaque : null;
                _authAlgorithm = p.TryGetValue("algorithm", out var alg) ? alg : null;
            }
            else if (basicChallenge != null)
            {
                _useBasicAuth = true;
                var p = ParseChallengeParams(basicChallenge.Substring("Basic".Length));
                Realm = p.TryGetValue("realm", out var realm) ? realm : "RTSP SERVER";
            }
            else
            {
                throw new Exceptions.RTSPAuthenticationException(
                    "Server offered no supported authentication scheme (expected Digest or Basic)");
            }
        }

        /// <summary>
        /// 解析质询参数（key="value" 或 key=value，逗号分隔；值内允许逗号内嵌于引号）
        /// </summary>
        private static Dictionary<string, string> ParseChallengeParams(string challenge)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < challenge.Length)
            {
                // 跳过分隔符
                while (i < challenge.Length && (challenge[i] == ',' || char.IsWhiteSpace(challenge[i])))
                    i++;

                int eq = challenge.IndexOf('=', i);
                if (eq < 0) break;

                string key = challenge.Substring(i, eq - i).Trim();
                i = eq + 1;

                string value;
                if (i < challenge.Length && challenge[i] == '"')
                {
                    int endQuote = challenge.IndexOf('"', i + 1);
                    if (endQuote < 0) endQuote = challenge.Length;
                    value = challenge.Substring(i + 1, endQuote - i - 1);
                    i = endQuote + 1;
                }
                else
                {
                    int end = challenge.IndexOf(',', i);
                    if (end < 0) end = challenge.Length;
                    value = challenge.Substring(i, end - i).Trim();
                    i = end;
                }

                if (key.Length > 0)
                    result[key] = value;
            }
            return result;
        }

        private void UpdateAuthorization(string method, string? uri = null)
        {
            if (UserName == null || Password == null)
                return;

            string requestUri = uri ?? Uri?.AbsoluteUri ?? "/";

            if (_useBasicAuth)
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
                Authorization = $"Basic {token}";
                return;
            }

            if (Realm == null || Nonce == null)
                return;

            Authorization = BuildDigestAuthorization(method, requestUri);
        }

        /// <summary>
        /// 构造 Digest Authorization 头（支持 RFC 2617 qop=auth 与 RFC 7616 SHA-256）
        /// </summary>
        private string BuildDigestAuthorization(string method, string uri)
        {
            bool useSha256 = _authAlgorithm != null &&
                _authAlgorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase);
            Func<string, string> h = useSha256
                ? s => s.Sha256().ToLower()
                : s => s.Md532().ToLower();

            string ha1 = h($"{UserName}:{Realm}:{Password}");
            if (_authAlgorithm != null && _authAlgorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
            {
                // MD5-sess / SHA-256-sess
                ha1 = h($"{ha1}:{Nonce}:{_sessCnonce ??= Guid.NewGuid().ToString("N").Substring(0, 16)}");
            }
            string ha2 = h($"{method}:{uri}");

            var sb = new StringBuilder(256);
            sb.Append("Digest username=\"").Append(UserName)
              .Append("\", realm=\"").Append(Realm)
              .Append("\", nonce=\"").Append(Nonce)
              .Append("\", uri=\"").Append(uri).Append('"');

            // 服务器声明 qop 时必须携带 qop/nc/cnonce 参与摘要
            if (_authQop != null && _authQop.Split(',').Select(x => x.Trim())
                    .Contains("auth", StringComparer.OrdinalIgnoreCase))
            {
                string cnonce = Guid.NewGuid().ToString("N").Substring(0, 16);
                string nc = Interlocked.Increment(ref _nonceCount).ToString("x8");
                string response = ComputeDigestResponse(ha1, ha2, Nonce!, nc, cnonce, "auth", h);

                sb.Append(", qop=auth, nc=").Append(nc)
                  .Append(", cnonce=\"").Append(cnonce)
                  .Append("\", response=\"").Append(response).Append('"');
            }
            else
            {
                string response = ComputeDigestResponse(ha1, ha2, Nonce!, null, null, null, h);
                sb.Append(", response=\"").Append(response).Append('"');
            }

            if (_authOpaque != null)
                sb.Append(", opaque=\"").Append(_authOpaque).Append('"');
            if (_authAlgorithm != null)
                sb.Append(", algorithm=").Append(_authAlgorithm);

            return sb.ToString();
        }

        private string? _sessCnonce;

        /// <summary>
        /// RFC 2617/7616 摘要计算核心（qop 为 null 时按 RFC 2069 旧格式）。
        /// 独立出来便于用标准测试向量验证。
        /// </summary>
        public static string ComputeDigestResponse(
            string ha1, string ha2, string nonce, string? nc, string? cnonce, string? qop, Func<string, string> hash)
        {
            return qop == null
                ? hash($"{ha1}:{nonce}:{ha2}")
                : hash($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        }

        private void UpdateSessionFromResponse(RTSPResponse response)
        {
            try
            {
                var sessionHeader = response.Headers.FirstOrDefault(x =>
                    x.Key.Equals("Session", StringComparison.OrdinalIgnoreCase));
                if (sessionHeader.Value != null)
                {
                    var parts = sessionHeader.Value.Split(';');
                    SessionId = parts[0].Trim();

                    foreach (var part in parts)
                    {
                        if (part.Trim().StartsWith("timeout="))
                        {
                            if (int.TryParse(part.Trim().Substring(8), out int timeout))
                            {
                                Timeout = timeout;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastException = ex;
            }
        }

        private void SetState(RTSPConnectionState newState)
        {
            var oldState = State;
            State = newState;
            StateChanged?.Invoke(this, new RTSPConnectionStateChangedEventArgs(oldState, newState));
        }

        private void OnError(Exception ex)
        {
            Error?.Invoke(this, new RTSPErrorEventArgs(ex));
        }

        #endregion

        #region 认证方法

        public static void GetDigestParams(ref string realm, ref string nonce, string authHeader)
        {
            string[] authVal = authHeader.Remove(0, 7).Split(',');
            foreach (var item in authVal)
            {
                int splitIndex = item.IndexOf('=');
                if (splitIndex < 0) continue;

                string k = item.Substring(0, splitIndex).Trim();
                string v = item.Substring(splitIndex + 1).Trim().Trim('"');

                switch (k)
                {
                    case "realm": realm = v; break;
                    case "nonce": nonce = v; break;
                }
            }
        }

        public static string AuthorizationDigest(string username, string password, string uri, string realm, string nonce, string method)
        {
            string ha1 = $"{username}:{realm}:{password}".Md532().ToLower();
            string ha2 = $"{method}:{uri}".Md532().ToLower();
            string response = $"{ha1}:{nonce}:{ha2}".Md532().ToLower();

            return $"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }

        #endregion

        #region 音频发送

        /// <summary>
        /// 发送 G.711A 音频
        /// </summary>
        public async Task PlayAudio_G711A(byte[] audio, int fps, int sampleRate, long ssrc, byte channel = 0x00, Action<decimal, long>? progress = null, CancellationToken ct = default)
        {
            await PlayAudio_G711(audio, fps, sampleRate, RTPPayloadType.PCMA, ssrc, channel, progress, ct);
        }

        /// <summary>
        /// 发送 G.711 音频
        /// </summary>
        public async Task PlayAudio_G711(byte[] audio, int fps, int sampleRate, RTPPayloadType codecType, long ssrc, byte channel = 0x00, Action<decimal, long>? progress = null, CancellationToken ct = default)
        {
            int rtspHeaderLen = 4;
            int rtpHeaderLen = 12;
            int packSecLen = sampleRate / fps;
            int rtpPackLen = rtpHeaderLen + packSecLen;
            int audioLen = audio.Length;
            int packetHeaderLen = rtspHeaderLen + rtpHeaderLen;
            int packetIntervalMs = 1000 / fps;
            int totalPackets = audioLen / packSecLen;

            // 在循环外创建 Stopwatch，避免每次分配
            var sw = new Stopwatch();

            // RTP 时间戳必须是媒体时钟采样计数（每包 += 本包采样数），
            // 旧实现用 Unix 秒导致一秒内所有包时间戳相同，严格接收端会丢弃
            uint rtpTimestamp = (uint)Environment.TickCount;

            for (int i = 0; i < totalPackets; i++)
            {
                ct.ThrowIfCancellationRequested();

                sw.Restart();

                uint timestamp = rtpTimestamp;
                rtpTimestamp += (uint)packSecLen;
                byte[] packet = new byte[packetHeaderLen + packSecLen];

                // RTSP Interleaved Header
                packet[0] = 0x24; // Magic
                packet[1] = channel;
                packet[2] = (byte)((rtpPackLen >> 8) & 0xFF);
                packet[3] = (byte)(rtpPackLen & 0xFF);

                // RTP Header
                packet[4] = 0x80; // V=2, P=0, X=0, CC=0
                packet[5] = (byte)codecType;
                packet[6] = (byte)((i >> 8) & 0xFF);
                packet[7] = (byte)(i & 0xFF);
                packet[8] = (byte)((timestamp >> 24) & 0xFF);
                packet[9] = (byte)((timestamp >> 16) & 0xFF);
                packet[10] = (byte)((timestamp >> 8) & 0xFF);
                packet[11] = (byte)(timestamp & 0xFF);
                packet[12] = (byte)((ssrc >> 24) & 0xFF);
                packet[13] = (byte)((ssrc >> 16) & 0xFF);
                packet[14] = (byte)((ssrc >> 8) & 0xFF);
                packet[15] = (byte)(ssrc & 0xFF);

                Array.Copy(audio, i * packSecLen, packet, packetHeaderLen, packSecLen);
                await SendRawAsync(packet, ct);

                sw.Stop();
                int elapsed = (int)sw.ElapsedMilliseconds;
                progress?.Invoke((decimal)i / totalPackets, elapsed);

                int delay = packetIntervalMs - elapsed;
                if (delay > 0)
                {
                    await Task.Delay(delay, ct);
                }
            }
        }

        #endregion

        #region Dispose

        /// <summary>
        /// 异步释放：发送 TEARDOWN（限时）并优雅关闭。推荐使用此方法。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _userDisconnect = true;

            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            catch { }

            DisposeCore();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 同步释放：立即关闭连接，不发送 TEARDOWN、不等待任何网络 I/O。
        /// （旧实现同步等待 TEARDOWN 响应，最长可阻塞 15 秒且在有同步上下文的线程上会死锁）
        /// 需要优雅关闭请使用 <see cref="DisposeAsync"/> 或先调用 <see cref="DisconnectAsync"/>。
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (disposing)
            {
                _userDisconnect = true;
                StopKeepAlive();
                StopRtcp();
                try { _receiveCts?.Cancel(); } catch { }
                DisposeCore();

                if (State != RTSPConnectionState.Disconnected)
                {
                    SetState(RTSPConnectionState.Disconnected);
                }
            }
        }

        private void DisposeCore()
        {
            try { CleanupFacade(); } catch { }
            try { CleanupUdpTransports(); } catch { }
            try { _tcpStream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            try { _receiveCts?.Dispose(); } catch { }
            try { _keepAliveCts?.Dispose(); } catch { }
            try { _rtcpCts?.Dispose(); } catch { }
            _sendLock.Dispose();
            _tcpStream = null;
            _client = null;
        }

        #endregion
    }
}
