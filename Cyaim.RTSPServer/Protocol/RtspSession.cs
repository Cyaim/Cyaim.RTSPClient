using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Media;
using Microsoft.Extensions.Logging;

namespace Cyaim.RTSPServer.Protocol;

/// <summary>
/// RTSP 会话
/// 处理单个客户端的 RTSP 通信
/// </summary>
public sealed class RtspSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly RtspServerOptions _options;
    private readonly StreamManager _streamManager;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _streamLock = new(1, 1);  // 序列化 TCP interleaved 写入

    private string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private RtspSessionState _state = RtspSessionState.Connected;
    private readonly Dictionary<int, RtpStream> _rtpStreams = new();
    private Task? _senderTask;  // 会话级 RTP 发送任务（单订阅，按 TrackId 分发）
    private string _currentStreamPath = "/";  // 当前请求的流路径
    private bool _wasPlaying;  // 是否曾经在播放状态（用于统计）
    private readonly IPAddress _clientIpAddress;

    public string SessionId => _sessionId;
    public string? ClientEndpoint => _client.Client.RemoteEndPoint?.ToString();
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public RtspSessionState State => _state;

    public RtspSession(TcpClient client, RtspServerOptions options, StreamManager streamManager, ILogger? logger = null)
    {
        _client = client;
        _stream = client.GetStream();
        _options = options;
        _streamManager = streamManager;
        _logger = logger;
        _clientIpAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
    }

    /// <summary>
    /// 处理客户端请求
    /// </summary>
    public async Task ProcessAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var linkedCt = linkedCts.Token;

        var buffer = new byte[65536];
        int buffered = 0;

        try
        {
            while (!linkedCt.IsCancellationRequested && _client.Connected)
            {
                // 读取数据到缓冲区
                int bytesRead = await _stream.ReadAsync(buffer, buffered, buffer.Length - buffered, linkedCt);
                if (bytesRead == 0)
                    break;

                buffered += bytesRead;
                int offset = 0;

                // 处理缓冲区中的所有数据
                while (offset < buffered)
                {
                    // 检查是否为 RTP interleaved 数据 (以 $ 开头)
                    if (buffer[offset] == 0x24)
                    {
                        // 检查是否有完整的 RTP 包头 (4 bytes)
                        if (buffered - offset < 4)
                            break; // 需要更多数据

                        int rtpLength = (buffer[offset + 2] << 8) | buffer[offset + 3];
                        int totalRtpLength = 4 + rtpLength;

                        // 检查是否有完整的 RTP 包
                        if (buffered - offset < totalRtpLength)
                            break; // 需要更多数据

                        // 处理 RTP 数据
                        byte channel = buffer[offset + 1];
                        byte[] rtpData = new byte[rtpLength];
                        Array.Copy(buffer, offset + 4, rtpData, 0, rtpLength);
                        HandleInterleavedData(channel, rtpData);

                        offset += totalRtpLength;
                    }
                    else
                    {
                        // RTSP 文本请求 - 字节级查找头部结束标记，避免整块解码字符串
                        int headerEnd = FindHeaderEnd(buffer, offset, buffered, out int headerEndLength);

                        if (headerEnd < 0)
                        {
                            // 没有找到完整的请求头，等待更多数据
                            break;
                        }

                        string requestText = Encoding.UTF8.GetString(buffer, offset, headerEnd - offset);

                        // 检查是否有内容体
                        if (TryParseContentLength(requestText, out int contentLength) && contentLength > 0)
                        {
                            int totalLength = (headerEnd - offset) + headerEndLength + contentLength;
                            if (buffered - offset < totalLength)
                                break; // 需要更多数据

                            string content = Encoding.UTF8.GetString(buffer, headerEnd + headerEndLength, contentLength);
                            await ProcessRequestAsync(requestText, content, linkedCt);
                            offset += totalLength;
                        }
                        else
                        {
                            await ProcessRequestAsync(requestText, null, linkedCt);
                            offset = headerEnd + headerEndLength;
                        }
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

                LastActivity = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            // 客户端断开连接（如播放器直接关闭）是正常情况，不作为错误记录
            _logger?.LogDebug(ex, "Session connection closed: {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Session error: {SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// 处理 TCP interleaved 数据 (RTP/RTCP)
    /// </summary>
    private void HandleInterleavedData(byte channel, byte[] data)
    {
        _logger?.LogDebug("Received interleaved data: channel={Channel}, length={Length}", 
            channel, data.Length);

        if (channel % 2 == 0)
        {
            HandleRtpData(channel, data);
        }
        else
        {
            HandleRtcpData(channel, data);
        }
    }

    /// <summary>
    /// 处理 RTP 数据
    /// </summary>
    private void HandleRtpData(byte channel, byte[] data)
    {
        _logger?.LogDebug("RTP data: channel={Channel}, size={Size}", channel, data.Length);
        // TODO: 可以在这里处理接收到的 RTP 数据（例如音频回传）
    }

    /// <summary>
    /// 处理 RTCP 数据
    /// </summary>
    private void HandleRtcpData(byte channel, byte[] data)
    {
        _logger?.LogDebug("RTCP data: channel={Channel}, size={Size}", channel, data.Length);
        // TODO: 处理 RTCP 反馈
    }

    /// <summary>
    /// 字节级查找头部结束标记（\r\n\r\n 或 \n\n）
    /// </summary>
    /// <returns>头部结束位置（buffer 内绝对偏移），未找到返回 -1</returns>
    private static int FindHeaderEnd(byte[] buffer, int offset, int end, out int markerLength)
    {
        for (int i = offset; i < end - 1; i++)
        {
            if (buffer[i] == (byte)'\r' && i + 3 < end &&
                buffer[i + 1] == (byte)'\n' && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                markerLength = 4;
                return i;
            }
            if (buffer[i] == (byte)'\n' && buffer[i + 1] == (byte)'\n')
            {
                markerLength = 2;
                return i;
            }
        }

        markerLength = 0;
        return -1;
    }

    private bool TryParseContentLength(string headers, out int contentLength)
    {
        contentLength = 0;
        var lines = headers.Split("\r\n");
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["Content-Length:".Length..].Trim();
                return int.TryParse(value, out contentLength);
            }
        }
        return false;
    }

    private async Task ProcessRequestAsync(string requestText, string? content, CancellationToken ct)
    {
        var request = ParseRequest(requestText, content);
        
        // 记录详细的客户端请求
        LogRequest(request, requestText);

        var response = request.Method.ToUpper() switch
        {
            "OPTIONS" => HandleOptions(request),
            "DESCRIBE" => await HandleDescribeAsync(request, ct),
            "SETUP" => await HandleSetupAsync(request, ct),
            "PLAY" => HandlePlay(request),
            "PAUSE" => HandlePause(request),
            "TEARDOWN" => HandleTeardown(request),
            "GET_PARAMETER" => HandleGetParameter(request),
            "SET_PARAMETER" => HandleSetParameter(request),
            _ => CreateResponse(request.CSeq, 405, "Method Not Allowed")
        };

        // 记录详细的服务端响应
        LogResponse(response);

        await SendResponseAsync(response, ct);
    }

    /// <summary>
    /// 记录客户端请求详情
    /// </summary>
    private void LogRequest(RtspRequest request, string rawRequest)
    {
        _logger?.LogInformation(
            "════════════════════════════════════════════════════════════\n" +
            "📤 CLIENT REQUEST [{Endpoint}]\n" +
            "──────────────────────────────────────────────────────────────\n" +
            "{Method} {Uri} {Version}\n" +
            "──────────────────────────────────────────────────────────────\n" +
            "{Headers}\n" +
            "{Content}" +
            "════════════════════════════════════════════════════════════",
            ClientEndpoint,
            request.Method, request.Uri, request.Version,
            FormatHeaders(request.Headers),
            !string.IsNullOrEmpty(request.Content) ? $"──────────────────────────────────────────────────────────────\n{request.Content}\n" : "");
    }

    /// <summary>
    /// 记录服务端响应详情
    /// </summary>
    private void LogResponse(RtspResponse response)
    {
        _logger?.LogInformation(
            "════════════════════════════════════════════════════════════\n" +
            "📥 SERVER RESPONSE\n" +
            "──────────────────────────────────────────────────────────────\n" +
            "{Version} {StatusCode} {StatusMessage}\n" +
            "──────────────────────────────────────────────────────────────\n" +
            "{Headers}\n" +
            "{Content}" +
            "════════════════════════════════════════════════════════════",
            response.Version, response.StatusCode, response.StatusMessage,
            FormatHeaders(response.Headers),
            !string.IsNullOrEmpty(response.Content) ? $"──────────────────────────────────────────────────────────────\n{response.Content}\n" : "");
    }

    /// <summary>
    /// 格式化头部为可读字符串
    /// </summary>
    private static string FormatHeaders(Dictionary<string, string> headers)
    {
        if (headers == null || headers.Count == 0)
            return "(no headers)";

        var sb = new StringBuilder();
        foreach (var header in headers)
        {
            sb.AppendLine($"  {header.Key}: {header.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private RtspRequest ParseRequest(string requestText, string? content)
    {
        // 统一换行符为 \r\n
        requestText = requestText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        
        var lines = requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var request = new RtspRequest { Content = content };

        if (lines.Length > 0)
        {
            // 解析请求行: OPTIONS rtsp://... RTSP/1.0
            var parts = lines[0].Split(' ', 3);
            if (parts.Length >= 3)
            {
                request.Method = parts[0];
                request.Uri = parts[1];
                request.Version = parts[2];
            }
            else if (parts.Length == 2)
            {
                request.Method = parts[0];
                request.Uri = parts[1];
            }
            else if (parts.Length == 1)
            {
                request.Method = parts[0];
            }
        }

        // 解析头部
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                string key = line[..colonIndex].Trim();
                string value = line[(colonIndex + 1)..].Trim();
                
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers[key] = value;

                    if (key.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(value, out int cseq);
                        request.CSeq = cseq;
                    }
                }
            }
        }

        _logger?.LogDebug("Parsed request: Method={Method}, Uri={Uri}, CSeq={CSeq}", 
            request.Method, request.Uri, request.CSeq);

        return request;
    }

    #region RTSP 方法处理

    private RtspResponse HandleOptions(RtspRequest request)
    {
        var response = CreateResponse(request.CSeq, 200, "OK");
        response.Headers["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER";
        return response;
    }

    private async Task<RtspResponse> HandleDescribeAsync(RtspRequest request, CancellationToken ct)
    {
        // 验证认证
        if (_options.EnableAuthentication && !ValidateAuth(request))
        {
            var response = CreateResponse(request.CSeq, 401, "Unauthorized");
            response.Headers["WWW-Authenticate"] = $"Digest realm=\"RTSP Server\", nonce=\"{GenerateNonce()}\"";
            return response;
        }

        // 获取流信息 - 从完整URL提取路径
        string path = ExtractPath(request.Uri);
        _currentStreamPath = path;  // 保存当前流路径
        var streamInfo = _streamManager.GetStream(path);

        if (streamInfo == null)
        {
            // 如果精确匹配失败，尝试模糊匹配
            streamInfo = _streamManager.GetAllStreams()
                .FirstOrDefault(s => path.Contains(s.Path) || s.Path.Contains(path));
        }

        if (streamInfo == null)
        {
            return CreateResponse(request.CSeq, 404, "Not Found");
        }

        // SPS/PPS 可能在源启动后才可用，DESCRIBE 前刷新一次
        _streamManager.RefreshStreamMetadata(streamInfo.Path);

        // 生成 SDP
        string sdp = GenerateSdp(streamInfo);
        int sdpByteCount = Encoding.UTF8.GetByteCount(sdp);

        var describeResponse = CreateResponse(request.CSeq, 200, "OK");
        describeResponse.Headers["Content-Type"] = "application/sdp";
        describeResponse.Headers["Content-Length"] = sdpByteCount.ToString();
        describeResponse.Headers["Content-Base"] = $"{request.Uri}/";
        describeResponse.Content = sdp;

        return describeResponse;
    }

    private async Task<RtspResponse> HandleSetupAsync(RtspRequest request, CancellationToken ct)
    {
        // 解析 Transport 头
        if (!request.Headers.TryGetValue("Transport", out string? transportHeader))
        {
            return CreateResponse(request.CSeq, 461, "Unsupported Transport");
        }

        // 解析传输模式
        var transport = ParseTransport(transportHeader);

        // 从 URI 解析 trackID（SDP a=control:trackID=N）；解析不到时按到达顺序分配
        int trackId = ParseTrackId(request.Uri) ?? _rtpStreams.Count;

        var rtpStream = new RtpStream
        {
            TrackId = trackId,
            Transport = transport
        };

        string transportResponse;

        if (transport.Protocol == TransportProtocol.Udp)
        {
            // UDP 模式
            int serverRtpPort = _options.RtpPortRangeStart + (trackId * 2);
            int serverRtcpPort = serverRtpPort + 1;

            rtpStream.InterleavedChannel = -1; // UDP 不使用 interleaved
            rtpStream.ServerRtpPort = serverRtpPort;
            rtpStream.ServerRtcpPort = serverRtcpPort;

            // 创建 UDP socket 用于发送 RTP 数据
            var rtpEndpoint = new IPEndPoint(_clientIpAddress, transport.ClientRtpPort);
            var rtpSocket = new UdpClient();
            rtpSocket.Client.Bind(new IPEndPoint(IPAddress.Any, serverRtpPort));
            rtpSocket.Connect(rtpEndpoint);
            rtpStream.RtpSocket = rtpSocket;
            rtpStream.ClientRtpEndpoint = rtpEndpoint;

            transportResponse = $"RTP/AVP;unicast;" +
                $"client_port={transport.ClientRtpPort}-{transport.ClientRtcpPort};" +
                $"server_port={serverRtpPort}-{serverRtcpPort}";

            _logger?.LogDebug("UDP transport: client={ClientRtp}-{ClientRtcp}, server={ServerRtp}-{ServerRtcp}",
                transport.ClientRtpPort, transport.ClientRtcpPort, serverRtpPort, serverRtcpPort);
        }
        else
        {
            // TCP Interleaved 模式：优先使用客户端请求的通道号
            rtpStream.InterleavedChannel = transport.InterleavedStart >= 0
                ? transport.InterleavedStart
                : trackId * 2;
            transportResponse = $"RTP/AVP/TCP;unicast;interleaved={rtpStream.InterleavedChannel}-{rtpStream.InterleavedChannel + 1}";
        }

        _rtpStreams[trackId] = rtpStream;

        var response = CreateResponse(request.CSeq, 200, "OK");
        response.Headers["Session"] = $"{_sessionId};timeout={_options.SessionTimeout}";
        response.Headers["Transport"] = transportResponse;

        return response;
    }

    private RtspResponse HandlePlay(RtspRequest request)
    {
        _state = RtspSessionState.Playing;

        // 统计活跃客户端
        if (!_wasPlaying)
        {
            _wasPlaying = true;
            _streamManager.IncrementActiveClients(_currentStreamPath);
        }

        // 单个发送任务订阅一次媒体源，按 TrackId 分发到各轨道
        // （此前每轨道各订阅一次，包被随机分到某个订阅后又因 TrackId 不匹配被丢弃）
        if (_senderTask == null || _senderTask.IsCompleted)
        {
            _senderTask = Task.Run(() => SendRtpSessionAsync(_cts.Token), _cts.Token);
        }

        var response = CreateResponse(request.CSeq, 200, "OK");
        response.Headers["Session"] = _sessionId;
        response.Headers["Range"] = "npt=0.000-";

        return response;
    }

    private RtspResponse HandlePause(RtspRequest request)
    {
        _state = RtspSessionState.Ready;
        return CreateResponse(request.CSeq, 200, "OK");
    }

    private RtspResponse HandleTeardown(RtspRequest request)
    {
        _state = RtspSessionState.Connected;

        // 清理 UDP sockets
        foreach (var rtpStream in _rtpStreams.Values)
        {
            rtpStream.RtpSocket?.Dispose();
        }
        _rtpStreams.Clear();

        var response = CreateResponse(request.CSeq, 200, "OK");
        response.Headers["Session"] = _sessionId;
        return response;
    }

    private RtspResponse HandleGetParameter(RtspRequest request)
    {
        return CreateResponse(request.CSeq, 200, "OK");
    }

    private RtspResponse HandleSetParameter(RtspRequest request)
    {
        return CreateResponse(request.CSeq, 200, "OK");
    }

    #endregion

    #region RTP 发送

    private async Task SendRtpSessionAsync(CancellationToken ct)
    {
        try
        {
            var streamInfo = _streamManager.GetStream(_currentStreamPath);
            if (streamInfo == null)
            {
                _logger?.LogWarning("Stream not found: {Path}", _currentStreamPath);
                return;
            }

            _logger?.LogInformation("Starting RTP session sender for {Path}, tracks=[{Tracks}]",
                _currentStreamPath, string.Join(",", _rtpStreams.Keys));

            long packetCount = 0;
            await foreach (var packet in _streamManager.GetRtpStreamAsync(_currentStreamPath, ct))
            {
                if (_state != RtspSessionState.Playing || ct.IsCancellationRequested)
                    break;

                // 按 TrackId 分发到已 SETUP 的轨道，未 SETUP 的轨道直接跳过
                if (!_rtpStreams.TryGetValue(packet.TrackId, out var stream))
                    continue;

                await SendRtpPacketAsync(stream, packet.Data, ct);
                packetCount++;

                if (packetCount % 1000 == 0)
                {
                    _logger?.LogDebug("Sent {Count} RTP packets, last size: {Size} bytes",
                        packetCount, packet.Data.Length);
                }
            }

            _logger?.LogInformation("RTP session sender ended for {Path}, sent {Count} packets",
                _currentStreamPath, packetCount);
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "RTP sender connection closed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "RTP stream error");
        }
    }

    private async Task SendRtpPacketAsync(RtpStream stream, byte[] data, CancellationToken ct)
    {
        int bytesSent;
        if (stream.RtpSocket != null)
        {
            await stream.RtpSocket.SendAsync(data, ct);
            bytesSent = data.Length;
        }
        else
        {
            // TCP interleaved: header+data 合并为单次 WriteAsync；缓冲区走 ArrayPool 避免逐包分配
            int frameLen = 4 + data.Length;
            byte[] frame = ArrayPool<byte>.Shared.Rent(frameLen);
            try
            {
                frame[0] = 0x24;
                frame[1] = (byte)stream.InterleavedChannel;
                frame[2] = (byte)((data.Length >> 8) & 0xFF);
                frame[3] = (byte)(data.Length & 0xFF);
                Array.Copy(data, 0, frame, 4, data.Length);

                await _streamLock.WaitAsync(ct);
                try
                {
                    await _stream.WriteAsync(frame.AsMemory(0, frameLen), ct);
                }
                finally
                {
                    _streamLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
            bytesSent = frameLen;
        }

        if (!string.IsNullOrEmpty(_currentStreamPath))
            _streamManager.RecordBytesSent(_currentStreamPath, bytesSent);
    }

    #endregion

    #region 辅助方法

    private async Task SendResponseAsync(RtspResponse response, CancellationToken ct)
    {
        // 头部 + 内容体合并为一个缓冲区，并持有 _streamLock 写入，
        // 避免 PLAY 后 RTP interleaved 帧插入响应中间破坏客户端解析
        string responseText = response.ToString();
        byte[] responseBytes = response.Content == null
            ? Encoding.UTF8.GetBytes(responseText)
            : Encoding.UTF8.GetBytes(responseText + response.Content);

        _logger?.LogDebug("Sending {Length} bytes: {Data}", responseBytes.Length,
            Convert.ToHexString(responseBytes[..Math.Min(100, responseBytes.Length)]));

        await _streamLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(responseBytes, ct);
        }
        finally
        {
            _streamLock.Release();
        }

        _logger?.LogDebug("Response sent successfully");
    }

    private RtspResponse CreateResponse(int cseq, int statusCode, string statusMessage)
    {
        return new RtspResponse
        {
            Version = "RTSP/1.0",
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            CSeq = cseq,
            Headers = new Dictionary<string, string>()
        };
    }

    private bool ValidateAuth(RtspRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out string? auth))
            return false;

        // TODO: 实现 Digest 认证验证
        return true;
    }

    private string GenerateNonce()
    {
        return Guid.NewGuid().ToString("N");
    }

    private string GenerateSdp(StreamInfo streamInfo)
    {
        const string CRLF = "\r\n";
        var sb = new StringBuilder();
        
        sb.Append("v=0").Append(CRLF);
        sb.Append($"o=- 0 0 IN IP4 {GetLocalIp()}").Append(CRLF);
        sb.Append($"s={streamInfo.Name}").Append(CRLF);
        sb.Append("c=IN IP4 0.0.0.0").Append(CRLF);
        sb.Append("t=0 0").Append(CRLF);

        // 视频媒体
        {
            int payloadType = streamInfo.VideoCodec switch
            {
                VideoCodecType.H264 => 96,
                VideoCodecType.H265 => 97,
                _ => 96
            };

            string codecName = streamInfo.VideoCodec switch
            {
                VideoCodecType.H264 => "H264",
                VideoCodecType.H265 => "H265",
                _ => "H264"
            };

            sb.Append($"m=video 0 RTP/AVP {payloadType}").Append(CRLF);
            sb.Append($"a=rtpmap:{payloadType} {codecName}/{streamInfo.ClockRate}").Append(CRLF);
            sb.Append("a=control:trackID=0").Append(CRLF);
            sb.Append("a=sendonly").Append(CRLF);
            
            // 添加 sprop-parameter-sets（SPS/PPS base64 编码）
            if (streamInfo.SpsData != null && streamInfo.PpsData != null)
            {
                string spsBase64 = Convert.ToBase64String(streamInfo.SpsData);
                string ppsBase64 = Convert.ToBase64String(streamInfo.PpsData);
                sb.Append($"a=fmtp:{payloadType} packetization-mode=1;sprop-parameter-sets={spsBase64},{ppsBase64}").Append(CRLF);
            }
        }

        // 音频媒体
        if (streamInfo.AudioCodec != AudioCodecType.None)
        {
            int payloadType = streamInfo.AudioCodec switch
            {
                AudioCodecType.PCMA => 8,
                AudioCodecType.PCMU => 0,
                AudioCodecType.AAC => 97,
                AudioCodecType.OPUS => 102,
                _ => 8
            };

            string codecName = streamInfo.AudioCodec switch
            {
                AudioCodecType.PCMA => "PCMA",
                AudioCodecType.PCMU => "PCMU",
                AudioCodecType.AAC => "MPEG4-GENERIC",
                AudioCodecType.OPUS => "OPUS",
                _ => "PCMA"
            };

            sb.Append($"m=audio 0 RTP/AVP {payloadType}").Append(CRLF);
            sb.Append($"a=rtpmap:{payloadType} {codecName}/{streamInfo.SampleRate}/{streamInfo.Channels}").Append(CRLF);

            // AAC fmtp for RFC 3640 (required for proper decoding)
            if (streamInfo.AudioCodec == AudioCodecType.AAC)
            {
                // 优先使用媒体源提取的真实 AudioSpecificConfig，与码流完全一致
                string asc = streamInfo.AacAudioSpecificConfig is { Length: > 0 } realAsc
                    ? Convert.ToHexString(realAsc)
                    : ComputeAacAudioSpecificConfig(streamInfo.SampleRate, streamInfo.Channels);
                sb.Append($"a=fmtp:{payloadType} streamtype=5; profile-level-id=1; ")
                  .Append($"mode=AAC-hbr; config={asc}; ")
                  .Append("sizeLength=13; indexLength=3; indexDeltaLength=3")
                  .Append(CRLF);
            }

            sb.Append("a=control:trackID=1").Append(CRLF);
            sb.Append("a=sendonly").Append(CRLF);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 计算 AAC AudioSpecificConfig（2 字节 hex）
    /// 5位 objectType(2=AAC-LC) + 4位 采样率索引 + 4位 声道数 + 3位 0
    /// </summary>
    private static string ComputeAacAudioSpecificConfig(int sampleRate, int channels)
    {
        ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int freqIndex = rates.IndexOf(sampleRate);
        if (freqIndex < 0) freqIndex = 4; // 默认 44100

        int channelConfig = Math.Clamp(channels, 1, 7);
        int config = (2 << 11) | (freqIndex << 7) | (channelConfig << 3);
        return config.ToString("X4");
    }

    /// <summary>
    /// 从 SETUP URI 中解析 trackID（如 rtsp://host/stream/trackID=1）
    /// </summary>
    private static int? ParseTrackId(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return null;

        int index = uri.LastIndexOf("trackID=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        int start = index + "trackID=".Length;
        int end = start;
        while (end < uri.Length && char.IsDigit(uri[end]))
            end++;

        return end > start && int.TryParse(uri[start..end], out int trackId) ? trackId : null;
    }

    private TransportInfo ParseTransport(string header)
    {
        var info = new TransportInfo
        {
            InterleavedStart = -1,  // -1 = 客户端未指定
            InterleavedEnd = -1
        };
        var parts = header.Split(';');

        // 判断协议类型
        // RTP/AVP = UDP (默认)
        // RTP/AVP/TCP = TCP interleaved
        if (header.Contains("TCP"))
            info.Protocol = TransportProtocol.Tcp;
        else
            info.Protocol = TransportProtocol.Udp;

        foreach (var part in parts)
        {
            string trimmed = part.Trim();
            
            if (trimmed.StartsWith("interleaved="))
            {
                var channels = trimmed.Split('=')[1].Split('-');
                if (channels.Length >= 2)
                {
                    info.InterleavedStart = int.Parse(channels[0]);
                    info.InterleavedEnd = int.Parse(channels[1]);
                }
            }
            else if (trimmed.StartsWith("client_port="))
            {
                var ports = trimmed.Split('=')[1].Split('-');
                if (ports.Length >= 2)
                {
                    info.ClientRtpPort = int.Parse(ports[0]);
                    info.ClientRtcpPort = int.Parse(ports[1]);
                }
            }
        }

        return info;
    }

    private static string? _cachedLocalIp;

    private static string GetLocalIp()
    {
        // DNS 查询较慢且结果稳定，缓存避免每次 DESCRIBE 都同步阻塞
        if (_cachedLocalIp != null)
            return _cachedLocalIp;

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            _cachedLocalIp = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            _cachedLocalIp = "127.0.0.1";
        }
        return _cachedLocalIp;
    }

    /// <summary>
    /// 从RTSP URI中提取路径
    /// rtsp://host:port/path -> /path
    /// /path -> /path
    /// </summary>
    private string ExtractPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "/";

        // 如果是完整URL，提取路径部分
        if (uri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = new Uri(uri);
                return parsed.AbsolutePath;
            }
            catch
            {
                // URI解析失败，尝试手动提取
                int pathStart = uri.IndexOf('/', 8); // 跳过 "rtsp://"
                if (pathStart >= 0)
                    return uri[pathStart..];
                return "/";
            }
        }

        // 已经是路径格式
        return uri.StartsWith("/") ? uri : "/" + uri;
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

        // 统计：减少活跃客户端
        if (_wasPlaying && !string.IsNullOrEmpty(_currentStreamPath))
        {
            _streamManager.DecrementActiveClients(_currentStreamPath);
        }

        // 清理 UDP sockets
        foreach (var rtpStream in _rtpStreams.Values)
        {
            rtpStream.RtpSocket?.Dispose();
        }
        _rtpStreams.Clear();

        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>
/// RTSP 请求
/// </summary>
public class RtspRequest
{
    public string Method { get; set; } = "";
    public string Uri { get; set; } = "";
    public string Version { get; set; } = "RTSP/1.0";
    public int CSeq { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Content { get; set; }
}

/// <summary>
/// RTSP 响应
/// </summary>
public class RtspResponse
{
    public string Version { get; set; } = "RTSP/1.0";
    public int StatusCode { get; set; }
    public string StatusMessage { get; set; } = "";
    public int CSeq { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Content { get; set; }

    private const string CRLF = "\r\n";

    public override string ToString()
    {
        var sb = new StringBuilder();
        
        // 状态行: RTSP/1.0 200 OK\r\n
        sb.Append(Version);
        sb.Append(' ');
        sb.Append(StatusCode);
        sb.Append(' ');
        sb.Append(StatusMessage);
        sb.Append(CRLF);

        // CSeq 头
        sb.Append("CSeq: ");
        sb.Append(CSeq);
        sb.Append(CRLF);

        // 其他头部
        foreach (var header in Headers)
        {
            sb.Append(header.Key);
            sb.Append(": ");
            sb.Append(header.Value);
            sb.Append(CRLF);
        }

        // 空行结束头部
        sb.Append(CRLF);

        return sb.ToString();
    }
}

/// <summary>
/// RTP 流信息
/// </summary>
public class RtpStream
{
    public int TrackId { get; set; }
    public TransportInfo Transport { get; set; } = new();
    public int InterleavedChannel { get; set; }
    public int ServerRtpPort { get; set; }
    public int ServerRtcpPort { get; set; }
    public uint Ssrc { get; set; }
    public ushort SequenceNumber { get; set; }
    /// <summary>
    /// UDP socket for RTP sending (null for TCP interleaved mode)
    /// </summary>
    public System.Net.Sockets.UdpClient? RtpSocket { get; set; }
    /// <summary>
    /// Client endpoint for UDP sending
    /// </summary>
    public System.Net.IPEndPoint? ClientRtpEndpoint { get; set; }
}

/// <summary>
/// 传输信息
/// </summary>
public class TransportInfo
{
    public TransportProtocol Protocol { get; set; }
    public int InterleavedStart { get; set; }
    public int InterleavedEnd { get; set; }
    public int ClientRtpPort { get; set; }
    public int ClientRtcpPort { get; set; }
    public int ServerRtpPort { get; set; }
    public int ServerRtcpPort { get; set; }
}

public enum TransportProtocol
{
    Tcp,
    Udp
}

/// <summary>
/// RTSP 会话状态
/// </summary>
public enum RtspSessionState
{
    Connected,
    Ready,
    Playing,
    Recording,
    Error
}
