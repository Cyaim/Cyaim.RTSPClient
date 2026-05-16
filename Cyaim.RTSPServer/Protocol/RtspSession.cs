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

    private string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private RtspSessionState _state = RtspSessionState.Connected;
    private int _cseq;
    private readonly Dictionary<int, RtpStream> _rtpStreams = new();

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
    }

    /// <summary>
    /// 处理客户端请求
    /// </summary>
    public async Task ProcessAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var linkedCt = linkedCts.Token;

        var buffer = new byte[4096];
        var requestBuffer = new StringBuilder();

        try
        {
            while (!linkedCt.IsCancellationRequested && _client.Connected)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, linkedCt);
                if (bytesRead == 0)
                    break;

                string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                requestBuffer.Append(data);

                // 检查是否收到完整请求
                string accumulated = requestBuffer.ToString();
                int headerEnd = accumulated.IndexOf("\r\n\r\n");
                
                while (headerEnd >= 0)
                {
                    string requestText = accumulated[..headerEnd];
                    requestBuffer.Clear();
                    
                    // 检查是否有内容体
                    if (TryParseContentLength(requestText, out int contentLength))
                    {
                        int totalLength = headerEnd + 4 + contentLength;
                        if (accumulated.Length >= totalLength)
                        {
                            string content = accumulated[(headerEnd + 4)..totalLength];
                            requestBuffer.Append(accumulated[totalLength..]);
                            await ProcessRequestAsync(requestText, content, linkedCt);
                        }
                        else
                        {
                            requestBuffer.Append(accumulated[headerEnd..]);
                            break;
                        }
                    }
                    else
                    {
                        requestBuffer.Append(accumulated[(headerEnd + 4)..]);
                        await ProcessRequestAsync(requestText, null, linkedCt);
                    }

                    accumulated = requestBuffer.ToString();
                    headerEnd = accumulated.IndexOf("\r\n\r\n");
                }

                LastActivity = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Session error: {SessionId}", _sessionId);
        }
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
        _logger?.LogDebug("RTSP {Method} {Uri}", request.Method, request.Uri);

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

        await SendResponseAsync(response, ct);
    }

    private RtspRequest ParseRequest(string requestText, string? content)
    {
        var lines = requestText.Split("\r\n");
        var request = new RtspRequest { Content = content };

        if (lines.Length > 0)
        {
            var parts = lines[0].Split(' ', 3);
            if (parts.Length >= 3)
            {
                request.Method = parts[0];
                request.Uri = parts[1];
                request.Version = parts[2];
            }
        }

        for (int i = 1; i < lines.Length; i++)
        {
            int colonIndex = lines[i].IndexOf(':');
            if (colonIndex > 0)
            {
                string key = lines[i][..colonIndex].Trim();
                string value = lines[i][(colonIndex + 1)..].Trim();
                request.Headers[key] = value;

                if (key.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out int cseq);
                    request.CSeq = cseq;
                }
            }
        }

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

        // 获取流信息
        string path = request.Uri;
        var streamInfo = _streamManager.GetStream(path);

        if (streamInfo == null)
        {
            return CreateResponse(request.CSeq, 404, "Not Found");
        }

        // 生成 SDP
        string sdp = GenerateSdp(streamInfo);

        var describeResponse = CreateResponse(request.CSeq, 200, "OK");
        describeResponse.Headers["Content-Type"] = "application/sdp";
        describeResponse.Headers["Content-Length"] = sdp.Length.ToString();
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

        // 分配 RTP 端口/通道
        int trackId = _rtpStreams.Count;
        var rtpStream = new RtpStream
        {
            TrackId = trackId,
            Transport = transport,
            InterleavedChannel = trackId * 2
        };

        _rtpStreams[trackId] = rtpStream;

        var response = CreateResponse(request.CSeq, 200, "OK");
        response.Headers["Session"] = $"{_sessionId};timeout={_options.SessionTimeout}";
        response.Headers["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={rtpStream.InterleavedChannel}-{rtpStream.InterleavedChannel + 1}";

        return response;
    }

    private RtspResponse HandlePlay(RtspRequest request)
    {
        _state = RtspSessionState.Playing;

        // 开始发送 RTP 数据
        foreach (var stream in _rtpStreams.Values)
        {
            _ = Task.Run(() => SendRtpStreamAsync(stream, _cts.Token), _cts.Token);
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

    private async Task SendRtpStreamAsync(RtpStream stream, CancellationToken ct)
    {
        try
        {
            var streamInfo = _streamManager.GetStream("/");
            if (streamInfo == null) return;

            await foreach (var packet in _streamManager.GetRtpStreamAsync("/", ct))
            {
                if (_state != RtspSessionState.Playing || ct.IsCancellationRequested)
                    break;

                await SendRtpPacketAsync(stream, packet.Data, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "RTP stream error");
        }
    }

    private async Task SendRtpPacketAsync(RtpStream stream, byte[] data, CancellationToken ct)
    {
        // TCP interleaved 格式: $ + channel + length(2) + data
        byte[] header = new byte[4];
        header[0] = 0x24; // $
        header[1] = (byte)stream.InterleavedChannel;
        header[2] = (byte)((data.Length >> 8) & 0xFF);
        header[3] = (byte)(data.Length & 0xFF);

        await _stream.WriteAsync(header, ct);
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    #endregion

    #region 辅助方法

    private async Task SendResponseAsync(RtspResponse response, CancellationToken ct)
    {
        string responseText = response.ToString();
        byte[] responseBytes = Encoding.UTF8.GetBytes(responseText);
        await _stream.WriteAsync(responseBytes, ct);

        if (response.Content != null)
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(response.Content);
            await _stream.WriteAsync(contentBytes, ct);
        }

        await _stream.FlushAsync(ct);
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
        var sb = new StringBuilder();
        sb.AppendLine("v=0");
        sb.AppendLine($"o=- 0 0 IN IP4 {GetLocalIp()}");
        sb.AppendLine($"s={streamInfo.Name}");
        sb.AppendLine("c=IN IP4 0.0.0.0");
        sb.AppendLine("t=0 0");

        // 视频媒体
        if (streamInfo.VideoCodec != VideoCodecType.H264 || streamInfo.Width > 0)
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

            sb.AppendLine($"m=video 0 RTP/AVP {payloadType}");
            sb.AppendLine($"a=rtpmap:{payloadType} {codecName}/{streamInfo.ClockRate}");
            sb.AppendLine("a=control:trackID=0");
            sb.AppendLine("a=recvonly");
        }

        // 音频媒体
        if (streamInfo.AudioCodec != AudioCodecType.None)
        {
            int payloadType = streamInfo.AudioCodec switch
            {
                AudioCodecType.PCMA => 8,
                AudioCodecType.PCMU => 0,
                AudioCodecType.AAC => 97,
                _ => 8
            };

            string codecName = streamInfo.AudioCodec switch
            {
                AudioCodecType.PCMA => "PCMA",
                AudioCodecType.PCMU => "PCMU",
                AudioCodecType.AAC => "MPEG4-GENERIC",
                _ => "PCMA"
            };

            sb.AppendLine($"m=audio 0 RTP/AVP {payloadType}");
            sb.AppendLine($"a=rtpmap:{payloadType} {codecName}/{streamInfo.SampleRate}/{streamInfo.Channels}");
            sb.AppendLine("a=control:trackID=1");
            sb.AppendLine("a=recvonly");
        }

        return sb.ToString();
    }

    private TransportInfo ParseTransport(string header)
    {
        var info = new TransportInfo();
        var parts = header.Split(';');

        foreach (var part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Contains("TCP"))
                info.Protocol = TransportProtocol.Tcp;
            else if (trimmed.Contains("UDP"))
                info.Protocol = TransportProtocol.Udp;
            else if (trimmed.StartsWith("interleaved="))
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

    private string GetLocalIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
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

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Version} {StatusCode} {StatusMessage}");
        sb.AppendLine($"CSeq: {CSeq}");

        foreach (var header in Headers)
        {
            sb.AppendLine($"{header.Key}: {header.Value}");
        }

        sb.AppendLine();
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
    public uint Ssrc { get; set; }
    public ushort SequenceNumber { get; set; }
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
