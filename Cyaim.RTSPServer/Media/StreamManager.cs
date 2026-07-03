using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private long _totalBytesSent;

    /// <summary>
    /// 所有流累计发送的字节数
    /// </summary>
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

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
            await CreateStreamAsync(config, ct, autoStart: false);
        }

        _logger.LogInformation("Initialized {Count} streams", _streams.Count);
    }

    /// <summary>
    /// 创建流
    /// </summary>
    /// <param name="config">流配置</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="autoStart">是否自动启动媒体源</param>
    public async Task<StreamInfo> CreateStreamAsync(StreamConfig config, CancellationToken ct, bool autoStart = true)
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
            CreatedAt = DateTime.UtcNow,
            IsRunning = false  // 默认不运行
        };

        _streams.TryAdd(config.Path, streamInfo);

        // 创建媒体源
        var source = CreateMediaSource(config);
        if (source != null)
        {
            _sources.TryAdd(config.Path, source);
            
            if (autoStart)
            {
                await source.StartAsync(ct);
                streamInfo.IsRunning = true;
                
                // 从媒体源获取 SPS/PPS 并保存到 StreamInfo
                if (source.SpsData != null)
                {
                    streamInfo.SpsData = source.SpsData;
                }
                if (source.PpsData != null)
                {
                    streamInfo.PpsData = source.PpsData;
                }
            }
            
            _logger.LogInformation("Stream created: {Path} ({Name}), AutoStart={AutoStart}", 
                config.Path, config.Name, autoStart);
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
    /// 启动指定流的媒体源
    /// </summary>
    public async Task<bool> StartStreamAsync(string path, CancellationToken ct)
    {
        if (_sources.TryGetValue(path, out var source) && _sources.ContainsKey(path))
        {
            await source.StartAsync(ct);
            
            // 更新 StreamInfo
            if (_streams.TryGetValue(path, out var streamInfo))
            {
                streamInfo.IsRunning = true;
                
                // 获取 SPS/PPS（可能在启动后才可用）
                if (source.SpsData != null)
                    streamInfo.SpsData = source.SpsData;
                if (source.PpsData != null)
                    streamInfo.PpsData = source.PpsData;
            }
            
            _logger.LogInformation("Stream started: {Path}", path);
            return true;
        }

        _logger.LogWarning("Stream not found for start: {Path}", path);
        return false;
    }

    /// <summary>
    /// 停止指定流的媒体源
    /// </summary>
    public async Task<bool> StopStreamAsync(string path, CancellationToken ct)
    {
        if (_sources.TryGetValue(path, out var source) && _streams.TryGetValue(path, out var streamInfo))
        {
            await source.StopAsync(ct);
            streamInfo.IsRunning = false;
            _logger.LogInformation("Stream stopped: {Path}", path);
            return true;
        }

        _logger.LogWarning("Stream not found for stop: {Path}", path);
        return false;
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
    /// 记录流发送的字节数
    /// </summary>
    public void RecordBytesSent(string path, int bytes)
    {
        Interlocked.Add(ref _totalBytesSent, bytes);
        if (_streams.TryGetValue(path, out var streamInfo))
        {
            streamInfo.TotalBytesSent += bytes;
        }
    }

    /// <summary>
    /// 增加流的活跃客户端计数
    /// </summary>
    public void IncrementActiveClients(string path)
    {
        if (_streams.TryGetValue(path, out var streamInfo))
        {
            Interlocked.Increment(ref streamInfo.activeClientCount);
            _logger?.LogDebug("Stream {Path}: clients={Count}", path, streamInfo.activeClientCount);
        }
    }

    /// <summary>
    /// 减少流的活跃客户端计数
    /// </summary>
    public void DecrementActiveClients(string path)
    {
        if (_streams.TryGetValue(path, out var streamInfo))
        {
            Interlocked.Decrement(ref streamInfo.activeClientCount);
            if (streamInfo.activeClientCount < 0)
                streamInfo.activeClientCount = 0;
            _logger?.LogDebug("Stream {Path}: clients={Count}", path, streamInfo.activeClientCount);
        }
    }

    /// <summary>
    /// 刷新流的媒体元数据（SPS/PPS 可能在源启动后才可用，DESCRIBE 前调用）
    /// </summary>
    public void RefreshStreamMetadata(string path)
    {
        if (_streams.TryGetValue(path, out var streamInfo) && _sources.TryGetValue(path, out var source))
        {
            if (streamInfo.SpsData == null && source.SpsData != null)
                streamInfo.SpsData = source.SpsData;
            if (streamInfo.PpsData == null && source.PpsData != null)
                streamInfo.PpsData = source.PpsData;
        }
    }

    /// <summary>
    /// 获取流的 RTP 数据读取器
    /// </summary>
    public IAsyncEnumerable<RtpPacket> GetRtpStreamAsync(string path, CancellationToken ct)
    {
        if (_sources.TryGetValue(path, out var source))
        {
            _logger?.LogDebug("Found media source for {Path}", path);
            return source.GetPacketsAsync(ct);
        }

        _logger?.LogWarning("Stream not found: {Path}. Available streams: {Paths}", 
            path, string.Join(", ", _sources.Keys));
        throw new InvalidOperationException($"Stream not found: {path}");
    }

    private IMediaSource? CreateMediaSource(StreamConfig config)
    {
        return config.SourceType switch
        {
            MediaSourceType.File => new FileMediaSource(config, this),
            MediaSourceType.RtspPull => new RtspPullMediaSource(config, this),
            MediaSourceType.TestPattern => new TestPatternMediaSource(config, this),
            MediaSourceType.RtmpPush => new RtmpPushMediaSource(config),
            MediaSourceType.Camera => new CameraMediaSource(config, this),
            MediaSourceType.Screen => new ScreenCaptureMediaSource(config, this),
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
    internal long activeClientCount;
    public int ActiveClients => (int)Interlocked.Read(ref activeClientCount);
    /// <summary>
    /// 媒体源是否正在运行
    /// </summary>
    public bool IsRunning { get; set; } = true;
    /// <summary>
    /// SPS 数据（用于 SDP sprop-parameter-sets）
    /// </summary>
    public byte[]? SpsData { get; set; }
    /// <summary>
    /// PPS 数据（用于 SDP sprop-parameter-sets）
    /// </summary>
    public byte[]? PpsData { get; set; }
    /// <summary>
    /// AAC AudioSpecificConfig（从媒体源提取的真实值，用于 SDP fmtp config=）
    /// </summary>
    public byte[]? AacAudioSpecificConfig { get; set; }
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

    /// <summary>
    /// SPS 数据（H.264）
    /// </summary>
    byte[]? SpsData { get; }

    /// <summary>
    /// PPS 数据（H.264）
    /// </summary>
    byte[]? PpsData { get; }
}

/// <summary>
/// RTP 包广播器
/// 单生产者写入，多订阅者各自持有独立的有界 Channel。
/// 每个包会分发给所有订阅者，慢订阅者只丢自己的旧包，互不影响。
/// （此前所有读者共享一个 Channel，音频包大多被视频轨的读者取走后丢弃，导致客户端无声音）
/// </summary>
public sealed class RtpPacketBroadcaster
{
    private readonly ConcurrentDictionary<long, Channel<RtpPacket>> _subscribers = new();
    private long _nextId;
    private volatile bool _completed;

    /// <summary>
    /// 当前订阅者数量
    /// </summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>
    /// 向所有订阅者广播一个 RTP 包
    /// </summary>
    public void Publish(RtpPacket packet)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(packet);
        }
    }

    /// <summary>
    /// 完成广播，通知所有订阅者流结束
    /// </summary>
    public void Complete()
    {
        _completed = true;
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 订阅 RTP 包流，每个订阅者拥有独立队列
    /// </summary>
    public async IAsyncEnumerable<RtpPacket> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<RtpPacket>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

        long id = Interlocked.Increment(ref _nextId);
        _subscribers.TryAdd(id, channel);

        if (_completed)
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await foreach (var packet in channel.Reader.ReadAllAsync(ct))
            {
                yield return packet;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// G.711 A-law 音频工具
/// 提供 PCM→A-law 编码和测试音生成（用于无实际音频源的测试类媒体源）
/// </summary>
public static class G711Audio
{
    /// <summary>
    /// 采样率固定 8000Hz
    /// </summary>
    public const int SampleRate = 8000;

    /// <summary>
    /// 每包采样数（20ms）
    /// </summary>
    public const int SamplesPerFrame = 160;

    /// <summary>
    /// 16-bit PCM 转 A-law
    /// </summary>
    public static byte LinearToALaw(short pcm)
    {
        const int Clip = 32635;
        int sign = ((~pcm) >> 8) & 0x80;
        int sample = sign == 0 ? -pcm : pcm;
        if (sample > Clip) sample = Clip;

        byte compressed;
        if (sample >= 256)
        {
            int exponent = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)(sample >> 8)) + 1;
            int mantissa = (sample >> (exponent + 3)) & 0x0F;
            compressed = (byte)((exponent << 4) | mantissa);
        }
        else
        {
            compressed = (byte)(sample >> 4);
        }

        return (byte)(compressed ^ (sign ^ 0x55));
    }

    /// <summary>
    /// 生成一帧 440Hz 正弦测试音（A-law 编码，160 采样 = 20ms）
    /// </summary>
    /// <param name="phase">正弦波相位，跨帧持续累积</param>
    public static byte[] GenerateToneFrame(ref double phase)
    {
        const double frequency = 440.0;
        const double amplitude = 8000.0;
        double phaseStep = 2 * Math.PI * frequency / SampleRate;

        var frame = new byte[SamplesPerFrame];
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            short pcm = (short)(amplitude * Math.Sin(phase));
            frame[i] = LinearToALaw(pcm);
            phase += phaseStep;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
        }
        return frame;
    }

    /// <summary>
    /// 将 A-law 帧封装为 RTP 包（PT=8）
    /// </summary>
    public static byte[] CreateRtpPacket(byte[] alawFrame, ushort sequenceNumber, uint timestamp)
    {
        var packet = new byte[12 + alawFrame.Length];
        packet[0] = 0x80;
        packet[1] = 8; // PCMA, marker=0
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = 0x12; packet[9] = 0x34; packet[10] = 0x56; packet[11] = 0x79;
        Array.Copy(alawFrame, 0, packet, 12, alawFrame.Length);
        return packet;
    }
}

/// <summary>
/// 测试类媒体源共用的音频配置工具
/// </summary>
internal static class MediaSourceAudioHelper
{
    /// <summary>
    /// 同步 StreamInfo 的音频参数：
    /// 测试类源只能产生 PCMA 测试音，音频关闭时置为 None，保证 SDP 与实际数据一致
    /// </summary>
    public static void ApplyTestToneAudioConfig(StreamManager? owner, StreamConfig config, bool audioEnabled)
    {
        var info = owner?.GetStream(config.Path);
        if (info == null)
            return;

        if (audioEnabled)
        {
            info.AudioCodec = AudioCodecType.PCMA;
            info.SampleRate = G711Audio.SampleRate;
            info.Channels = 1;
        }
        else
        {
            info.AudioCodec = AudioCodecType.None;
        }
    }
}

/// <summary>
/// 文件媒体源
/// 支持 H.264 Annex-B 文件和 MP4 容器
/// </summary>
public class FileMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private readonly StreamManager _owner;  // 用于更新 StreamInfo
    private bool _running;

    // Video timing
    private ushort _videoSequenceNumber;
    private uint _videoTimestamp;
    
    // Audio timing
    private ushort _audioSequenceNumber;
    private uint _audioTimestamp;

    // Video data
    private List<byte[]> _videoNalUnits = new();
    
    // Audio data
    private List<byte[]> _audioSamples = new();
    private int _audioSampleRate = 44100;
    private int _audioChannels = 2;
    private AudioCodecType _audioCodec = AudioCodecType.AAC;

    // H.264 NAL unit types
    private const byte NAL_SPS = 7;
    private const byte NAL_PPS = 8;
    private const byte NAL_IDR = 5;
    private const byte NAL_SLICE = 1;

    /// <summary>
    /// SPS 数据
    /// </summary>
    public byte[]? SpsData { get; private set; }

    /// <summary>
    /// PPS 数据
    /// </summary>
    public byte[]? PpsData { get; private set; }

    public FileMediaSource(StreamConfig config, StreamManager owner)
    {
        _config = config;
        _owner = owner;
    }

    private CancellationTokenSource? _producerCts;
    private RtpPacketBroadcaster? _broadcaster;

    public Task StartAsync(CancellationToken ct)
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;
        _broadcaster = new RtpPacketBroadcaster();

        _producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ProducePacketsAsync(_producerCts.Token), _producerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct)
    {
        var broadcaster = _broadcaster;
        if (broadcaster == null)
            return EmptyPacketsAsync();

        return broadcaster.SubscribeAsync(ct);
    }

    private static async IAsyncEnumerable<RtpPacket> EmptyPacketsAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// 生产者：生成视频和音频 RTP 包写入 Channel（单路运行，避免双路枚举）
    /// </summary>
    private async Task ProducePacketsAsync(CancellationToken ct)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"FileMediaSource: Source={_config.Source}, Exists={File.Exists(_config.Source ?? "")}");

            if (string.IsNullOrEmpty(_config.Source) || !File.Exists(_config.Source))
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: Source file not found or empty!");
                return;
            }

            string ext = Path.GetExtension(_config.Source).ToLowerInvariant();
            System.Diagnostics.Debug.WriteLine($"FileMediaSource: File extension={ext}");

            // 只读一次文件，供视频/音频两个解析器共用
            byte[] fileData = await File.ReadAllBytesAsync(_config.Source, ct);

            List<byte[]> nalUnits;

            if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: Parsing MP4 file...");
                nalUnits = ParseMp4File(fileData);
                System.Diagnostics.Debug.WriteLine($"FileMediaSource: MP4 parsing complete, {nalUnits.Count} NAL units");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: Parsing Annex-B file...");
                nalUnits = ParseAnnexBNalUnits(fileData);
                System.Diagnostics.Debug.WriteLine($"FileMediaSource: Annex-B parsing complete, {nalUnits.Count} NAL units");
            }

            if (nalUnits.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: No NAL units found!");
                return;
            }

            // 分离 SPS/PPS 和视频帧数据
            byte[]? spsData = SpsData;
            byte[]? ppsData = PpsData;
            var videoNalUnits = new List<byte[]>();

            foreach (var nal in nalUnits)
            {
                byte nalType = (byte)(nal[0] & 0x1F);
                if (nalType != NAL_SPS && nalType != NAL_PPS)
                    videoNalUnits.Add(nal);
            }

            System.Diagnostics.Debug.WriteLine($"FileMediaSource: Separated {videoNalUnits.Count} video NAL units");

            // 提取音频数据（如果有）
            if (_config.EnableAudio && _config.AudioCodec != AudioCodecType.None
                && (ext == ".mp4" || ext == ".m4v" || ext == ".mov"))
            {
                var audioResult = ParseMp4AudioTrack(fileData);
                _audioSamples = audioResult.samples;
                _audioSampleRate = audioResult.sampleRate;
                _audioChannels = audioResult.channels;
                _audioCodec = audioResult.codec;
                System.Diagnostics.Debug.WriteLine($"FileMediaSource: Audio parsed: {_audioSamples.Count} frames, " +
                    $"sr={_audioSampleRate}, ch={_audioChannels}");

                // 更新 StreamInfo 以匹配实际文件中的音频格式
                var info = _owner.GetStream(_config.Path);
                if (info != null)
                {
                    info.AudioCodec = _audioCodec;
                    info.SampleRate = _audioSampleRate;
                    info.Channels = _audioChannels;
                    info.AacAudioSpecificConfig = audioResult.asc; // SDP config= 直接用文件真实值
                }
            }

            // 视频帧率：优先使用文件真实帧率（mdhd/stts），配置值只作回退
            // （帧率不匹配会导致播放速度错误和音画不同步）
            int framerate = Math.Max(1, _config.Framerate);
            if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
            {
                int parsedFps = ParseMp4VideoFrameRate(fileData);
                if (parsedFps is > 0 and <= 240)
                {
                    framerate = parsedFps;
                    var vinfo = _owner.GetStream(_config.Path);
                    if (vinfo != null)
                        vinfo.Framerate = framerate;
                    System.Diagnostics.Debug.WriteLine($"FileMediaSource: video framerate from file = {framerate}");
                }
            }

            // 计算音频帧间隔（毫秒）
            int audioFrameSize = _audioCodec switch
            {
                AudioCodecType.PCMA => 160,
                AudioCodecType.PCMU => 160,
                AudioCodecType.AAC => 1024,
                AudioCodecType.OPUS => 960,
                _ => 160
            };
            double audioFrameIntervalMs = 1000.0 * audioFrameSize / _audioSampleRate;

            int frameCount = 0;
            int nalIndex = 0;
            int audioIndex = 0;
            bool loop = _config.Loop;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long frameIntervalTicks = (long)(System.Diagnostics.Stopwatch.Frequency / (double)framerate);
            long nextVideoTick = stopwatch.ElapsedTicks;
            long nextAudioTick = stopwatch.ElapsedTicks;
            long audioIntervalTicks = (long)(System.Diagnostics.Stopwatch.Frequency * audioFrameIntervalMs / 1000.0);

            while (_running && !ct.IsCancellationRequested)
            {
                long now = stopwatch.ElapsedTicks;
                bool videoDue = now >= nextVideoTick;
                bool audioDue = now >= nextAudioTick && _audioSamples.Count > 0;

                // 如果两个都没到时间，等待最近的那个
                if (!videoDue && !audioDue)
                {
                    // 只考虑实际存在的轨道的时间
                    long nextTick = _audioSamples.Count > 0
                        ? Math.Min(nextVideoTick, nextAudioTick)
                        : nextVideoTick;
                    long waitTicks = nextTick - now;
                    int waitMs = (int)(waitTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        await Task.Delay(waitMs, ct);
                    continue;
                }

                // 发送视频包：每个时钟周期发送一整帧
                // （SEI 等非 VCL NAL 与其后的 VCL NAL 同一时刻发出，避免占用帧周期拖慢播放）
                if (videoDue)
                {
                    nextVideoTick += frameIntervalTicks;

                    bool sentVclNal = false;
                    while (!sentVclNal && nalIndex < videoNalUnits.Count)
                    {
                        byte[] nalData = videoNalUnits[nalIndex];
                        byte nalType = (byte)(nalData[0] & 0x1F);
                        bool isKeyFrame = nalType == NAL_IDR;
                        bool isVclNal = nalType == NAL_IDR || nalType == NAL_SLICE;

                        if (isKeyFrame && spsData != null && ppsData != null)
                        {
                            var spsPkts = CreateRtpPackets(spsData, _videoSequenceNumber, _videoTimestamp, false);
                            foreach (var (data, seq) in spsPkts)
                            {
                                _broadcaster!.Publish(new RtpPacket
                                {
                                    Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                                    SequenceNumber = seq, IsKeyFrame = false
                                });
                                _videoSequenceNumber = (ushort)(seq + 1);  // 始终递增，丢包时有 gap
                            }
                            var ppsPkts = CreateRtpPackets(ppsData, _videoSequenceNumber, _videoTimestamp, false);
                            foreach (var (data, seq) in ppsPkts)
                            {
                                _broadcaster!.Publish(new RtpPacket
                                {
                                    Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                                    SequenceNumber = seq, IsKeyFrame = false
                                });
                                _videoSequenceNumber = (ushort)(seq + 1);
                            }
                        }

                        // marker 置于访问单元（帧）最后一个 RTP 包
                        var rtpPkts = CreateRtpPackets(nalData, _videoSequenceNumber, _videoTimestamp, isVclNal);
                        foreach (var (data, seq) in rtpPkts)
                        {
                            _broadcaster!.Publish(new RtpPacket
                            {
                                Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                                SequenceNumber = seq, IsKeyFrame = isKeyFrame
                            });
                            _videoSequenceNumber = (ushort)(seq + 1);
                        }

                        nalIndex++;

                        if (isVclNal)
                        {
                            _videoTimestamp += (uint)(90000 / framerate);
                            frameCount++;
                            sentVclNal = true;
                        }
                    }

                    if (nalIndex >= videoNalUnits.Count)
                    {
                        if (loop)
                        {
                            nalIndex = 0;
                            audioIndex = 0;
                            frameCount = 0;
                            nextVideoTick = stopwatch.ElapsedTicks;
                            nextAudioTick = stopwatch.ElapsedTicks;
                        }
                        else break;
                    }
                }

                // 发送音频包（按音频时钟独立发送，不受视频帧率约束）
                if (audioDue)
                {
                    nextAudioTick += audioIntervalTicks;

                    if (audioIndex < _audioSamples.Count)
                    {
                        byte[] audioData = _audioSamples[audioIndex];
                        var audioPkt = CreateAudioRtpPacket(audioData, _audioSequenceNumber, _audioTimestamp);

                        _broadcaster!.Publish(new RtpPacket
                        {
                            Data = audioPkt, TrackId = 1, Timestamp = _audioTimestamp,
                            SequenceNumber = _audioSequenceNumber, IsKeyFrame = false
                        });
                        _audioSequenceNumber++;
                        _audioTimestamp += (uint)audioFrameSize;

                        audioIndex++;
                        if (audioIndex >= _audioSamples.Count && loop)
                            audioIndex = 0;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"FileMediaSource: done ({frameCount} frames)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileMediaSource producer error: {ex.Message}");
        }
        finally
        {
            _broadcaster?.Complete();
        }
    }

    #region MP4 Demuxer

    /// <summary>
    /// 解析 MP4 文件，提取 H.264 NAL 单元
    /// </summary>
    private List<byte[]> ParseMp4File(byte[] fileData)
    {
        var nalUnits = new List<byte[]>();

        try
        {
            System.Diagnostics.Debug.WriteLine($"MP4 file loaded: {fileData.Length} bytes");

            // 解析顶层 atoms
            var atoms = ParseAtoms(fileData, 0, fileData.Length);
            System.Diagnostics.Debug.WriteLine($"Found {atoms.Count} top-level atoms: {string.Join(", ", atoms.Select(a => a.Type))}");

            // 查找 moov atom
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");
            if (moov == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: No moov atom found!");
                return nalUnits;
            }
            System.Diagnostics.Debug.WriteLine($"moov atom found at offset {moov.Offset}, size {moov.Size}");

            // 解析 moov 内部的 atoms
            var moovAtoms = ParseAtoms(fileData, moov.DataOffset, moov.DataSize);
            System.Diagnostics.Debug.WriteLine($"moov contains {moovAtoms.Count} atoms: {string.Join(", ", moovAtoms.Select(a => a.Type))}");

            // 查找 mdat atom
            var mdat = atoms.FirstOrDefault(a => a.Type == "mdat");
            if (mdat == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: No mdat atom found!");
                return nalUnits;
            }
            System.Diagnostics.Debug.WriteLine($"mdat atom found at offset {mdat.Offset}, size {mdat.Size}");

            // 查找 trak atom (视频轨道)
            foreach (var trak in moovAtoms.Where(a => a.Type == "trak"))
            {
                var trakAtoms = ParseAtoms(fileData, trak.DataOffset, trak.DataSize);
                System.Diagnostics.Debug.WriteLine($"trak contains {trakAtoms.Count} atoms");

                // 检查是否为视频轨道
                var mdia = trakAtoms.FirstOrDefault(a => a.Type == "mdia");
                if (mdia == null) continue;

                var mdiaAtoms = ParseAtoms(fileData, mdia.DataOffset, mdia.DataSize);

                // 检查 hdlr 类型
                var hdlr = mdiaAtoms.FirstOrDefault(a => a.Type == "hdlr");
                if (hdlr != null)
                {
                    if (hdlr.DataOffset + hdlr.DataSize <= fileData.Length)
                    {
                        // hdlr 格式: version(1) + flags(3) + pre_defined(4) + handler_type(4)
                        // handler_type 在 hdlr data 的偏移 +8 处
                        if (hdlr.DataSize >= 12)
                        {
                            string handlerType = System.Text.Encoding.ASCII.GetString(fileData, (int)hdlr.DataOffset + 8, 4);
                            System.Diagnostics.Debug.WriteLine($"handler_type: '{handlerType}'");
                            
                            if (handlerType != "vide")
                            {
                                System.Diagnostics.Debug.WriteLine($"Skipping non-video track (handler_type='{handlerType}')");
                                continue;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("Found video track!");
                            }
                        }
                    }
                }

                // 解析 minf -> stbl
                var minf = mdiaAtoms.FirstOrDefault(a => a.Type == "minf");
                if (minf == null) continue;

                var minfAtoms = ParseAtoms(fileData, minf.DataOffset, minf.DataSize);

                var stbl = minfAtoms.FirstOrDefault(a => a.Type == "stbl");
                if (stbl == null) continue;

                var stblAtoms = ParseAtoms(fileData, stbl.DataOffset, stbl.DataSize);
                System.Diagnostics.Debug.WriteLine($"stbl contains {stblAtoms.Count} atoms: {string.Join(", ", stblAtoms.Select(a => a.Type))}");

                // 解析 stsd 获取 SPS/PPS
                byte[]? sps = null;
                byte[]? pps = null;
                int nalLengthSize = 4; // 默认 4 字节长度前缀

                var stsd = stblAtoms.FirstOrDefault(a => a.Type == "stsd");
                if (stsd != null)
                {
                    (sps, pps, nalLengthSize) = ParseStsdForAvc(fileData, stsd.DataOffset, stsd.DataSize);
                    System.Diagnostics.Debug.WriteLine($"stsd parsed: SPS={sps?.Length ?? 0} bytes, PPS={pps?.Length ?? 0} bytes, nalLengthSize={nalLengthSize}");
                }

                // 解析 stsc (sample-to-chunk)
                var stsc = stblAtoms.FirstOrDefault(a => a.Type == "stsc");
                List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)>? sampleToChunk = null;
                if (stsc != null)
                {
                    sampleToChunk = ParseStsc(fileData, stsc.DataOffset, stsc.DataSize);
                    System.Diagnostics.Debug.WriteLine($"stsc: {sampleToChunk.Count} entries");
                }

                // 解析 stsz (sample sizes)
                var stsz = stblAtoms.FirstOrDefault(a => a.Type == "stsz");
                uint[]? sampleSizes = null;
                if (stsz != null)
                {
                    sampleSizes = ParseStsz(fileData, stsz.DataOffset, stsz.DataSize);
                    System.Diagnostics.Debug.WriteLine($"stsz: {sampleSizes.Length} samples");
                }

                // 解析 stco/co64 (chunk offsets)
                var stco = stblAtoms.FirstOrDefault(a => a.Type == "stco" || a.Type == "co64");
                long[]? chunkOffsets = null;
                if (stco != null)
                {
                    chunkOffsets = ParseChunkOffsets(fileData, stco.DataOffset, stco.DataSize, stco.Type);
                    System.Diagnostics.Debug.WriteLine($"stco/co64: {chunkOffsets.Length} chunks");
                }

                // 如果有 SPS/PPS，添加到 NAL 单元列表
                // 保存 SPS/PPS 到属性（用于 SDP）
                if (sps != null && sps.Length > 0)
                {
                    SpsData = sps;
                    nalUnits.Add(sps);
                    System.Diagnostics.Debug.WriteLine($"Added SPS: {sps.Length} bytes, type=0x{sps[0]:X2}");
                }
                if (pps != null && pps.Length > 0)
                {
                    PpsData = pps;
                    nalUnits.Add(pps);
                    System.Diagnostics.Debug.WriteLine($"Added PPS: {pps.Length} bytes, type=0x{pps[0]:X2}");
                }

                // 提取样本数据
                if (sampleSizes != null && chunkOffsets != null && sampleToChunk != null)
                {
                    var samples = ExtractSamples(fileData, sampleSizes, chunkOffsets, sampleToChunk, nalLengthSize);
                    nalUnits.AddRange(samples);
                    System.Diagnostics.Debug.WriteLine($"Extracted {samples.Count} NAL units from {sampleSizes.Length} samples");
                }

                break; // 只处理第一个视频轨道
            }
            
            System.Diagnostics.Debug.WriteLine($"Total NAL units: {nalUnits.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MP4 parsing error: {ex.Message}\n{ex.StackTrace}");
        }

        return nalUnits;
    }

    private List<Mp4Atom> ParseAtoms(byte[] data, long offset, long size)
    {
        var atoms = new List<Mp4Atom>();
        long pos = offset;
        long end = offset + size;

        while (pos < end - 8)
        {
            if (pos + 8 > data.Length) break;
            
            // 读取 atom 大小和类型
            uint atomSize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            string atomType = System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4);

            // 处理扩展大小 (64-bit)
            if (atomSize == 1)
            {
                // 扩展大小模式：接下来 8 字节是真正的大小
                if (pos + 16 > data.Length) break;
                ulong extendedSize = ((ulong)data[pos + 8] << 56) | ((ulong)data[pos + 9] << 48) |
                                     ((ulong)data[pos + 10] << 40) | ((ulong)data[pos + 11] << 32) |
                                     ((ulong)data[pos + 12] << 24) | ((ulong)data[pos + 13] << 16) |
                                     ((ulong)data[pos + 14] << 8) | data[pos + 15];
                atomSize = (uint)extendedSize;
                
                atoms.Add(new Mp4Atom
                {
                    Type = atomType,
                    Offset = pos,
                    Size = atomSize,
                    DataOffset = pos + 16,
                    DataSize = atomSize - 16
                });
            }
            else if (atomSize >= 8)
            {
                atoms.Add(new Mp4Atom
                {
                    Type = atomType,
                    Offset = pos,
                    Size = atomSize,
                    DataOffset = pos + 8,
                    DataSize = atomSize - 8
                });
            }
            else
            {
                // 无效的 atom 大小，跳过
                System.Diagnostics.Debug.WriteLine($"Invalid atom size {atomSize} at offset {pos}");
                break;
            }

            pos += atomSize;
        }

        return atoms;
    }

    private (byte[]? sps, byte[]? pps, int nalLengthSize) ParseStsdForAvc(byte[] data, long offset, long size)
    {
        long pos = offset;

        // stsd header: version(1) + flags(3) + entry_count(4)
        pos += 8;

        if (pos + 4 > data.Length) return (null, null, 4);
        
        // 读取第一个 sample entry 的大小
        uint entrySize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;

        if (pos + 4 > data.Length) return (null, null, 4);
        
        // 读取 format (如 avc1, hvc1 等)
        string format = System.Text.Encoding.ASCII.GetString(data, (int)pos, 4);
        System.Diagnostics.Debug.WriteLine($"stsd format: {format}");

        // 检查是否为 H.264
        if (!format.StartsWith("avc") && format != "hvc1" && format != "hev1")
        {
            System.Diagnostics.Debug.WriteLine($"Unsupported format: {format}");
            return (null, null, 4);
        }

        // 在 sample entry 内部查找 avcC box
        // avc1 sample entry 有固定头部，然后是子 boxes
        // 我们扫描整个 entry 查找 "avcC" 签名
        long entryEnd = offset + 8 + entrySize;
        long searchStart = pos + 4; // 从 format 字段之后开始

        System.Diagnostics.Debug.WriteLine($"stsd: entrySize={entrySize}, entryEnd={entryEnd}, searchStart={searchStart}");

        // 扫描查找 "avcC" 签名 (0x61766343)
        for (long scanPos = searchStart; scanPos < entryEnd - 8; scanPos++)
        {
            // 检查是否为 "avcC" 签名
            if (data[scanPos] == 0x61 && data[scanPos + 1] == 0x76 && 
                data[scanPos + 2] == 0x63 && data[scanPos + 3] == 0x43)
            {
                // 找到 avcC! 回退 4 字节获取 box 大小
                long boxStart = scanPos - 4;
                if (boxStart >= searchStart)
                {
                    uint boxSize = (uint)((data[boxStart] << 24) | (data[boxStart + 1] << 16) | 
                                          (data[boxStart + 2] << 8) | data[boxStart + 3]);
                    
                    System.Diagnostics.Debug.WriteLine($"  Found avcC at offset {boxStart}, size={boxSize}");
                    
                    if (boxSize >= 8 && boxStart + boxSize <= entryEnd)
                    {
                        // 解析 AVCDecoderConfigurationRecord
                        byte[] avccData = new byte[boxSize - 8];
                        Array.Copy(data, boxStart + 8, avccData, 0, avccData.Length);
                        return ParseAvcDecoderConfig(avccData);
                    }
                }
            }
            // 也检查 "hvcC" (HEVC)
            else if (data[scanPos] == 0x68 && data[scanPos + 1] == 0x76 && 
                     data[scanPos + 2] == 0x63 && data[scanPos + 3] == 0x43)
            {
                System.Diagnostics.Debug.WriteLine("HEVC codec detected, not supported yet");
                return (null, null, 4);
            }
        }

        System.Diagnostics.Debug.WriteLine("avcC box not found in stsd");
        return (null, null, 4);
    }

    private (byte[] sps, byte[] pps, int nalLengthSize) ParseAvcDecoderConfig(byte[] data)
    {
        if (data.Length < 6)
            return (Array.Empty<byte>(), Array.Empty<byte>(), 4);
            
        int offset = 0;

        // configurationVersion
        byte version = data[offset++];
        // AVCProfileIndication
        byte profile = data[offset++];
        // profile_compatibility
        byte compat = data[offset++];
        // AVCLevelIndication
        byte level = data[offset++];
        // lengthSizeMinusOne (NAL length size - 1)
        byte lengthSizeMinusOne = (byte)(data[offset++] & 0x03);
        int nalLengthSize = lengthSizeMinusOne + 1;

        System.Diagnostics.Debug.WriteLine($"AVC config: profile={profile}, level={level}, nalLengthSize={nalLengthSize}");

        // numOfSequenceParameterSets
        byte numSps = (byte)(data[offset++] & 0x1F);
        System.Diagnostics.Debug.WriteLine($"numSPS={numSps}");

        byte[]? sps = null;
        for (int i = 0; i < numSps; i++)
        {
            if (offset + 2 > data.Length) break;
            ushort spsLength = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            
            if (offset + spsLength > data.Length) break;
            sps = new byte[spsLength];
            Array.Copy(data, offset, sps, 0, spsLength);
            offset += spsLength;
            System.Diagnostics.Debug.WriteLine($"SPS[{i}]: {spsLength} bytes");
        }

        if (offset >= data.Length) return (sps ?? Array.Empty<byte>(), Array.Empty<byte>(), nalLengthSize);
        
        // numOfPictureParameterSets
        byte numPps = data[offset++];
        System.Diagnostics.Debug.WriteLine($"numPPS={numPps}");

        byte[]? pps = null;
        for (int i = 0; i < numPps; i++)
        {
            if (offset + 2 > data.Length) break;
            ushort ppsLength = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            
            if (offset + ppsLength > data.Length) break;
            pps = new byte[ppsLength];
            Array.Copy(data, offset, pps, 0, ppsLength);
            offset += ppsLength;
            System.Diagnostics.Debug.WriteLine($"PPS[{i}]: {ppsLength} bytes");
        }

        return (sps ?? Array.Empty<byte>(), pps ?? Array.Empty<byte>(), nalLengthSize);
    }

    private List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)> ParseStsc(byte[] data, long offset, long size)
    {
        var entries = new List<(uint, uint, uint)>();
        long pos = offset;

        if (pos + 8 > data.Length) return entries;
        
        // version(1) + flags(3) + entry_count(4)
        pos += 4; // skip version/flags
        uint entryCount = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;

        for (int i = 0; i < entryCount && pos + 12 <= data.Length; i++)
        {
            uint firstChunk = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            uint samplesPerChunk = (uint)((data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7]);
            uint sampleDescIndex = (uint)((data[pos + 8] << 24) | (data[pos + 9] << 16) | (data[pos + 10] << 8) | data[pos + 11]);
            entries.Add((firstChunk, samplesPerChunk, sampleDescIndex));
            pos += 12;
        }

        return entries;
    }

    private uint[] ParseStsz(byte[] data, long offset, long size)
    {
        long pos = offset;

        if (pos + 12 > data.Length) return Array.Empty<uint>();
        
        // version(1) + flags(3)
        pos += 4;
        uint sampleSize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;
        uint sampleCount = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;

        if (sampleSize != 0)
        {
            // 所有样本大小相同
            return Enumerable.Repeat(sampleSize, (int)sampleCount).ToArray();
        }

        var sizes = new uint[sampleCount];
        for (int i = 0; i < sampleCount && pos + 4 <= data.Length; i++)
        {
            sizes[i] = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            pos += 4;
        }

        return sizes;
    }

    private long[] ParseChunkOffsets(byte[] data, long offset, long size, string type)
    {
        long pos = offset;

        if (pos + 8 > data.Length) return Array.Empty<long>();
        
        // version(1) + flags(3)
        pos += 4;
        uint entryCount = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;

        var offsets = new long[entryCount];
        int bytesPerEntry = type == "co64" ? 8 : 4;

        for (int i = 0; i < entryCount && pos + bytesPerEntry <= data.Length; i++)
        {
            if (bytesPerEntry == 8)
            {
                offsets[i] = ((long)data[pos] << 56) | ((long)data[pos + 1] << 48) | ((long)data[pos + 2] << 40) | ((long)data[pos + 3] << 32) |
                             ((long)data[pos + 4] << 24) | ((long)data[pos + 5] << 16) | ((long)data[pos + 6] << 8) | data[pos + 7];
            }
            else
            {
                offsets[i] = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            }
            pos += bytesPerEntry;
        }

        return offsets;
    }

    private List<byte[]> ExtractSamples(byte[] fileData, uint[] sampleSizes, long[] chunkOffsets,
        List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)> sampleToChunk,
        int nalLengthSize)
    {
        var samples = new List<byte[]>();
        int sampleIndex = 0;

        for (int chunkIdx = 0; chunkIdx < chunkOffsets.Length; chunkIdx++)
        {
            // 确定当前 chunk 的样本数
            uint samplesInChunk = sampleToChunk[0].samplesPerChunk;
            for (int i = sampleToChunk.Count - 1; i >= 0; i--)
            {
                if (chunkIdx + 1 >= sampleToChunk[i].firstChunk)
                {
                    samplesInChunk = sampleToChunk[i].samplesPerChunk;
                    break;
                }
            }

            long chunkOffset = chunkOffsets[chunkIdx];

            for (int s = 0; s < samplesInChunk && sampleIndex < sampleSizes.Length; s++)
            {
                uint sampleSize = sampleSizes[sampleIndex];

                // 检查边界
                if (chunkOffset + sampleSize > fileData.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"Sample {sampleIndex} out of bounds: offset={chunkOffset}, size={sampleSize}, fileSize={fileData.Length}");
                    sampleIndex++;
                    continue;
                }

                // 读取样本数据
                byte[] sampleData = new byte[sampleSize];
                Array.Copy(fileData, chunkOffset, sampleData, 0, sampleSize);

                // 转换 AVCC 格式到 Annex-B 格式
                var nalUnits = ConvertAvccToAnnexB(sampleData, nalLengthSize);
                
                // 为每个样本的第一帧添加调试信息
                if (sampleIndex < 5 && nalUnits.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Sample {sampleIndex}: size={sampleSize}, NALs={nalUnits.Count}, firstNAL type=0x{nalUnits[0][0]:X2}");
                }
                
                samples.AddRange(nalUnits);

                chunkOffset += sampleSize;
                sampleIndex++;
            }
        }

        return samples;
    }

    /// <summary>
    /// 将 AVCC 格式的样本转换为 Annex-B 格式的 NAL 单元
    /// AVCC: [length][NAL][length][NAL]...
    /// </summary>
    private List<byte[]> ConvertAvccToAnnexB(byte[] sampleData, int nalLengthSize)
    {
        var nalUnits = new List<byte[]>();
        int offset = 0;

        while (offset < sampleData.Length)
        {
            if (offset + nalLengthSize > sampleData.Length)
                break;

            // 读取 NAL 长度
            uint nalLength = 0;
            for (int i = 0; i < nalLengthSize; i++)
            {
                nalLength = (nalLength << 8) | sampleData[offset + i];
            }
            offset += nalLengthSize;

            if (nalLength == 0 || offset + nalLength > sampleData.Length)
                break;

            // 提取 NAL 数据
            byte[] nal = new byte[nalLength];
            Array.Copy(sampleData, offset, nal, 0, nalLength);
            nalUnits.Add(nal);

            offset += (int)nalLength;
        }

        return nalUnits;
    }

    private class Mp4Atom
    {
        public string Type { get; set; } = "";
        public long Offset { get; set; }
        public long Size { get; set; }
        public long DataOffset { get; set; }
        public long DataSize { get; set; }
    }

    #endregion

    #region Annex-B Parser

    /// <summary>
    /// 解析 Annex-B 格式的 NAL 单元
    /// </summary>
    private List<byte[]> ParseAnnexBNalUnits(byte[] data)
    {
        var nalUnits = new List<byte[]>();
        int i = 0;
        int nalStart = -1;

        while (i < data.Length)
        {
            if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                if (nalStart >= 0)
                {
                    int nalLength = i - nalStart;
                    if (nalLength > 0)
                    {
                        byte[] nal = new byte[nalLength];
                        Array.Copy(data, nalStart, nal, 0, nalLength);
                        nalUnits.Add(nal);
                    }
                }
                nalStart = i + 4;
                i += 4;
            }
            else if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (nalStart >= 0)
                {
                    int nalLength = i - nalStart;
                    if (nalLength > 0)
                    {
                        byte[] nal = new byte[nalLength];
                        Array.Copy(data, nalStart, nal, 0, nalLength);
                        nalUnits.Add(nal);
                    }
                }
                nalStart = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }

        if (nalStart >= 0 && nalStart < data.Length)
        {
            int nalLength = data.Length - nalStart;
            if (nalLength > 0)
            {
                byte[] nal = new byte[nalLength];
                Array.Copy(data, nalStart, nal, 0, nalLength);
                nalUnits.Add(nal);
            }
        }

        return nalUnits;
    }

    #endregion

    #region RTP Packet Creation

    /// <summary>
    /// 创建 RTP 包（支持 FU-A 分片）
    /// 返回一个或多个 RTP 包，序列号自动递增
    /// </summary>
    private List<(byte[] data, ushort seq)> CreateRtpPackets(byte[] nalData, ushort startSeq, uint timestamp, bool isMarker)
    {
        var packets = new List<(byte[] data, ushort seq)>();
        int rtpHeaderSize = 12;
        int maxPayloadSize = 1400; // MTU 限制
        ushort seq = startSeq;

        if (nalData.Length <= maxPayloadSize)
        {
            // 单个 NAL 单元模式
            var packet = new byte[rtpHeaderSize + nalData.Length];
            WriteRtpHeader(packet, seq, timestamp, isMarker);
            Array.Copy(nalData, 0, packet, rtpHeaderSize, nalData.Length);
            packets.Add((packet, seq));
        }
        else
        {
            // FU-A 分片模式
            byte nalHeader = nalData[0];
            byte nalType = (byte)(nalHeader & 0x1F);
            byte nri = (byte)(nalHeader & 0x60); // NRI bits
            
            // FU indicator: F(1) + NRI(2) + Type(5)=28
            byte fuIndicator = (byte)((nalHeader & 0x80) | nri | 28);
            
            int offset = 1; // 跳过 NAL header
            int remaining = nalData.Length - 1;
            bool firstFragment = true;
            
            while (remaining > 0)
            {
                int chunkSize = Math.Min(remaining, maxPayloadSize - 2); // 减去 FU indicator + FU header
                bool lastFragment = (remaining <= maxPayloadSize - 2);
                
                // FU header: S(1) + E(1) + R(1) + Type(5)
                byte fuHeader = nalType;
                if (firstFragment) fuHeader |= 0x80; // Start bit
                if (lastFragment) fuHeader |= 0x40;  // End bit
                
                // 创建包
                var packet = new byte[rtpHeaderSize + 2 + chunkSize];
                WriteRtpHeader(packet, seq, timestamp, lastFragment && isMarker);
                packet[rtpHeaderSize] = fuIndicator;
                packet[rtpHeaderSize + 1] = fuHeader;
                Array.Copy(nalData, offset, packet, rtpHeaderSize + 2, chunkSize);
                
                packets.Add((packet, seq));
                seq++;
                
                offset += chunkSize;
                remaining -= chunkSize;
                firstFragment = false;
            }
        }

        return packets;
    }

    /// <summary>
    /// 写入 RTP 头
    /// </summary>
    private void WriteRtpHeader(byte[] packet, ushort sequenceNumber, uint timestamp, bool isMarker)
    {
        packet[0] = 0x80;
        packet[1] = (byte)((isMarker ? 0x80 : 0x00) | 96);
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = 0x12;
        packet[9] = 0x34;
        packet[10] = 0x56;
        packet[11] = 0x78;
    }

    /// <summary>
    /// 创建音频 RTP 包
    /// AAC 按 RFC 3640 AAC-hbr 模式添加 AU-headers（与 SDP fmtp 中声明的
    /// sizeLength=13/indexLength=3 对应），其余编码为裸载荷。
    /// </summary>
    private byte[] CreateAudioRtpPacket(byte[] audioData, ushort sequenceNumber, uint timestamp)
    {
        byte payloadType = _audioCodec switch
        {
            AudioCodecType.PCMA => 8,
            AudioCodecType.PCMU => 0,
            AudioCodecType.AAC => 97,
            AudioCodecType.OPUS => 102,
            _ => 8
        };

        const int rtpHeaderSize = 12;
        bool isAac = _audioCodec == AudioCodecType.AAC;
        int auHeaderSize = isAac ? 4 : 0;
        var packet = new byte[rtpHeaderSize + auHeaderSize + audioData.Length];

        packet[0] = 0x80;
        // AAC/OPUS：每包一个完整帧，置 marker；G.711 连续流不置 marker
        bool marker = isAac || _audioCodec == AudioCodecType.OPUS;
        packet[1] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = 0x12;  packet[9] = 0x34;
        packet[10] = 0x56; packet[11] = 0x79;

        if (isAac)
        {
            // AU-headers-length: 16 bits（1 个 AU-header = 16 位）
            packet[12] = 0x00;
            packet[13] = 0x10;
            // AU-header: AU-size(13位) + AU-index(3位, 0)
            packet[14] = (byte)(audioData.Length >> 5);
            packet[15] = (byte)((audioData.Length & 0x1F) << 3);
        }

        Array.Copy(audioData, 0, packet, rtpHeaderSize + auHeaderSize, audioData.Length);
        return packet;
    }

    #endregion

    /// <summary>
    /// Parse audio track from MP4 file (AAC/MP3)
    /// </summary>
    private (List<byte[]> samples, int sampleRate, int channels, AudioCodecType codec, byte[]? asc)
        ParseMp4AudioTrack(byte[] fileData)
    {
        var samples = new List<byte[]>();
        int sampleRate = 44100;
        int channels = 2;
        AudioCodecType codec = AudioCodecType.AAC;
        byte[]? asc = null;

        try
        {
            var atoms = ParseAtoms(fileData, 0, fileData.Length);
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");
            if (moov == null) return (samples, sampleRate, channels, codec, asc);

            var moovAtoms = ParseAtoms(fileData, moov.DataOffset, moov.DataSize);
            var mdat = atoms.FirstOrDefault(a => a.Type == "mdat");
            if (mdat == null) return (samples, sampleRate, channels, codec, asc);

            // Find audio track
            foreach (var trak in moovAtoms.Where(a => a.Type == "trak"))
            {
                var trakAtoms = ParseAtoms(fileData, trak.DataOffset, trak.DataSize);
                var mdia = trakAtoms.FirstOrDefault(a => a.Type == "mdia");
                if (mdia == null) continue;

                var mdiaAtoms = ParseAtoms(fileData, mdia.DataOffset, mdia.DataSize);
                var hdlr = mdiaAtoms.FirstOrDefault(a => a.Type == "hdlr");
                if (hdlr == null || hdlr.DataSize < 12) continue;

                string handlerType = System.Text.Encoding.ASCII.GetString(fileData, (int)hdlr.DataOffset + 8, 4);
                if (handlerType != "soun") continue;

                System.Diagnostics.Debug.WriteLine("Found audio track!");

                var minf = mdiaAtoms.FirstOrDefault(a => a.Type == "minf");
                if (minf == null) continue;
                var minfAtoms = ParseAtoms(fileData, minf.DataOffset, minf.DataSize);
                var stbl = minfAtoms.FirstOrDefault(a => a.Type == "stbl");
                if (stbl == null) continue;
                var stblAtoms = ParseAtoms(fileData, stbl.DataOffset, stbl.DataSize);

                // Parse stsd for audio codec info
                var stsd = stblAtoms.FirstOrDefault(a => a.Type == "stsd");
                if (stsd != null)
                {
                    var audioInfo = ParseAudioStsd(fileData, stsd.DataOffset, stsd.DataSize);
                    sampleRate = audioInfo.sampleRate;
                    channels = audioInfo.channels;
                    codec = audioInfo.codec;
                    asc = audioInfo.asc;
                }

                // Parse sample tables
                var stsc = stblAtoms.FirstOrDefault(a => a.Type == "stsc");
                var stsz = stblAtoms.FirstOrDefault(a => a.Type == "stsz");
                var stco = stblAtoms.FirstOrDefault(a => a.Type == "stco" || a.Type == "co64");

                if (stsc == null || stsz == null || stco == null) continue;

                var sampleToChunk = ParseStsc(fileData, stsc.DataOffset, stsc.DataSize);
                var sampleSizes = ParseStsz(fileData, stsz.DataOffset, stsz.DataSize);
                var chunkOffsets = ParseChunkOffsets(fileData, stco.DataOffset, stco.DataSize, stco.Type);

                // Extract audio samples from mdat
                int sampleIndex = 0;
                for (int chunkIdx = 0; chunkIdx < chunkOffsets.Length; chunkIdx++)
                {
                    uint samplesInChunk = sampleToChunk[0].samplesPerChunk;
                    for (int i = sampleToChunk.Count - 1; i >= 0; i--)
                    {
                        if (chunkIdx + 1 >= sampleToChunk[i].firstChunk)
                        {
                            samplesInChunk = sampleToChunk[i].samplesPerChunk;
                            break;
                        }
                    }

                    long chunkOffset = chunkOffsets[chunkIdx];
                    for (int s = 0; s < samplesInChunk && sampleIndex < sampleSizes.Length; s++)
                    {
                        uint size = sampleSizes[sampleIndex];
                        if (chunkOffset + size <= fileData.Length && size > 0)
                        {
                            byte[] sampleData = new byte[size];
                            Array.Copy(fileData, chunkOffset, sampleData, 0, size);
                            samples.Add(sampleData);
                            chunkOffset += size;
                        }
                        sampleIndex++;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Audio: {samples.Count} frames, sampleRate={sampleRate}, channels={channels}");
                break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audio parsing error: {ex.Message}");
        }

        return (samples, sampleRate, channels, codec, asc);
    }

    /// <summary>
    /// 解析视频轨真实帧率（mdhd timescale / stts 平均采样时长）
    /// </summary>
    /// <returns>帧率；解析失败返回 0</returns>
    private int ParseMp4VideoFrameRate(byte[] fileData)
    {
        try
        {
            var atoms = ParseAtoms(fileData, 0, fileData.Length);
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");
            if (moov == null) return 0;

            foreach (var trak in ParseAtoms(fileData, moov.DataOffset, moov.DataSize).Where(a => a.Type == "trak"))
            {
                var mdia = ParseAtoms(fileData, trak.DataOffset, trak.DataSize).FirstOrDefault(a => a.Type == "mdia");
                if (mdia == null) continue;
                var mdiaAtoms = ParseAtoms(fileData, mdia.DataOffset, mdia.DataSize);

                var hdlr = mdiaAtoms.FirstOrDefault(a => a.Type == "hdlr");
                if (hdlr == null || hdlr.DataSize < 12) continue;
                string handlerType = System.Text.Encoding.ASCII.GetString(fileData, (int)hdlr.DataOffset + 8, 4);
                if (handlerType != "vide") continue;

                // mdhd: version(1)+flags(3) + creation/modification（v0 各4字节，v1 各8字节）+ timescale(4)
                var mdhd = mdiaAtoms.FirstOrDefault(a => a.Type == "mdhd");
                if (mdhd == null) continue;
                byte version = fileData[mdhd.DataOffset];
                long tsPos = mdhd.DataOffset + (version == 1 ? 4 + 16 : 4 + 8);
                uint timescale = ReadBe32(fileData, tsPos);
                if (timescale == 0) continue;

                var minf = mdiaAtoms.FirstOrDefault(a => a.Type == "minf");
                if (minf == null) continue;
                var stbl = ParseAtoms(fileData, minf.DataOffset, minf.DataSize).FirstOrDefault(a => a.Type == "stbl");
                if (stbl == null) continue;
                var stts = ParseAtoms(fileData, stbl.DataOffset, stbl.DataSize).FirstOrDefault(a => a.Type == "stts");
                if (stts == null || stts.DataSize < 8) continue;

                uint entryCount = ReadBe32(fileData, stts.DataOffset + 4);
                long totalSamples = 0, totalDelta = 0;
                long p = stts.DataOffset + 8;
                long sttsEnd = stts.DataOffset + stts.DataSize;
                for (uint i = 0; i < entryCount && p + 8 <= sttsEnd; i++, p += 8)
                {
                    uint count = ReadBe32(fileData, p);
                    uint delta = ReadBe32(fileData, p + 4);
                    totalSamples += count;
                    totalDelta += (long)count * delta;
                }

                if (totalSamples == 0 || totalDelta == 0) continue;
                return (int)Math.Round(timescale * (double)totalSamples / totalDelta);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ParseMp4VideoFrameRate error: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// AAC 采样率索引表（AudioSpecificConfig samplingFrequencyIndex）
    /// </summary>
    private static readonly int[] AacSampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>
    /// Parse audio stsd entry (mp4a format)
    /// 按 ISO 14496-12/-1 规范解析 AudioSampleEntry 固定字段和 esds 中的
    /// AudioSpecificConfig（旧实现按字节模式盲扫，会解析出错误的采样率，
    /// 导致 SDP 时钟/config 与实际码流不符，VLC 等播放器无声）。
    /// </summary>
    private static (int sampleRate, int channels, AudioCodecType codec, byte[]? asc) ParseAudioStsd(
        byte[] data, long offset, long size)
    {
        var fallback = (44100, 2, AudioCodecType.AAC, (byte[]?)null);
        long end = Math.Min(offset + size, data.Length);

        // stsd: FullBox 头(4) + entry_count(4)，随后是 sample entry
        long entryStart = offset + 8;
        if (entryStart + 36 > end)
            return fallback;

        string entryType = System.Text.Encoding.ASCII.GetString(data, (int)entryStart + 4, 4);
        if (entryType != "mp4a")
            return fallback; // 非 AAC 类条目（samr/mp3 等），维持默认

        uint entrySize = ReadBe32(data, entryStart);
        long entryEnd = Math.Min(entryStart + entrySize, end);

        // AudioSampleEntry 固定布局（相对 entryStart）：
        // 8 box头 + 6 reserved + 2 data_reference_index
        // + 2 version + 2 revision + 4 vendor
        // + 2 channelcount(24) + 2 samplesize + 2 pre_defined + 2 reserved
        // + 4 samplerate 16.16(32)
        int channels = ReadBe16(data, entryStart + 24);
        int sampleRate = (int)(ReadBe32(data, entryStart + 32) >> 16);
        int qtVersion = ReadBe16(data, entryStart + 16);

        // QuickTime v1/v2 在固定字段后有额外数据
        long childPos = entryStart + 36;
        if (qtVersion == 1) childPos += 16;
        else if (qtVersion == 2) childPos += 36;

        // 遍历子 box 查找 esds（可能嵌在 QuickTime 'wave' box 内）
        byte[]? asc = FindEsdsAsc(data, childPos, entryEnd);

        // ASC 是权威来源，覆盖 box 字段
        if (asc is { Length: >= 2 })
        {
            int freqIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
            if (freqIndex == 15)
            {
                if (asc.Length >= 5)
                {
                    sampleRate = ((asc[1] & 0x7F) << 17) | (asc[2] << 9) | (asc[3] << 1) | (asc[4] >> 7);
                    int chanCfg = (asc[4] >> 3) & 0x0F;
                    if (chanCfg > 0) channels = chanCfg;
                }
            }
            else
            {
                sampleRate = AacSampleRates[freqIndex];
                int chanCfg = (asc[1] >> 3) & 0x0F;
                if (chanCfg > 0) channels = chanCfg;
            }
        }

        if (sampleRate < 7350 || sampleRate > 96000) sampleRate = 44100;
        if (channels < 1 || channels > 8) channels = 2;

        return (sampleRate, channels, AudioCodecType.AAC, asc);
    }

    /// <summary>
    /// 在 box 范围内查找 esds 并提取 AudioSpecificConfig
    /// </summary>
    private static byte[]? FindEsdsAsc(byte[] data, long pos, long end)
    {
        while (pos + 8 <= end)
        {
            uint boxSize = ReadBe32(data, pos);
            if (boxSize < 8)
                break;

            string boxType = System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4);
            long boxEnd = Math.Min(pos + boxSize, end);

            if (boxType == "esds")
            {
                // esds: box头(8) + FullBox version/flags(4)，随后是描述符
                return ParseEsDescriptorForAsc(data, pos + 12, boxEnd);
            }
            if (boxType == "wave")
            {
                var nested = FindEsdsAsc(data, pos + 8, boxEnd);
                if (nested != null)
                    return nested;
            }

            pos += boxSize;
        }

        return null;
    }

    /// <summary>
    /// 解析 MPEG-4 ES 描述符链，提取 DecoderSpecificInfo（tag 0x05，即 AudioSpecificConfig）
    /// </summary>
    private static byte[]? ParseEsDescriptorForAsc(byte[] data, long pos, long end)
    {
        while (pos < end)
        {
            byte tag = data[pos++];
            long len = ReadDescriptorLength(data, ref pos, end);
            long descEnd = Math.Min(pos + len, end);

            switch (tag)
            {
                case 0x03: // ES_Descriptor: ES_ID(2) + flags(1) + 可选字段，随后是子描述符
                    if (pos + 3 > end) return null;
                    pos += 2;
                    byte esFlags = data[pos++];
                    if ((esFlags & 0x80) != 0) pos += 2;                        // streamDependence
                    if ((esFlags & 0x40) != 0 && pos < end) pos += 1 + data[pos]; // URL
                    if ((esFlags & 0x20) != 0) pos += 2;                        // OCRstream
                    continue; // 进入子描述符

                case 0x04: // DecoderConfigDescriptor: OTI(1)+streamType(1)+bufferSize(3)+maxBr(4)+avgBr(4)
                    pos += 13;
                    continue; // 进入子描述符

                case 0x05: // DecoderSpecificInfo = AudioSpecificConfig
                    if (descEnd <= pos) return null;
                    var asc = new byte[descEnd - pos];
                    Array.Copy(data, pos, asc, 0, asc.Length);
                    return asc;

                default:
                    pos = descEnd;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// 读取 MPEG-4 描述符的变长 length 字段（每字节 7 位，最高位为续位）
    /// </summary>
    private static long ReadDescriptorLength(byte[] data, ref long pos, long end)
    {
        long length = 0;
        for (int i = 0; i < 4 && pos < end; i++)
        {
            byte b = data[pos++];
            length = (length << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }
        return length;
    }

    private static uint ReadBe32(byte[] data, long pos)
    {
        return (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
    }

    private static int ReadBe16(byte[] data, long pos)
    {
        return (data[pos] << 8) | data[pos + 1];
    }

    public void Dispose()
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
    }
}

/// <summary>
/// RTSP 拉流源
/// 从远程 RTSP 服务器拉取流并转发
/// </summary>
public class RtspPullMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private readonly StreamManager? _owner;
    private bool _running;
    private Cyaim.RTSPClient.RTSPSession? _rtspSession;
    private readonly RtpPacketBroadcaster _broadcaster = new();

    /// <summary>
    /// SPS 数据
    /// </summary>
    public byte[]? SpsData { get; private set; }

    /// <summary>
    /// PPS 数据
    /// </summary>
    public byte[]? PpsData { get; private set; }

    public RtspPullMediaSource(StreamConfig config, StreamManager? owner = null)
    {
        _config = config;
        _owner = owner;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _running = true;

        if (string.IsNullOrEmpty(_config.Source))
        {
            return;
        }

        try
        {
            // 创建 RTSP 客户端会话
            _rtspSession = new Cyaim.RTSPClient.RTSPSession(_config.Source);

            // 订阅数据接收事件
            _rtspSession.DataReceived += OnDataReceived;

            // 连接到远程服务器
            await _rtspSession.ConnectAsync(ct);

            // 执行 RTSP 握手
            await _rtspSession.OptionsAsync(ct);
            await _rtspSession.DescribeAsync(ct: ct);

            // SETUP 视频轨道 (使用 TCP interleaved 模式)
            await _rtspSession.SetupAsync("trackID=0", "RTP/AVP/TCP;unicast;interleaved=0-1", ct: ct);

            // 如果有音频轨道，也 SETUP
            var audioMedia = _rtspSession.SDP?.GetAudioMedia();
            if (audioMedia != null)
            {
                await _rtspSession.SetupAsync("trackID=1", "RTP/AVP/TCP;unicast;interleaved=2-3", ct: ct);
            }

            // 将上游 SDP 的音频参数同步到本地 StreamInfo，保证转发出去的 SDP 与实际数据一致
            SyncStreamInfoFromSdp(audioMedia);

            // 开始播放
            await _rtspSession.PlayAsync(ct: ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RTSP pull source error: {ex.Message}");
            _running = false;
        }
    }

    /// <summary>
    /// 根据上游 SDP 更新本地流信息（音频编码、采样率、声道数）
    /// </summary>
    private void SyncStreamInfoFromSdp(Cyaim.RTSPClient.Media.MediaDescription? audioMedia)
    {
        var info = _owner?.GetStream(_config.Path);
        if (info == null)
            return;

        if (audioMedia == null)
        {
            info.AudioCodec = AudioCodecType.None;
            return;
        }

        var codec = audioMedia.GetPrimaryCodec();
        if (codec == null)
            return;

        info.AudioCodec = codec.EncodingName?.ToUpperInvariant() switch
        {
            "PCMA" => AudioCodecType.PCMA,
            "PCMU" => AudioCodecType.PCMU,
            "MPEG4-GENERIC" => AudioCodecType.AAC,
            "OPUS" => AudioCodecType.OPUS,
            "G722" => AudioCodecType.G722,
            _ => info.AudioCodec
        };
        if (codec.ClockRate > 0)
            info.SampleRate = codec.ClockRate;
        if (codec.Channels > 0)
            info.Channels = codec.Channels;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _running = false;

        if (_rtspSession != null)
        {
            try
            {
                await _rtspSession.TeardownAsync(ct: ct);
            }
            catch { }

            _rtspSession.DataReceived -= OnDataReceived;
            _rtspSession.Dispose();
            _rtspSession = null;
        }
    }

    private void OnDataReceived(object? sender, Cyaim.RTSPClient.Events.RtpDataReceivedEventArgs e)
    {
        if (!_running)
            return;

        try
        {
            // 将客户端 RTP 包转换为服务器 RTP 包
            var packet = new RtpPacket
            {
                Data = e.Packet.Raw,
                TrackId = e.Packet.TrackId,
                Timestamp = e.Packet.Timestamp,
                SequenceNumber = e.Packet.SequenceNumber,
                IsKeyFrame = e.Packet.Marker // 使用 marker bit 作为关键帧指示
            };

            // 广播给所有订阅者（非阻塞）
            _broadcaster.Publish(packet);
        }
        catch
        {
            // 忽略写入错误
        }
    }

    public IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct)
    {
        return _broadcaster.SubscribeAsync(ct);
    }

    public void Dispose()
    {
        _running = false;
        _rtspSession?.Dispose();
        _broadcaster.Complete();
    }
}

/// <summary>
/// 测试图案源
/// 生成有效的 H.264 RTP 包用于测试
/// 使用程序化生成的有效 H.264 NAL 单元
/// </summary>
public class TestPatternMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private readonly StreamManager? _owner;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;
    private RtpPacketBroadcaster? _broadcaster;
    private CancellationTokenSource? _producerCts;

    // 音频状态（PCMA 测试音）
    private ushort _audioSequenceNumber;
    private uint _audioTimestamp;
    private double _tonePhase;

    // 当前 GOP 内的帧号（frame_num，模 16）
    private int _gopFrameNum;

    /// <summary>
    /// SPS 数据
    /// </summary>
    public byte[]? SpsData { get; private set; }

    /// <summary>
    /// PPS 数据
    /// </summary>
    public byte[]? PpsData { get; private set; }

    public TestPatternMediaSource(StreamConfig config, StreamManager? owner = null)
    {
        _config = config;
        _owner = owner;
    }

    private bool AudioEnabled => _config.EnableAudio && _config.AudioCodec != AudioCodecType.None;

    public Task StartAsync(CancellationToken ct)
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;

        // 使用合规的 H.264 测试流参数集
        SpsData = H264TestStream.Sps;
        PpsData = H264TestStream.Pps;

        // 测试源只会产生 PCMA 测试音，保证 SDP 与实际数据一致
        MediaSourceAudioHelper.ApplyTestToneAudioConfig(_owner, _config, AudioEnabled);

        _broadcaster = new RtpPacketBroadcaster();
        _producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ProduceAsync(_producerCts.Token), _producerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct)
    {
        // 未启动时返回空流而不是抛异常（PLAY 层已用 455 拦截未启动的流）
        var broadcaster = _broadcaster;
        if (broadcaster == null)
            return EmptyPackets();
        return broadcaster.SubscribeAsync(ct);
    }

    private static async IAsyncEnumerable<RtpPacket> EmptyPackets()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// 生产者：按各自时钟生成视频帧和音频帧并广播
    /// </summary>
    private async Task ProduceAsync(CancellationToken ct)
    {
        try
        {
            int framerate = Math.Max(1, _config.Framerate);
            bool audioEnabled = AudioEnabled;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long videoIntervalTicks = System.Diagnostics.Stopwatch.Frequency / framerate;
            long audioIntervalTicks = System.Diagnostics.Stopwatch.Frequency * G711Audio.SamplesPerFrame / G711Audio.SampleRate;
            long nextVideoTick = 0;
            long nextAudioTick = 0;
            int frameCount = 0;

            while (_running && !ct.IsCancellationRequested)
            {
                long now = stopwatch.ElapsedTicks;
                bool videoDue = now >= nextVideoTick;
                bool audioDue = audioEnabled && now >= nextAudioTick;

                if (!videoDue && !audioDue)
                {
                    long nextTick = audioEnabled ? Math.Min(nextVideoTick, nextAudioTick) : nextVideoTick;
                    int waitMs = (int)((nextTick - now) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        await Task.Delay(waitMs, ct);
                    continue;
                }

                if (videoDue)
                {
                    nextVideoTick += videoIntervalTicks;
                    bool isKeyFrame = frameCount % (framerate * 2) == 0; // 每2秒一个关键帧

                    if (isKeyFrame)
                    {
                        // 关键帧: 发送 SPS + PPS + IDR（IDR 为 I_PCM 大帧，自动 FU-A 分片）
                        _gopFrameNum = 0;
                        PublishNal(H264TestStream.Sps, isMarker: false, isKeyFrame: true);
                        PublishNal(H264TestStream.Pps, isMarker: false, isKeyFrame: false);
                        PublishNal(H264TestStream.IdrFrame, isMarker: true, isKeyFrame: true);
                    }
                    else
                    {
                        PublishNal(H264TestStream.BuildPFrame(_gopFrameNum), isMarker: true, isKeyFrame: false);
                    }

                    _gopFrameNum = (_gopFrameNum + 1) % 16;
                    _timestamp += (uint)(90000 / framerate);
                    frameCount++;
                }

                if (audioDue)
                {
                    nextAudioTick += audioIntervalTicks;

                    var toneFrame = G711Audio.GenerateToneFrame(ref _tonePhase);
                    var audioPacket = G711Audio.CreateRtpPacket(toneFrame, _audioSequenceNumber, _audioTimestamp);
                    _broadcaster!.Publish(new RtpPacket
                    {
                        Data = audioPacket,
                        TrackId = 1,
                        Timestamp = _audioTimestamp,
                        SequenceNumber = _audioSequenceNumber,
                        IsKeyFrame = false
                    });
                    _audioSequenceNumber++;
                    _audioTimestamp += (uint)G711Audio.SamplesPerFrame;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _broadcaster?.Complete();
        }
    }

    /// <summary>
    /// 将 NAL 打包（必要时 FU-A 分片）并广播
    /// </summary>
    private void PublishNal(byte[] nalData, bool isMarker, bool isKeyFrame)
    {
        foreach (var (data, seq) in H264TestStream.Packetize(nalData, _sequenceNumber, _timestamp, isMarker))
        {
            _broadcaster!.Publish(new RtpPacket
            {
                Data = data,
                TrackId = 0,
                Timestamp = _timestamp,
                SequenceNumber = seq,
                IsKeyFrame = isKeyFrame
            });
            _sequenceNumber = (ushort)(seq + 1);
        }
    }

    public void Dispose()
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
    }
}

/// <summary>
/// H.264 位流写入器
/// </summary>
internal class BitWriter
{
    private readonly List<byte> _buffer = new();
    private int _currentByte;
    private int _bitPosition = 7; // 从高位到低位

    public void WriteBit(bool bit)
    {
        if (bit)
            _currentByte |= (1 << _bitPosition);

        _bitPosition--;

        if (_bitPosition < 0)
        {
            _buffer.Add((byte)_currentByte);
            _currentByte = 0;
            _bitPosition = 7;
        }
    }

    public void WriteBits(int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            WriteBit(((value >> i) & 1) == 1);
        }
    }

    /// <summary>
    /// 写入无符号指数哥伦布编码
    /// </summary>
    public void WriteExpGolomb(uint value)
    {
        value++; // 无符号 exp-golomb 编码: codeNum = value + 1

        // 计算需要的位数
        int bits = 0;
        uint temp = value;
        while (temp > 0)
        {
            bits++;
            temp >>= 1;
        }

        // 写入前导零
        for (int i = 0; i < bits - 1; i++)
            WriteBit(false);

        // 写入值
        for (int i = bits - 1; i >= 0; i--)
            WriteBit(((value >> i) & 1) == 1);
    }

    /// <summary>
    /// 写入有符号指数哥伦布编码
    /// </summary>
    public void WriteSignedExpGolomb(int value)
    {
        uint codeNum;
        if (value > 0)
            codeNum = (uint)(2 * value - 1);
        else
            codeNum = (uint)(-2 * value);

        WriteExpGolomb(codeNum);
    }

    /// <summary>
    /// 补零至字节对齐（用于 I_PCM 的 pcm_alignment_zero_bit 等）
    /// </summary>
    public void AlignToByte()
    {
        while (_bitPosition != 7)
        {
            WriteBit(false);
        }
    }

    public void Flush()
    {
        if (_bitPosition < 7)
        {
            _buffer.Add((byte)_currentByte);
            _currentByte = 0;
            _bitPosition = 7;
        }
    }

    public byte[] ToArray()
    {
        return _buffer.ToArray();
    }
}

/// <summary>
/// RTMP 推流源
/// 接受外部 RTMP 推流并转发为 RTSP
/// </summary>
public class RtmpPushMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;

    public byte[]? SpsData { get; private set; }
    public byte[]? PpsData { get; private set; }

    public RtmpPushMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        // TODO: 实现 RTMP 服务器，监听推流连接
        System.Diagnostics.Debug.WriteLine($"RTMP push source: {_config.Path} (listening on port {_config.Source ?? "1935"})");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Placeholder: 等待 RTMP 推流数据
        while (_running && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
        }
        yield break;
    }

    public void Dispose() { _running = false; }
}

/// <summary>
/// 摄像头源
/// 从本地摄像头采集视频
/// </summary>
public class CameraMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private readonly StreamManager? _owner;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;
    private RtpPacketBroadcaster? _broadcaster;
    private CancellationTokenSource? _producerCts;

    // 音频状态（PCMA 测试音）
    private ushort _audioSequenceNumber;
    private uint _audioTimestamp;
    private double _tonePhase;

    // 当前 GOP 内的帧号（frame_num，模 16）
    private int _gopFrameNum;

    public byte[]? SpsData { get; private set; }
    public byte[]? PpsData { get; private set; }

    public CameraMediaSource(StreamConfig config, StreamManager? owner = null)
    {
        _config = config;
        _owner = owner;
    }

    private bool AudioEnabled => _config.EnableAudio && _config.AudioCodec != AudioCodecType.None;

    public Task StartAsync(CancellationToken ct)
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;
        SpsData = H264TestStream.Sps;
        PpsData = H264TestStream.Pps;
        System.Diagnostics.Debug.WriteLine($"Camera source: {_config.Path} (device: {_config.Source ?? "default"})");

        MediaSourceAudioHelper.ApplyTestToneAudioConfig(_owner, _config, AudioEnabled);

        _broadcaster = new RtpPacketBroadcaster();
        _producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ProduceAsync(_producerCts.Token), _producerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct)
    {
        // 未启动时返回空流而不是抛异常（PLAY 层已用 455 拦截未启动的流）
        var broadcaster = _broadcaster;
        if (broadcaster == null)
            return EmptyPackets();
        return broadcaster.SubscribeAsync(ct);
    }

    private static async IAsyncEnumerable<RtpPacket> EmptyPackets()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        // TODO: 使用 DirectShow/MediaFoundation 采集摄像头
        // Fallback: 使用测试图案 + PCMA 测试音
        try
        {
            int framerate = Math.Max(1, _config.Framerate);
            bool audioEnabled = AudioEnabled;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long videoIntervalTicks = System.Diagnostics.Stopwatch.Frequency / framerate;
            long audioIntervalTicks = System.Diagnostics.Stopwatch.Frequency * G711Audio.SamplesPerFrame / G711Audio.SampleRate;
            long nextVideoTick = 0;
            long nextAudioTick = 0;
            int frameCount = 0;

            while (_running && !ct.IsCancellationRequested)
            {
                long now = stopwatch.ElapsedTicks;
                bool videoDue = now >= nextVideoTick;
                bool audioDue = audioEnabled && now >= nextAudioTick;

                if (!videoDue && !audioDue)
                {
                    long nextTick = audioEnabled ? Math.Min(nextVideoTick, nextAudioTick) : nextVideoTick;
                    int waitMs = (int)((nextTick - now) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        await Task.Delay(waitMs, ct);
                    continue;
                }

                if (videoDue)
                {
                    nextVideoTick += videoIntervalTicks;
                    bool isKeyFrame = frameCount % (framerate * 2) == 0;

                    if (isKeyFrame)
                    {
                        _gopFrameNum = 0;
                        PublishNal(H264TestStream.Sps, isMarker: false, isKeyFrame: true);
                        PublishNal(H264TestStream.Pps, isMarker: false, isKeyFrame: false);
                        PublishNal(H264TestStream.IdrFrame, isMarker: true, isKeyFrame: true);
                    }
                    else
                    {
                        PublishNal(H264TestStream.BuildPFrame(_gopFrameNum), isMarker: true, isKeyFrame: false);
                    }

                    _gopFrameNum = (_gopFrameNum + 1) % 16;
                    _timestamp += (uint)(90000 / framerate);
                    frameCount++;
                }

                if (audioDue)
                {
                    nextAudioTick += audioIntervalTicks;

                    var toneFrame = G711Audio.GenerateToneFrame(ref _tonePhase);
                    var audioPacket = G711Audio.CreateRtpPacket(toneFrame, _audioSequenceNumber, _audioTimestamp);
                    _broadcaster!.Publish(new RtpPacket
                    {
                        Data = audioPacket,
                        TrackId = 1,
                        Timestamp = _audioTimestamp,
                        SequenceNumber = _audioSequenceNumber,
                        IsKeyFrame = false
                    });
                    _audioSequenceNumber++;
                    _audioTimestamp += (uint)G711Audio.SamplesPerFrame;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _broadcaster?.Complete();
        }
    }

    /// <summary>
    /// 将 NAL 打包（必要时 FU-A 分片）并广播
    /// </summary>
    private void PublishNal(byte[] nalData, bool isMarker, bool isKeyFrame)
    {
        foreach (var (data, seq) in H264TestStream.Packetize(nalData, _sequenceNumber, _timestamp, isMarker))
        {
            _broadcaster!.Publish(new RtpPacket
            {
                Data = data,
                TrackId = 0,
                Timestamp = _timestamp,
                SequenceNumber = seq,
                IsKeyFrame = isKeyFrame
            });
            _sequenceNumber = (ushort)(seq + 1);
        }
    }

    public void Dispose()
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
    }
}

/// <summary>
/// 屏幕捕获源
/// 捕获屏幕内容作为视频流
/// </summary>
public class ScreenCaptureMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private readonly StreamManager? _owner;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;
    private RtpPacketBroadcaster? _broadcaster;
    private CancellationTokenSource? _producerCts;

    // 音频状态（PCMA 测试音）
    private ushort _audioSequenceNumber;
    private uint _audioTimestamp;
    private double _tonePhase;

    // 当前 GOP 内的帧号（frame_num，模 16）
    private int _gopFrameNum;

    public byte[]? SpsData { get; private set; }
    public byte[]? PpsData { get; private set; }

    public ScreenCaptureMediaSource(StreamConfig config, StreamManager? owner = null)
    {
        _config = config;
        _owner = owner;
    }

    private bool AudioEnabled => _config.EnableAudio && _config.AudioCodec != AudioCodecType.None;

    public Task StartAsync(CancellationToken ct)
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;
        SpsData = H264TestStream.Sps;
        PpsData = H264TestStream.Pps;
        System.Diagnostics.Debug.WriteLine($"Screen capture source: {_config.Path}");

        MediaSourceAudioHelper.ApplyTestToneAudioConfig(_owner, _config, AudioEnabled);

        _broadcaster = new RtpPacketBroadcaster();
        _producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ProduceAsync(_producerCts.Token), _producerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RtpPacket> GetPacketsAsync(CancellationToken ct)
    {
        // 未启动时返回空流而不是抛异常（PLAY 层已用 455 拦截未启动的流）
        var broadcaster = _broadcaster;
        if (broadcaster == null)
            return EmptyPackets();
        return broadcaster.SubscribeAsync(ct);
    }

    private static async IAsyncEnumerable<RtpPacket> EmptyPackets()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        // TODO: 使用 Windows.Graphics.Capture 或 DXGI 屏幕捕获
        // Fallback: 使用测试图案 + PCMA 测试音
        try
        {
            int framerate = Math.Max(1, _config.Framerate);
            bool audioEnabled = AudioEnabled;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long videoIntervalTicks = System.Diagnostics.Stopwatch.Frequency / framerate;
            long audioIntervalTicks = System.Diagnostics.Stopwatch.Frequency * G711Audio.SamplesPerFrame / G711Audio.SampleRate;
            long nextVideoTick = 0;
            long nextAudioTick = 0;
            int frameCount = 0;

            while (_running && !ct.IsCancellationRequested)
            {
                long now = stopwatch.ElapsedTicks;
                bool videoDue = now >= nextVideoTick;
                bool audioDue = audioEnabled && now >= nextAudioTick;

                if (!videoDue && !audioDue)
                {
                    long nextTick = audioEnabled ? Math.Min(nextVideoTick, nextAudioTick) : nextVideoTick;
                    int waitMs = (int)((nextTick - now) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        await Task.Delay(waitMs, ct);
                    continue;
                }

                if (videoDue)
                {
                    nextVideoTick += videoIntervalTicks;
                    bool isKeyFrame = frameCount % (framerate * 2) == 0;

                    if (isKeyFrame)
                    {
                        _gopFrameNum = 0;
                        PublishNal(H264TestStream.Sps, isMarker: false, isKeyFrame: true);
                        PublishNal(H264TestStream.Pps, isMarker: false, isKeyFrame: false);
                        PublishNal(H264TestStream.IdrFrame, isMarker: true, isKeyFrame: true);
                    }
                    else
                    {
                        PublishNal(H264TestStream.BuildPFrame(_gopFrameNum), isMarker: true, isKeyFrame: false);
                    }

                    _gopFrameNum = (_gopFrameNum + 1) % 16;
                    _timestamp += (uint)(90000 / framerate);
                    frameCount++;
                }

                if (audioDue)
                {
                    nextAudioTick += audioIntervalTicks;

                    var toneFrame = G711Audio.GenerateToneFrame(ref _tonePhase);
                    var audioPacket = G711Audio.CreateRtpPacket(toneFrame, _audioSequenceNumber, _audioTimestamp);
                    _broadcaster!.Publish(new RtpPacket
                    {
                        Data = audioPacket,
                        TrackId = 1,
                        Timestamp = _audioTimestamp,
                        SequenceNumber = _audioSequenceNumber,
                        IsKeyFrame = false
                    });
                    _audioSequenceNumber++;
                    _audioTimestamp += (uint)G711Audio.SamplesPerFrame;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _broadcaster?.Complete();
        }
    }

    /// <summary>
    /// 将 NAL 打包（必要时 FU-A 分片）并广播
    /// </summary>
    private void PublishNal(byte[] nalData, bool isMarker, bool isKeyFrame)
    {
        foreach (var (data, seq) in H264TestStream.Packetize(nalData, _sequenceNumber, _timestamp, isMarker))
        {
            _broadcaster!.Publish(new RtpPacket
            {
                Data = data,
                TrackId = 0,
                Timestamp = _timestamp,
                SequenceNumber = seq,
                IsKeyFrame = isKeyFrame
            });
            _sequenceNumber = (ushort)(seq + 1);
        }
    }

    public void Dispose()
    {
        _running = false;
        _producerCts?.Cancel();
        _broadcaster?.Complete();
    }
}
