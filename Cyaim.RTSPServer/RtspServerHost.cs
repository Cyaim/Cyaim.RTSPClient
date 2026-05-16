using System.Net;
using System.Net.Sockets;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Media;
using Cyaim.RTSPServer.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cyaim.RTSPServer;

/// <summary>
/// RTSP 服务器主机
/// 高性能处理大量并发连接
/// </summary>
public sealed class RtspServerHost : BackgroundService
{
    private readonly ILogger<RtspServerHost> _logger;
    private readonly RtspServerOptions _options;
    private readonly RtspProtocolHandler _protocolHandler;
    private readonly StreamManager _streamManager;
    private TcpListener? _listener;
    private readonly SemaphoreSlim _connectionSemaphore;

    public RtspServerHost(
        ILogger<RtspServerHost> logger,
        IOptions<RtspServerOptions> options,
        RtspProtocolHandler protocolHandler,
        StreamManager streamManager)
    {
        _logger = logger;
        _options = options.Value;
        _protocolHandler = protocolHandler;
        _streamManager = streamManager;
        _connectionSemaphore = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting RTSP Server on {Host}:{Port}", _options.Host, _options.Port);

        // 初始化流
        await _streamManager.InitializeAsync(stoppingToken);

        // 启动 TCP 监听
        var ip = IPAddress.Parse(_options.Host);
        _listener = new TcpListener(ip, _options.Port);
        _listener.Start();

        _logger.LogInformation("RTSP Server started. Listening on {Endpoint}", _listener.LocalEndpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 等待连接槽位
                await _connectionSemaphore.WaitAsync(stoppingToken);

                try
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    
                    // 异步处理连接，不阻塞主循环
                    _ = HandleClientAsync(client, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting connection");
                    _connectionSemaphore.Release();
                }
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("RTSP Server stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            // 配置 TCP 选项
            client.NoDelay = true;
            client.ReceiveBufferSize = 65536;
            client.SendBufferSize = 65536;

            await _protocolHandler.HandleClientAsync(client, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client");
        }
        finally
        {
            _connectionSemaphore.Release();
            client.Dispose();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping RTSP Server...");
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 获取服务器状态
    /// </summary>
    public RtspServerStatus GetStatus()
    {
        return new RtspServerStatus
        {
            IsRunning = _listener != null,
            Endpoint = _listener?.LocalEndpoint?.ToString(),
            ActiveConnections = _options.MaxConnections - _connectionSemaphore.CurrentCount,
            MaxConnections = _options.MaxConnections,
            ActiveSessions = _protocolHandler.ActiveSessionCount,
            StreamCount = _streamManager.GetAllStreams().Count()
        };
    }
}

/// <summary>
/// RTSP 服务器状态
/// </summary>
public class RtspServerStatus
{
    public bool IsRunning { get; set; }
    public string? Endpoint { get; set; }
    public int ActiveConnections { get; set; }
    public int MaxConnections { get; set; }
    public int ActiveSessions { get; set; }
    public int StreamCount { get; set; }
}
