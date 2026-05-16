using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cyaim.RTSPServer.Protocol;

/// <summary>
/// RTSP 协议处理器
/// 处理 RTSP 请求和响应
/// </summary>
public sealed class RtspProtocolHandler : IDisposable
{
    private readonly ILogger<RtspProtocolHandler> _logger;
    private readonly RtspServerOptions _options;
    private readonly ConcurrentDictionary<string, RtspSession> _sessions = new();
    private readonly StreamManager _streamManager;

    public RtspProtocolHandler(
        ILogger<RtspProtocolHandler> logger,
        IOptions<RtspServerOptions> options,
        StreamManager streamManager)
    {
        _logger = logger;
        _options = options.Value;
        _streamManager = streamManager;
    }

    /// <summary>
    /// 处理客户端连接
    /// </summary>
    public async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        _logger.LogInformation("Client connected: {Endpoint}", endpoint);

        var session = new RtspSession(client, _options, _streamManager, _logger);
        _sessions.TryAdd(session.SessionId, session);

        try
        {
            await session.ProcessAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client session error: {SessionId}", session.SessionId);
        }
        finally
        {
            _sessions.TryRemove(session.SessionId, out _);
            session.Dispose();
            _logger.LogInformation("Client disconnected: {Endpoint}", endpoint);
        }
    }

    /// <summary>
    /// 获取活跃会话数
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// 获取所有会话信息
    /// </summary>
    public IEnumerable<RtspSessionInfo> GetSessions()
    {
        return _sessions.Values.Select(s => new RtspSessionInfo
        {
            SessionId = s.SessionId,
            ClientEndpoint = s.ClientEndpoint,
            ConnectedAt = s.ConnectedAt,
            LastActivity = s.LastActivity,
            State = s.State
        });
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}

/// <summary>
/// RTSP 会话信息
/// </summary>
public class RtspSessionInfo
{
    public string SessionId { get; init; } = "";
    public string? ClientEndpoint { get; init; }
    public DateTime ConnectedAt { get; init; }
    public DateTime LastActivity { get; init; }
    public RtspSessionState State { get; init; }
}
