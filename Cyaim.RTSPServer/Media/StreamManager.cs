using System.Collections.Concurrent;
using Cyaim.RTSPServer.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cyaim.RTSPServer.Media;

/// <summary>
/// 流管理器
/// 管理所有媒体流的生命周期
/// </summary>
public sealed class StreamManager : IDisposable
{
    private readonly ILogger<StreamManager> _logger;
    private readonly RtspServerOptions _options;
    private readonly ConcurrentDictionary<string, StreamInfo> _streams = new();
    private readonly ConcurrentDictionary<string, IMediaSource> _sources = new();

    public StreamManager(ILogger<StreamManager> logger, IOptions<RtspServerOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// 初始化所有配置的流
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        foreach (var config in _options.Streams)
        {
            await CreateStreamAsync(config, ct);
        }

        _logger.LogInformation("Initialized {Count} streams", _streams.Count);
    }

    /// <summary>
    /// 创建流
    /// </summary>
    public async Task<StreamInfo> CreateStreamAsync(StreamConfig config, CancellationToken ct)
    {
        var streamInfo = new StreamInfo
        {
            Path = config.Path,
            Name = config.Name,
            Description = config.Description,
            VideoCodec = config.VideoCodec,
            AudioCodec = config.EnableAudio ? config.AudioCodec : AudioCodecType.None,
            Width = config.Width,
            Height = config.Height,
            Framerate = config.Framerate,
            ClockRate = 90000, // 视频默认
            SampleRate = config.AudioCodec switch
            {
                AudioCodecType.PCMA => 8000,
                AudioCodecType.PCMU => 8000,
                AudioCodecType.AAC => 44100,
                AudioCodecType.OPUS => 48000,
                _ => 8000
            },
            Channels = 1,
            CreatedAt = DateTime.UtcNow
        };

        _streams.TryAdd(config.Path, streamInfo);

        // 创建媒体源
        var source = CreateMediaSource(config);
        if (source != null)
        {
            _sources.TryAdd(config.Path, source);
            await source.StartAsync(ct);
            _logger.LogInformation("Stream created: {Path} ({Name})", config.Path, config.Name);
        }

        return streamInfo;
    }

    /// <summary>
    /// 获取流信息
    /// </summary>
    public StreamInfo? GetStream(string path)
    {
        return _streams.GetValueOrDefault(path);
    }

    /// <summary>
    /// 获取所有流
    /// </summary>
    public IEnumerable<StreamInfo> GetAllStreams()
    {
        return _streams.Values;
    }

    /// <summary>
    /// 删除流
    /// </summary>
    public async Task RemoveStreamAsync(string path, CancellationToken ct)
    {
        if (_sources.TryRemove(path, out var source))
        {
            await source.StopAsync(ct);
            source.Dispose();
        }

        _streams.TryRemove(path, out _);
        _logger.LogInformation("Stream removed: {Path}", path);
    }

    /// <summary>
    /// 获取流的 RTP 数据读取器
    /// </summary>
    public IAsyncEnumerable<RtpPacket> GetRtpStreamAsync(string path, CancellationToken ct)
    {
        if (_sources.TryGetValue(path, out var source))
        {
            return source.GetPacketsAsync(ct);
        }

        throw new InvalidOperationException($"Stream not found: {path}");
    }

    private IMediaSource? CreateMediaSource(StreamConfig config)
    {
        return config.SourceType switch
        {
            MediaSourceType.File => new FileMediaSource(config),
            MediaSourceType.RtspPull => new RtspPullMediaSource(config),
            MediaSourceType.TestPattern => new TestPatternMediaSource(config),
            _ => null
        };
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }
        _sources.Clear();
        _streams.Clear();
    }
}

/// <summary>
/// 流信息
/// </summary>
public class StreamInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public VideoCodecType VideoCodec { get; set; }
    public AudioCodecType AudioCodec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Framerate { get; set; }
    public int ClockRate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public DateTime CreatedAt { get; set; }
    public long TotalBytesSent { get; set; }
    public int ActiveClients { get; set; }
}

/// <summary>
/// RTP 数据包
/// </summary>
public class RtpPacket
{
    public byte[] Data { get; set; } = [];
    public int TrackId { get; set; }
    public uint Timestamp { get; set; }
    public ushort SequenceNumber { get; set; }
    public bool IsKeyFrame { get; set; }
}

/// <summary>
/// 媒体源接口
/// </summary>
public interface IMediaSource : IDisposable
{
    /// <summary>
    /// 启动源
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// 停止源
    /// </summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// 获取 RTP 数据包流
    /// </summary>
    IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct);
}

/// <summary>
/// 文件媒体源
/// </summary>
public class FileMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;

    public FileMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // TODO: 读取文件并生成 RTP 包
        while (_running && !ct.IsCancellationRequested)
        {
            yield return new RtpPacket
            {
                Data = new byte[1024],
                TrackId = 0,
                Timestamp = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF)
            };
            await Task.Delay(40, ct); // 25fps
        }
    }

    public void Dispose() { }
}

/// <summary>
/// RTSP 拉流源
/// </summary>
public class RtspPullMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;

    public RtspPullMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // TODO: 连接远程 RTSP 服务器并拉流
        while (_running && !ct.IsCancellationRequested)
        {
            yield return new RtpPacket
            {
                Data = Array.Empty<byte>(),
                TrackId = 0
            };
            await Task.Delay(40, ct);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// 测试图案源
/// </summary>
public class TestPatternMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;

    public TestPatternMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            // 生成测试图案 RTP 包
            yield return new RtpPacket
            {
                Data = GenerateTestFrame(),
                TrackId = 0,
                Timestamp = _timestamp,
                SequenceNumber = _sequenceNumber++,
                IsKeyFrame = _sequenceNumber % 100 == 0
            };

            _timestamp += (uint)(90000 / _config.Framerate);
            await Task.Delay(1000 / _config.Framerate, ct);
        }
    }

    private byte[] GenerateTestFrame()
    {
        // TODO: 生成真实的测试图案帧
        return new byte[1024];
    }

    public void Dispose() { }
}
