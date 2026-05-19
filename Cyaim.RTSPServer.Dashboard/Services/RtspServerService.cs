using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Media;
using Cyaim.RTSPServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cyaim.RTSPServer.Dashboard.Services;

/// <summary>
/// RTSP 服务器管理服务
/// 提供完整的服务器控制和监控功能
/// </summary>
public class RtspServerService : IDisposable
{
    private readonly ILogger<RtspServerService> _logger;
    private readonly RtspServerOptions _options;
    private readonly StreamManager _streamManager;
    private readonly RtspProtocolHandler _protocolHandler;
    private readonly RtspLogCapture _logCapture;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private readonly object _lock = new();
    private readonly ObservableCollection<StreamViewModel> _streams = new();
    private readonly ObservableCollection<ClientViewModel> _clients = new();
    private readonly ObservableCollection<LogEntry> _logs = new();
    private DateTime _startTime;
    private long _totalBytesSent;
    private long _totalConnections;

    /// <summary>
    /// 记录发送的字节数
    /// </summary>
    public void RecordBytesSent(long bytes)
    {
        Interlocked.Add(ref _totalBytesSent, bytes);
    }

    public event EventHandler<ServerStatusEventArgs>? StatusChanged;
    public event EventHandler<LogEntry>? LogReceived;

    public bool IsRunning { get; private set; }
    public ObservableCollection<StreamViewModel> Streams => _streams;
    public ObservableCollection<ClientViewModel> Clients => _clients;
    public ObservableCollection<LogEntry> Logs => _logs;

    public RtspServerService(ILogger<RtspServerService> logger, IOptions<RtspServerOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        // 创建日志捕获器
        _logCapture = new RtspLogCapture();
        _logCapture.LogReceived += (sender, log) =>
        {
            _logs.Add(log);
            LogReceived?.Invoke(this, log);
        };

        // 使用自定义 LoggerFactory 来捕获 RTSP 协议日志
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new RtspLogCaptureProvider(_logCapture));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        _streamManager = new StreamManager(
            loggerFactory.CreateLogger<StreamManager>(),
            options);
        _protocolHandler = new RtspProtocolHandler(
            loggerFactory.CreateLogger<RtspProtocolHandler>(),
            options, _streamManager);

        // 加载配置的流
        LoadConfiguredStreams();
    }

    private void LoadConfiguredStreams()
    {
        foreach (var config in _options.Streams)
        {
            _streams.Add(new StreamViewModel
            {
                Path = config.Path,
                Name = config.Name,
                Description = config.Description ?? "",
                SourceType = config.SourceType.ToString(),
                VideoCodec = config.VideoCodec.ToString(),
                Resolution = $"{config.Width}x{config.Height}",
                Framerate = config.Framerate,
                Status = "Ready",
                ActiveClients = 0
            });
        }
    }

    /// <summary>
    /// 启动服务器
    /// </summary>
    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (IsRunning) return;
            IsRunning = true;
            _startTime = DateTime.Now;
            _cts = new CancellationTokenSource();
        }

        AddLog("Server starting...", LogLevel.Information);

        try
        {
            // 初始化流管理器
            await _streamManager.InitializeAsync(_cts.Token);

            // 启动服务器
            _serverTask = Task.Run(async () =>
            {
                // 创建 TCP 监听器
                var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Parse(_options.Host), _options.Port);
                listener.Start();

                AddLog($"Server listening on {_options.Host}:{_options.Port}", LogLevel.Information);
                StatusChanged?.Invoke(this, new ServerStatusEventArgs(true));

                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(_cts.Token);
                        Interlocked.Increment(ref _totalConnections);

                        _ = HandleClientAsync(client, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    listener.Stop();
                }
            }, _cts.Token);

            // 启动统计更新
            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                    UpdateStatistics();
                }
            }, _cts.Token);

            AddLog("Server started successfully", LogLevel.Information);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            AddLog($"Failed to start server: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    /// <summary>
    /// 停止服务器
    /// </summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            IsRunning = false;
        }

        AddLog("Server stopping...", LogLevel.Information);

        _cts?.Cancel();

        if (_serverTask != null)
        {
            try
            {
                await Task.WhenAny(_serverTask, Task.Delay(5000));
            }
            catch { }
        }

        _clients.Clear();
        StatusChanged?.Invoke(this, new ServerStatusEventArgs(false));
        AddLog("Server stopped", LogLevel.Information);
    }

    /// <summary>
    /// 重启服务器
    /// </summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(1000);
        await StartAsync();
    }

    /// <summary>
    /// 添加流
    /// </summary>
    public async Task<bool> AddStreamAsync(StreamConfig config)
    {
        try
        {
            var stream = await _streamManager.CreateStreamAsync(config, _cts?.Token ?? CancellationToken.None);

            _streams.Add(new StreamViewModel
            {
                Path = config.Path,
                Name = config.Name,
                Description = config.Description ?? "",
                SourceType = config.SourceType.ToString(),
                VideoCodec = config.VideoCodec.ToString(),
                Resolution = $"{config.Width}x{config.Height}",
                Framerate = config.Framerate,
                Status = "Ready",
                ActiveClients = 0
            });

            AddLog($"Stream added: {config.Path} ({config.Name})", LogLevel.Information);
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"Failed to add stream: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 删除流
    /// </summary>
    public async Task<bool> RemoveStreamAsync(string path)
    {
        try
        {
            await _streamManager.RemoveStreamAsync(path, _cts?.Token ?? CancellationToken.None);

            var stream = _streams.FirstOrDefault(s => s.Path == path);
            if (stream != null)
                _streams.Remove(stream);

            AddLog($"Stream removed: {path}", LogLevel.Information);
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"Failed to remove stream: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 获取服务器状态
    /// </summary>
    public ServerStatistics GetStatistics()
    {
        return new ServerStatistics
        {
            IsRunning = IsRunning,
            StartTime = _startTime,
            Uptime = IsRunning ? DateTime.Now - _startTime : TimeSpan.Zero,
            TotalConnections = _totalConnections,
            ActiveConnections = _clients.Count,
            ActiveStreams = _streams.Count(s => s.Status == "Streaming"),
            TotalStreams = _streams.Count,
            TotalBytesSent = _totalBytesSent,
            BandwidthMbps = CalculateBandwidth()
        };
    }

    private async Task HandleClientAsync(System.Net.Sockets.TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var clientVm = new ClientViewModel
        {
            Endpoint = endpoint,
            ConnectedAt = DateTime.Now,
            Status = "Connected"
        };

        _clients.Add(clientVm);
        AddLog($"Client connected: {endpoint}", LogLevel.Information);

        try
        {
            await _protocolHandler.HandleClientAsync(client, ct);
        }
        catch (Exception ex)
        {
            AddLog($"Client error ({endpoint}): {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            _clients.Remove(clientVm);
            client.Dispose();
            AddLog($"Client disconnected: {endpoint}", LogLevel.Information);
        }
    }

    private void UpdateStatistics()
    {
        // 更新流状态
        foreach (var stream in _streams)
        {
            var info = _streamManager.GetStream(stream.Path);
            if (info != null)
            {
                stream.ActiveClients = info.ActiveClients;
                stream.Status = info.ActiveClients > 0 ? "Streaming" : "Ready";
            }
        }
    }

    private double CalculateBandwidth()
    {
        // 简化的带宽计算
        return _clients.Count * 2.5; // 假设每个客户端 2.5 Mbps
    }

    private void AddLog(string message, LogLevel level)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        };

        _logs.Add(entry);

        // 限制日志数量
        while (_logs.Count > 5000)
            _logs.RemoveAt(0);

        LogReceived?.Invoke(this, entry);
        _logger.Log(level, message);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _protocolHandler?.Dispose();
        _streamManager?.Dispose();
        _logCapture?.Dispose();
    }
}

#region RTSP 日志捕获

/// <summary>
/// RTSP 日志捕获器
/// </summary>
public class RtspLogCapture : IDisposable
{
    public event EventHandler<LogEntry>? LogReceived;

    public void Capture(string message, LogLevel level)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        };
        LogReceived?.Invoke(this, entry);
    }

    public void Dispose() { }
}

/// <summary>
/// RTSP 日志捕获 Provider
/// </summary>
public class RtspLogCaptureProvider : ILoggerProvider
{
    private readonly RtspLogCapture _capture;

    public RtspLogCaptureProvider(RtspLogCapture capture)
    {
        _capture = capture;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RtspLogCaptureLogger(_capture, categoryName);
    }

    public void Dispose() { }
}

/// <summary>
/// RTSP 日志捕获 Logger
/// </summary>
public class RtspLogCaptureLogger : ILogger
{
    private readonly RtspLogCapture _capture;
    private readonly string _categoryName;

    public RtspLogCaptureLogger(RtspLogCapture capture, string categoryName)
    {
        _capture = capture;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        
        // 只捕获 RTSP 协议相关的日志
        if (_categoryName.Contains("Protocol") || _categoryName.Contains("Rtsp") || 
            message.Contains("CLIENT REQUEST") || message.Contains("SERVER RESPONSE"))
        {
            _capture.Capture(message, logLevel);
        }
    }
}

#endregion

#region View Models

public class StreamViewModel
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public string Resolution { get; set; } = "";
    public int Framerate { get; set; }
    public string Status { get; set; } = "Ready";
    public int ActiveClients { get; set; }
}

public class ClientViewModel
{
    public string Endpoint { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public string Status { get; set; } = "";
    public long BytesSent { get; set; }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
}

#endregion

#region Statistics

public class ServerStatistics
{
    public bool IsRunning { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime { get; set; }
    public long TotalConnections { get; set; }
    public int ActiveConnections { get; set; }
    public int ActiveStreams { get; set; }
    public int TotalStreams { get; set; }
    public long TotalBytesSent { get; set; }
    public double BandwidthMbps { get; set; }
}

public class ServerStatusEventArgs : EventArgs
{
    public bool IsRunning { get; }

    public ServerStatusEventArgs(bool isRunning)
    {
        IsRunning = isRunning;
    }
}

#endregion
