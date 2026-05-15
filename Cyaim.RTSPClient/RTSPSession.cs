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
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSP 会话管理类
    /// 支持完整的 RTSP 协议操作
    /// </summary>
    public class RTSPSession : IDisposable
    {
        #region 字段

        private TcpClient? _client;
        private NetworkStream? _tcpStream;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<RTSPResponse>> _pendingRequests = new();
        private readonly ConcurrentDictionary<int, RTSPResponse> _requestResults = new();
        private int _cseq;
        private bool _disposed;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;

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
            if (Uri == null)
                throw new InvalidOperationException("URI not set");

            try
            {
                SetState(RTSPConnectionState.Connecting);
                _client = new TcpClient(Uri.Host, Uri.Port);
                _tcpStream = _client.GetStream();
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

        private async Task ConnectInternalAsync(CancellationToken ct)
        {
            if (Uri == null)
                throw new InvalidOperationException("URI not set");

            try
            {
                SetState(RTSPConnectionState.Connecting);
                _client = new TcpClient();
                await _client.ConnectAsync(Uri.Host, Uri.Port);
                _tcpStream = _client.GetStream();
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

            SetState(RTSPConnectionState.Disconnecting);

            try
            {
                // 尝试发送 TEARDOWN
                if (Uri != null && SessionId != null)
                {
                    try
                    {
                        await TeardownAsync(ct: ct);
                    }
                    catch { }
                }

                StopReceiveLoop();
                _tcpStream?.Close();
                _client?.Close();
            }
            finally
            {
                _tcpStream = null;
                _client = null;
                SessionId = null;
                SDP = null;
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
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
        }

        private void StopReceiveLoop()
        {
            _receiveCts?.Cancel();
            _receiveTask?.Wait(TimeSpan.FromSeconds(5));
            _receiveCts?.Dispose();
            _receiveCts = null;
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            if (_tcpStream == null) return;

            var buffer = new byte[4096];

            try
            {
                while (!ct.IsCancellationRequested && _tcpStream != null)
                {
                    int bytesRead = await _tcpStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0)
                    {
                        // 服务器关闭连接
                        break;
                    }

                    // 检查是否为 RTP 数据 (以 $ 开头)
                    if (buffer[0] == 0x24 && bytesRead >= 4)
                    {
                        // TCP interleaved RTP 数据
                        byte channel = buffer[1];
                        int length = (buffer[2] << 8) | buffer[3];

                        if (bytesRead >= 4 + length)
                        {
                            var rtpData = new byte[length];
                            Array.Copy(buffer, 4, rtpData, 0, length);
                            OnDataReceived(rtpData, channel);
                        }
                    }
                    else
                    {
                        // RTSP 响应文本
                        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        var response = new RTSPResponse(msg, buffer);

                        // 完成等待的请求
                        if (_pendingRequests.TryRemove(response.CSeq, out var tcs))
                        {
                            tcs.TrySetResult(response);
                        }

                        _requestResults.AddOrUpdate(response.CSeq, response, (_, _) => response);
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
                if (State != RTSPConnectionState.Disconnecting &&
                    State != RTSPConnectionState.Disconnected)
                {
                    SetState(RTSPConnectionState.Disconnected);
                }
            }
        }

        private void OnDataReceived(byte[] data, byte channel)
        {
            var packet = new Rtp.RTPPacket(
                version: 2,
                padding: false,
                extension: false,
                csrcCount: 0,
                marker: false,
                payloadType: 0,
                sequenceNumber: 0,
                timestamp: 0,
                ssrc: 0,
                csrc: Array.Empty<uint>(),
                payload: data,
                trackId: channel / 2,
                streamType: channel % 2 == 0 ? StreamType.Video : StreamType.Audio,
                raw: data
            );
            DataReceived?.Invoke(this, new RtpDataReceivedEventArgs(packet));
        }

        #endregion

        #region 发送方法

        /// <summary>
        /// 发送原始数据
        /// </summary>
        public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
        {
            if (_tcpStream == null)
                throw new InvalidOperationException("Not connected");

            await _tcpStream.WriteAsync(data, 0, data.Length, ct);
        }

        /// <summary>
        /// 发送 RTSP 请求并等待响应
        /// </summary>
        public async Task<RTSPResponse> SendRequestAsync(RTSPRequest request, CancellationToken ct = default)
        {
            if (_tcpStream == null)
                throw new InvalidOperationException("Not connected");

            // 创建 TCS 用于等待响应
            var tcs = new TaskCompletionSource<RTSPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[request.CSeq] = tcs;

            // 发送请求
            string req = RTSPRequest.GetRequest(request);
            byte[] data = Encoding.UTF8.GetBytes(req);
            await _tcpStream.WriteAsync(data, 0, data.Length, ct);

            // 等待响应（带超时）
            using var timeoutCts = new CancellationTokenSource(WaitResponseTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                // 注册取消回调
                using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _pendingRequests.TryRemove(request.CSeq, out _);
                throw new TimeoutException($"RTSP response timeout for CSeq {request.CSeq}");
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
            var response = await SendRequestAsync(request, ct);

            if (response.StatusCode == "200")
            {
                var pub = response.Headers.FirstOrDefault(x => x.Key == "Public");
                Public = pub.Value;
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

            var response = await SendRequestAsync(request, ct);

            if (response.StatusCode == "200")
            {
                SDP = SDPParser.Parse(response.Response);
                if (useBackchannel)
                {
                    HasBackChannelSupported = SDP.GetBackChannel() != null;
                }
            }
            else if (response.StatusCode == "401")
            {
                // 需要认证
                HandleAuthChallenge(response);
            }

            return response;
        }

        /// <summary>
        /// SETUP - 设置媒体传输通道
        /// </summary>
        public async Task<RTSPResponse> SetupAsync(string channelUri, string transport, bool useBackchannel = false, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            var request = CreateRequest("SETUP");
            request.URI = Uri.AbsoluteUri + channelUri;
            request.Transport = transport;
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct);
            UpdateSessionFromResponse(response);

            return response;
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

            var response = await SendRequestAsync(request, ct);
            UpdateSessionFromResponse(response);

            if (response.StatusCode == "200")
            {
                SetState(RTSPConnectionState.Playing);
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
            request.URI = Uri.AbsoluteUri + (channelUri ?? "");
            request.Session = SessionId;
            if (useBackchannel)
                request.Require = OnvifBackChannel;

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            var response = await SendRequestAsync(request, ct);
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
                // 构建参数体
                var sb = new StringBuilder();
                foreach (var p in parameters)
                {
                    sb.AppendLine(p);
                }
                request.ContentLength = sb.Length.ToString();
            }

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct);
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
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            }
            request.ContentLength = sb.Length.ToString();

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct);
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
            request.ContentLength = sdpContent.Length.ToString();

            UpdateAuthorization(request.Method);
            request.Authorization = Authorization;

            return await SendRequestAsync(request, ct);
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

            // 1. OPTIONS
            var optionsResponse = await OptionsAsync(ct);
            if (optionsResponse.StatusCode != "200")
            {
                throw new Exception("OPTIONS request failed");
            }

            // 2. DESCRIBE
            var describeResponse = await DescribeAsync(useBackchannel, ct);

            if (describeResponse.StatusCode == "401")
            {
                // 需要 Digest 认证
                HandleAuthChallenge(describeResponse);

                // 重新发送 DESCRIBE
                var request = CreateRequest("DESCRIBE");
                request.Accept = "application/sdp";
                if (useBackchannel)
                    request.Require = OnvifBackChannel;

                UpdateAuthorization(request.Method);
                request.Authorization = Authorization;

                describeResponse = await SendRequestAsync(request, ct);

                if (describeResponse.StatusCode == "200")
                {
                    SDP = SDPParser.Parse(describeResponse.Response);
                }
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
                var response = await GetParameterAsync(ct: ct);
                sw.Stop();

                KeepAlive?.Invoke(this, new KeepAliveEventArgs(response.StatusCode == "200", (int)sw.ElapsedMilliseconds));
                return response.StatusCode == "200";
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
                CSeq = Interlocked.Increment(ref _cseq)
            };
        }

        private void HandleAuthChallenge(RTSPResponse response)
        {
            var auth = response.Headers.FirstOrDefault(x => x.Key == "WWW-Authenticate");
            if (auth.Value == null || !auth.Value.StartsWith("Digest"))
            {
                throw new Exception("Server auth mode not Digest");
            }

            string realm = "RTSP SERVER";
            string nonce = "";
            GetDigestParams(ref realm, ref nonce, auth.Value);

            Realm = realm;
            Nonce = nonce;
        }

        private void UpdateAuthorization(string method)
        {
            if (UserName != null && Password != null && Realm != null && Nonce != null && Uri != null)
            {
                Authorization = AuthorizationDigest(UserName, Password, Uri.AbsoluteUri, Realm, Nonce, method);
            }
        }

        private void UpdateSessionFromResponse(RTSPResponse response)
        {
            try
            {
                var sessionHeader = response.Headers.FirstOrDefault(x => x.Key == "Session");
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

            for (int i = 0; i < totalPackets; i++)
            {
                ct.ThrowIfCancellationRequested();

                sw.Restart();

                long timestamp = Timestamp.GetNowTimestamp();
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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        DisconnectAsync().GetAwaiter().GetResult();
                    }
                    catch { }

                    _receiveCts?.Dispose();
                    _tcpStream?.Dispose();
                    _client?.Dispose();
                }

                _disposed = true;
            }
        }

        #endregion
    }
}
