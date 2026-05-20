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
            MediaSourceType.RtspPull => new RtspPullMediaSource(config),
            MediaSourceType.TestPattern => new TestPatternMediaSource(config),
            MediaSourceType.RtmpPush => new RtmpPushMediaSource(config),
            MediaSourceType.Camera => new CameraMediaSource(config),
            MediaSourceType.Screen => new ScreenCaptureMediaSource(config),
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
    private Channel<RtpPacket>? _packetChannel;

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        _packetChannel = Channel.CreateBounded<RtpPacket>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,  // 超载时丢旧包，避免生产者阻塞
            SingleWriter = true,
            SingleReader = false
        });

        _producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ProducePacketsAsync(_producerCts.Token), _producerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _producerCts?.Cancel();
        _packetChannel?.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_packetChannel == null)
            yield break;

        await foreach (var packet in _packetChannel.Reader.ReadAllAsync(ct))
        {
            yield return packet;
        }
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

            List<byte[]> nalUnits;

            if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: Parsing MP4 file...");
                nalUnits = await ParseMp4FileAsync(_config.Source, ct);
                System.Diagnostics.Debug.WriteLine($"FileMediaSource: MP4 parsing complete, {nalUnits.Count} NAL units");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("FileMediaSource: Parsing Annex-B file...");
                byte[] fileData = await File.ReadAllBytesAsync(_config.Source, ct);
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
                var audioResult = await ParseMp4AudioTrackAsync(_config.Source, ct);
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
            long frameIntervalTicks = (long)(System.Diagnostics.Stopwatch.Frequency / (double)_config.Framerate);
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

                // 发送视频包
                if (videoDue)
                {
                    nextVideoTick += frameIntervalTicks;

                    byte[] nalData = videoNalUnits[nalIndex];
                    byte nalType = (byte)(nalData[0] & 0x1F);
                    bool isKeyFrame = nalType == NAL_IDR;

                    if (isKeyFrame && spsData != null && ppsData != null)
                    {
                        var spsPkts = CreateRtpPackets(spsData, _videoSequenceNumber, _videoTimestamp, false);
                        foreach (var (data, seq) in spsPkts)
                        {
                            _packetChannel!.Writer.TryWrite(new RtpPacket
                            {
                                Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                                SequenceNumber = seq, IsKeyFrame = false
                            });
                            _videoSequenceNumber = (ushort)(seq + 1);  // 始终递增，丢包时有 gap
                        }
                        var ppsPkts = CreateRtpPackets(ppsData, _videoSequenceNumber, _videoTimestamp, false);
                        foreach (var (data, seq) in ppsPkts)
                        {
                            _packetChannel!.Writer.TryWrite(new RtpPacket
                            {
                                Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                                SequenceNumber = seq, IsKeyFrame = false
                            });
                            _videoSequenceNumber = (ushort)(seq + 1);
                        }
                    }

                    bool isLastNal = nalIndex + 1 >= videoNalUnits.Count
                        || (videoNalUnits[nalIndex + 1][0] & 0x1F) == NAL_IDR;
                    var rtpPkts = CreateRtpPackets(nalData, _videoSequenceNumber, _videoTimestamp, isLastNal);
                    foreach (var (data, seq) in rtpPkts)
                    {
                        _packetChannel!.Writer.TryWrite(new RtpPacket
                        {
                            Data = data, TrackId = 0, Timestamp = _videoTimestamp,
                            SequenceNumber = seq, IsKeyFrame = isKeyFrame
                        });
                        _videoSequenceNumber = (ushort)(seq + 1);
                    }

                    if (isKeyFrame || nalType == NAL_SLICE)
                    {
                        _videoTimestamp += (uint)(90000 / _config.Framerate);
                        frameCount++;
                    }

                    nalIndex++;
                    if (nalIndex >= videoNalUnits.Count)
                    {
                        if (loop)
                        {
                            nalIndex = 0;
                            audioIndex = 0;
                            frameCount = 0;
                            nextVideoTick = stopwatch.ElapsedTicks;
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

                        _packetChannel!.Writer.TryWrite(new RtpPacket
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
            _packetChannel?.Writer.TryComplete();
        }
    }

    #region MP4 Demuxer

    /// <summary>
    /// 解析 MP4 文件，提取 H.264 NAL 单元
    /// </summary>
    private async Task<List<byte[]>> ParseMp4FileAsync(string filePath, CancellationToken ct)
    {
        var nalUnits = new List<byte[]>();

        try
        {
            byte[] fileData = await File.ReadAllBytesAsync(filePath, ct);
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

        // AAC-hbr mode: raw AAC frames, no AU-headers (declared in SDP fmtp)
        int rtpHeaderSize = 12;
        var packet = new byte[rtpHeaderSize + audioData.Length];

        packet[0] = 0x80;
        packet[1] = (byte)(payloadType & 0x7F);
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = 0x12;  packet[9] = 0x34;
        packet[10] = 0x56; packet[11] = 0x79;

        Array.Copy(audioData, 0, packet, rtpHeaderSize, audioData.Length);
        return packet;
    }

    #endregion

    /// <summary>
    /// Parse audio track from MP4 file (AAC/MP3)
    /// </summary>
    private async Task<(List<byte[]> samples, int sampleRate, int channels, AudioCodecType codec)> 
        ParseMp4AudioTrackAsync(string filePath, CancellationToken ct)
    {
        var samples = new List<byte[]>();
        int sampleRate = 44100;
        int channels = 2;
        AudioCodecType codec = AudioCodecType.AAC;

        try
        {
            byte[] fileData = await File.ReadAllBytesAsync(filePath, ct);
            var atoms = ParseAtoms(fileData, 0, fileData.Length);
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");
            if (moov == null) return (samples, sampleRate, channels, codec);

            var moovAtoms = ParseAtoms(fileData, moov.DataOffset, moov.DataSize);
            var mdat = atoms.FirstOrDefault(a => a.Type == "mdat");
            if (mdat == null) return (samples, sampleRate, channels, codec);

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

        return (samples, sampleRate, channels, codec);
    }

    /// <summary>
    /// Parse audio stsd entry (mp4a format)
    /// </summary>
    private static (int sampleRate, int channels, AudioCodecType codec) ParseAudioStsd(
        byte[] data, long offset, long size)
    {
        long pos = offset + 8; // skip version/flags/entry_count
        if (pos + 4 > data.Length) return (44100, 2, AudioCodecType.AAC);

        uint entrySize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;

        // Scan for mp4a atom within the entry
        long entryEnd = offset + 8 + entrySize;
        for (long scan = pos; scan < entryEnd - 8; scan++)
        {
            if (data[scan] == 'm' && data[scan + 1] == 'p' && data[scan + 2] == '4' && data[scan + 3] == 'a')
            {
                // Found mp4a - ES descriptor follows
                // Quick parse: look for sample rate (4 bytes) and channels (2 bytes) 
                // typically at fixed offset 24 within mp4a
                long mp4aStart = scan - 4; // atom size is before the type
                if (mp4aStart >= 0)
                {
                    uint mp4aSize = (uint)((data[mp4aStart] << 24) | (data[mp4aStart + 1] << 16) |
                                            (data[mp4aStart + 2] << 8) | data[mp4aStart + 3]);
                    long mp4aEnd = mp4aStart + mp4aSize;
                    
                    // Look for esds atom within mp4a
                    for (long es = scan + 4; es < mp4aEnd - 8; es++)
                    {
                        if (data[es] == 'e' && data[es + 1] == 's' && data[es + 2] == 'd' && data[es + 3] == 's')
                        {
                            long esdsDataStart = es + 8;
                            long esdsDataEnd = es + ((data[es - 4] << 24) | (data[es - 3] << 16) |
                                                       (data[es - 2] << 8) | data[es - 1]);
                            
                            // ES_Descriptor: tag 0x03
                            // DecoderConfigDescriptor: tag 0x04
                            // DecoderSpecificInfo: tag 0x05
                            for (long p = esdsDataStart + 4; p < esdsDataEnd - 2; p++)
                            {
                                // Look for DecoderConfigDescriptor tag
                                if (data[p] == 0x04)
                                {
                                    long dcdStart = p;
                                    // Sample rate at offset +3 (3 bytes): objectTypeIndication + flags + sampleRate
                                    // ... actual layout is complex, simplify by looking for common patterns
                                    
                                    // Try to read 4-byte sample rate at common offset
                                    if (dcdStart + 15 < esdsDataEnd)
                                    {
                                        int sr = (data[dcdStart + 13] << 8) | data[dcdStart + 14];
                                        if (sr > 0) return (sr, 2, AudioCodecType.AAC);
                                    }
                                }
                            }
                        }
                    }
                }
                break;
            }
        }

        return (44100, 2, AudioCodecType.AAC);
    }

    public void Dispose() { }
}

/// <summary>
/// RTSP 拉流源
/// 从远程 RTSP 服务器拉取流并转发
/// </summary>
public class RtspPullMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;
    private Cyaim.RTSPClient.RTSPSession? _rtspSession;
    private readonly Channel<RtpPacket> _packetChannel = Channel.CreateBounded<RtpPacket>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = true
    });

    /// <summary>
    /// SPS 数据
    /// </summary>
    public byte[]? SpsData { get; private set; }

    /// <summary>
    /// PPS 数据
    /// </summary>
    public byte[]? PpsData { get; private set; }

    public RtspPullMediaSource(StreamConfig config)
    {
        _config = config;
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
            if (_rtspSession.SDP != null && _rtspSession.SDP.MediaDescriptions.Any(m => m.MediaType == "audio"))
            {
                await _rtspSession.SetupAsync("trackID=1", "RTP/AVP/TCP;unicast;interleaved=2-3", ct: ct);
            }

            // 开始播放
            await _rtspSession.PlayAsync(ct: ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RTSP pull source error: {ex.Message}");
            _running = false;
        }
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

            // 写入通道（非阻塞）
            _packetChannel.Writer.TryWrite(packet);
        }
        catch
        {
            // 忽略写入错误
        }
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var packet in _packetChannel.Reader.ReadAllAsync(ct))
        {
            if (!_running || ct.IsCancellationRequested)
                break;

            yield return packet;
        }
    }

    public void Dispose()
    {
        _running = false;
        _rtspSession?.Dispose();
        _packetChannel.Writer.TryComplete();
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
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;

    /// <summary>
    /// SPS 数据
    /// </summary>
    public byte[]? SpsData { get; private set; }

    /// <summary>
    /// PPS 数据
    /// </summary>
    public byte[]? PpsData { get; private set; }

    public TestPatternMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        
        // 生成 SPS/PPS 并保存到属性
        SpsData = GenerateSPS();
        PpsData = GeneratePPS();
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        int frameCount = 0;

        while (_running && !ct.IsCancellationRequested)
        {
            bool isKeyFrame = frameCount % (_config.Framerate * 2) == 0; // 每2秒一个关键帧

            if (isKeyFrame)
            {
                // 关键帧: 发送 SPS + PPS + IDR
                byte[] sps = GenerateSPS();
                byte[] pps = GeneratePPS();
                byte[] idr = GenerateIDRFrame();

                // SPS - 不设置 marker
                yield return CreatePacket(sps, _sequenceNumber++, _timestamp, false, true);

                // PPS - 不设置 marker
                yield return CreatePacket(pps, _sequenceNumber++, _timestamp, false, false);

                // IDR - 设置 marker（最后一个 NAL）
                yield return CreatePacket(idr, _sequenceNumber++, _timestamp, true, true);
            }
            else
            {
                // P 帧 - 设置 marker
                byte[] pFrame = GeneratePFrame();
                yield return CreatePacket(pFrame, _sequenceNumber++, _timestamp, true, false);
            }

            _timestamp += (uint)(90000 / _config.Framerate);
            frameCount++;

            await Task.Delay(1000 / _config.Framerate, ct);
        }
    }

    private RtpPacket CreatePacket(byte[] nalData, ushort seq, uint ts, bool marker, bool isKeyFrame)
    {
        byte[] rtpPacket = new byte[12 + nalData.Length];
        // RTP header
        rtpPacket[0] = 0x80; // V=2
        rtpPacket[1] = (byte)((marker ? 0x80 : 0x00) | 96); // M + PT
        rtpPacket[2] = (byte)(seq >> 8);
        rtpPacket[3] = (byte)(seq & 0xFF);
        rtpPacket[4] = (byte)(ts >> 24);
        rtpPacket[5] = (byte)((ts >> 16) & 0xFF);
        rtpPacket[6] = (byte)((ts >> 8) & 0xFF);
        rtpPacket[7] = (byte)(ts & 0xFF);
        rtpPacket[8] = 0x12; rtpPacket[9] = 0x34; rtpPacket[10] = 0x56; rtpPacket[11] = 0x78; // SSRC
        // NAL data
        Array.Copy(nalData, 0, rtpPacket, 12, nalData.Length);

        return new RtpPacket
        {
            Data = rtpPacket,
            TrackId = 0,
            Timestamp = ts,
            SequenceNumber = seq,
            IsKeyFrame = isKeyFrame
        };
    }

    /// <summary>
    /// 生成有效的 SPS NAL 单元
    /// </summary>
    private byte[] GenerateSPS()
    {
        var writer = new BitWriter();

        // NAL header
        writer.WriteBits(0, 1); // forbidden_zero_bit
        writer.WriteBits(3, 2); // nal_ref_idc (3 = high for SPS)
        writer.WriteBits(7, 5); // nal_unit_type (7 = SPS)

        // SPS data
        writer.WriteExpGolomb(66); // profile_idc (Baseline)
        writer.WriteBit(true);     // constraint_set0_flag
        writer.WriteBit(true);     // constraint_set1_flag
        writer.WriteBit(false);    // constraint_set2_flag
        writer.WriteBit(false);    // constraint_set3_flag
        writer.WriteBits(0, 4);    // reserved
        writer.WriteExpGolomb(30); // level_idc (3.0)

        writer.WriteExpGolomb(0);  // seq_parameter_set_id

        // chroma_format_idc (for High profile, but we use Baseline so skip)
        // For Baseline profile, these are implicit

        writer.WriteExpGolomb(0);  // log2_max_frame_num_minus4 (0 = max_frame_num = 16)
        writer.WriteExpGolomb(0);  // pic_order_cnt_type (0)
        writer.WriteExpGolomb(0);  // log2_max_pic_order_cnt_lsb_minus4 (0 = max = 16)
        writer.WriteExpGolomb(0);  // max_num_ref_frames
        writer.WriteBit(false);    // gaps_in_frame_num_value_allowed_flag

        // pic_width_in_mbs_minus1 (320 / 16 - 1 = 19)
        writer.WriteExpGolomb(19);
        // pic_height_in_map_units_minus1 (240 / 16 - 1 = 14)
        writer.WriteExpGolomb(14);

        writer.WriteBit(true);     // frame_mbs_only_flag
        writer.WriteBit(false);    // direct_8x8_inference_flag

        writer.WriteBit(false);    // frame_cropping_flag
        writer.WriteBit(false);    // vui_parameters_present_flag

        writer.Flush();
        return writer.ToArray();
    }

    /// <summary>
    /// 生成有效的 PPS NAL 单元
    /// </summary>
    private byte[] GeneratePPS()
    {
        var writer = new BitWriter();

        // NAL header
        writer.WriteBits(0, 1); // forbidden_zero_bit
        writer.WriteBits(3, 2); // nal_ref_idc
        writer.WriteBits(8, 5); // nal_unit_type (8 = PPS)

        // PPS data
        writer.WriteExpGolomb(0); // pic_parameter_set_id
        writer.WriteExpGolomb(0); // seq_parameter_set_id
        writer.WriteBit(false);   // entropy_coding_mode_flag (CAVLC)
        writer.WriteBit(false);   // bottom_field_pic_order_in_frame_present_flag
        writer.WriteExpGolomb(0); // num_slice_groups_minus1
        writer.WriteExpGolomb(0); // num_ref_idx_l0_default_active_minus1
        writer.WriteExpGolomb(0); // num_ref_idx_l1_default_active_minus1
        writer.WriteBit(false);   // weighted_pred_flag
        writer.WriteBits(0, 2);   // weighted_bipred_idc
        writer.WriteSignedExpGolomb(0); // pic_init_qp_minus26
        writer.WriteSignedExpGolomb(0); // pic_init_qs_minus26
        writer.WriteSignedExpGolomb(0); // chroma_qp_index_offset
        writer.WriteBit(false);   // deblocking_filter_control_present_flag
        writer.WriteBit(false);   // constrained_intra_pred_flag
        writer.WriteBit(false);   // redundant_pic_cnt_present_flag

        writer.Flush();
        return writer.ToArray();
    }

    /// <summary>
    /// 生成最小有效 IDR 帧（全黑 320x240）
    /// </summary>
    private byte[] GenerateIDRFrame()
    {
        var writer = new BitWriter();

        // NAL header
        writer.WriteBits(0, 1); // forbidden_zero_bit
        writer.WriteBits(3, 2); // nal_ref_idc (3 = required for IDR)
        writer.WriteBits(5, 5); // nal_unit_type (5 = IDR slice)

        // Slice header
        writer.WriteExpGolomb(0); // first_mb_in_slice
        writer.WriteExpGolomb(7); // slice_type (7 = I slice)
        writer.WriteExpGolomb(0); // pic_parameter_set_id
        writer.WriteBits(0, 4);   // frame_num (4 bits for log2_max_frame_num_minus4=0)

        // idr_pic_id
        writer.WriteExpGolomb(0);

        // pic_order_cnt_lsb (4 bits for log2_max_pic_order_cnt_lsb_minus4=0)
        writer.WriteBits(0, 4);

        // no output of prior pics flag (for IDR)
        writer.WriteBit(false);

        // 一个宏块数据 (全黑)
        // mb_type = I_16x16 with DC prediction, no residual
        // In H.264 CAVLC, I_16x16 mb_type depends on the number of non-zero coefficients
        // For a completely black frame with DC prediction, we use mb_type that signals no residual

        // For I_16x16, the mb_type values are:
        // I_NxN = 0 (uses 4x4 transform)
        // I_16x16_0_0_0 = 1 (16x16 DC, no residual, QP=0)
        // etc.

        // We'll use I_NxN (mb_type=0) which means Intra_4x4
        writer.WriteExpGolomb(0); // mb_type = 0 (I_NxN)

        // Transform size flag (8x8 transform)
        writer.WriteBit(false); // transform_size_8x8_flag = false (use 4x4)

        // coded_block_pattern (CBP) = 0 means no coded blocks
        // For I_NxN, CBP is derived, but we need to signal it
        // Actually, for I_NxN, the CBP is implicitly 0 if no coefficients are coded

        // coded_block_pattern for Intra: 
        // We need to write the CBP. For all-zero (no residual), CBP = 0
        // CBP is coded as me_cbp table lookup
        // CBP = 0 means no luma or chroma blocks coded
        writer.WriteExpGolomb(0); // coded_block_pattern (0 = no blocks coded)

        // mb_qp_delta = 0
        writer.WriteSignedExpGolomb(0);

        // No residual data (all zeros)

        // end_of_slice_flag (implicit in bitstream)

        writer.Flush();
        return writer.ToArray();
    }

    /// <summary>
    /// 生成最小有效 P 帧
    /// </summary>
    private byte[] GeneratePFrame()
    {
        var writer = new BitWriter();

        // NAL header
        writer.WriteBits(0, 1); // forbidden_zero_bit
        writer.WriteBits(2, 2); // nal_ref_idc (2 = used for reference)
        writer.WriteBits(1, 5); // nal_unit_type (1 = non-IDR slice)

        // Slice header
        writer.WriteExpGolomb(0); // first_mb_in_slice
        writer.WriteExpGolomb(5); // slice_type (5 = P slice)
        writer.WriteExpGolomb(0); // pic_parameter_set_id
        writer.WriteBits(0, 4);   // frame_num

        // pic_order_cnt_lsb
        writer.WriteBits(0, 4);

        // All macroblocks are skipped (P_skip mode)
        // P_skip is signaled by mb_type = 5 in P slices
        // But we need at least one macroblock...

        // Use P_skip macroblock (no motion vector, no residual)
        // In CAVLC, P_skip is signaled by mb_type = 0 with no coded_block_pattern
        writer.WriteExpGolomb(0); // mb_type = 0 (P_L0_16x16)

        // For P_L0_16x16, we need ref_idx_l0 and mvd_l0
        // ref_idx_l0 = 0 (reference frame 0)
        writer.WriteExpGolomb(0); // ref_idx_l0

        // mvd_l0 = (0,0) - no motion
        writer.WriteSignedExpGolomb(0); // mvd_l0[0]
        writer.WriteSignedExpGolomb(0); // mvd_l0[1]

        // coded_block_pattern = 0 (no residual)
        writer.WriteExpGolomb(0);

        // mb_qp_delta = 0
        writer.WriteSignedExpGolomb(0);

        writer.Flush();
        return writer.ToArray();
    }

    public void Dispose() { }
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
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;

    public byte[]? SpsData { get; private set; }
    public byte[]? PpsData { get; private set; }

    public CameraMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        SpsData = GenerateSps();
        PpsData = GeneratePps();
        System.Diagnostics.Debug.WriteLine($"Camera source: {_config.Path} (device: {_config.Source ?? "default"})");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // TODO: 使用 DirectShow/MediaFoundation 采集摄像头
        // Fallback: 使用测试图案
        int frameCount = 0;
        while (_running && !ct.IsCancellationRequested)
        {
            bool isKeyFrame = frameCount % (_config.Framerate * 2) == 0;
            byte[] nalData;

            if (isKeyFrame)
            {
                // 发送 SPS + PPS + IDR
                yield return CreatePacket(SpsData!, _sequenceNumber++, _timestamp, false, true);
                yield return CreatePacket(PpsData!, _sequenceNumber++, _timestamp, false, false);
                nalData = GenerateIdrFrame();
            }
            else
            {
                nalData = GeneratePFrame();
            }

            yield return CreatePacket(nalData, _sequenceNumber++, _timestamp, true, isKeyFrame);
            _timestamp += (uint)(90000 / _config.Framerate);
            frameCount++;
            await Task.Delay(1000 / _config.Framerate, ct);
        }
    }

    private RtpPacket CreatePacket(byte[] nalData, ushort seq, uint ts, bool marker, bool isKeyFrame)
    {
        byte[] rtpPacket = new byte[12 + nalData.Length];
        rtpPacket[0] = 0x80;
        rtpPacket[1] = (byte)((marker ? 0x80 : 0x00) | 96);
        rtpPacket[2] = (byte)(seq >> 8);
        rtpPacket[3] = (byte)(seq & 0xFF);
        rtpPacket[4] = (byte)(ts >> 24);
        rtpPacket[5] = (byte)((ts >> 16) & 0xFF);
        rtpPacket[6] = (byte)((ts >> 8) & 0xFF);
        rtpPacket[7] = (byte)(ts & 0xFF);
        rtpPacket[8] = 0x12; rtpPacket[9] = 0x34; rtpPacket[10] = 0x56; rtpPacket[11] = 0x78;
        Array.Copy(nalData, 0, rtpPacket, 12, nalData.Length);
        return new RtpPacket { Data = rtpPacket, TrackId = 0, Timestamp = ts, SequenceNumber = seq, IsKeyFrame = isKeyFrame };
    }

    private static byte[] GenerateSps()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(7, 5);
        w.WriteExpGolomb(66); w.WriteBit(true); w.WriteBit(true);
        w.WriteBit(false); w.WriteBit(false); w.WriteBits(0, 4); w.WriteExpGolomb(30);
        w.WriteExpGolomb(0); w.WriteExpGolomb(1); w.WriteExpGolomb(1);
        w.WriteBit(true); w.WriteExpGolomb(0); w.WriteExpGolomb(0);
        w.WriteBit(false); w.WriteBit(true); return w.ToArray();
    }

    private static byte[] GeneratePps()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(8, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0); w.WriteBit(false); w.WriteBit(true);
        w.WriteBit(true); return w.ToArray();
    }

    private static byte[] GenerateIdrFrame()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(5, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0); w.WriteBit(false);
        for (int i = 0; i < 10; i++) w.WriteBit(true);
        return w.ToArray();
    }

    private static byte[] GeneratePFrame()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(2, 2); w.WriteBits(1, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0);
        for (int i = 0; i < 5; i++) w.WriteBit(true);
        return w.ToArray();
    }

    public void Dispose() { _running = false; }
}

/// <summary>
/// 屏幕捕获源
/// 捕获屏幕内容作为视频流
/// </summary>
public class ScreenCaptureMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;

    public byte[]? SpsData { get; private set; }
    public byte[]? PpsData { get; private set; }

    public ScreenCaptureMediaSource(StreamConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running = true;
        SpsData = GenerateSps();
        PpsData = GeneratePps();
        System.Diagnostics.Debug.WriteLine($"Screen capture source: {_config.Path}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RtpPacket> GetPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // TODO: 使用 Windows.Graphics.Capture 或 DXGI 屏幕捕获
        // Fallback: 使用测试图案
        int frameCount = 0;
        while (_running && !ct.IsCancellationRequested)
        {
            bool isKeyFrame = frameCount % (_config.Framerate * 2) == 0;

            if (isKeyFrame)
            {
                yield return CreatePacket(SpsData!, _sequenceNumber++, _timestamp, false, true);
                yield return CreatePacket(PpsData!, _sequenceNumber++, _timestamp, false, false);
                byte[] idr = GenerateIdrFrame();
                yield return CreatePacket(idr, _sequenceNumber++, _timestamp, true, true);
            }
            else
            {
                byte[] pFrame = GeneratePFrame();
                yield return CreatePacket(pFrame, _sequenceNumber++, _timestamp, true, false);
            }

            _timestamp += (uint)(90000 / _config.Framerate);
            frameCount++;
            await Task.Delay(1000 / _config.Framerate, ct);
        }
    }

    private RtpPacket CreatePacket(byte[] nalData, ushort seq, uint ts, bool marker, bool isKeyFrame)
    {
        byte[] rtpPacket = new byte[12 + nalData.Length];
        rtpPacket[0] = 0x80;
        rtpPacket[1] = (byte)((marker ? 0x80 : 0x00) | 96);
        rtpPacket[2] = (byte)(seq >> 8);
        rtpPacket[3] = (byte)(seq & 0xFF);
        rtpPacket[4] = (byte)(ts >> 24);
        rtpPacket[5] = (byte)((ts >> 16) & 0xFF);
        rtpPacket[6] = (byte)((ts >> 8) & 0xFF);
        rtpPacket[7] = (byte)(ts & 0xFF);
        rtpPacket[8] = 0x12; rtpPacket[9] = 0x34; rtpPacket[10] = 0x56; rtpPacket[11] = 0x78;
        Array.Copy(nalData, 0, rtpPacket, 12, nalData.Length);
        return new RtpPacket { Data = rtpPacket, TrackId = 0, Timestamp = ts, SequenceNumber = seq, IsKeyFrame = isKeyFrame };
    }

    private static byte[] GenerateSps()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(7, 5);
        w.WriteExpGolomb(66); w.WriteBit(true); w.WriteBit(true);
        w.WriteBit(false); w.WriteBit(false); w.WriteBits(0, 4); w.WriteExpGolomb(30);
        w.WriteExpGolomb(0); w.WriteExpGolomb(1); w.WriteExpGolomb(1);
        w.WriteBit(true); w.WriteExpGolomb(0); w.WriteExpGolomb(0);
        w.WriteBit(false); w.WriteBit(true); return w.ToArray();
    }

    private static byte[] GeneratePps()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(8, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0); w.WriteBit(false); w.WriteBit(true);
        w.WriteBit(true); return w.ToArray();
    }

    private static byte[] GenerateIdrFrame()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(3, 2); w.WriteBits(5, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0); w.WriteBit(false);
        for (int i = 0; i < 10; i++) w.WriteBit(true);
        return w.ToArray();
    }

    private static byte[] GeneratePFrame()
    {
        var w = new BitWriter();
        w.WriteBits(0, 1); w.WriteBits(2, 2); w.WriteBits(1, 5);
        w.WriteExpGolomb(0); w.WriteExpGolomb(0);
        for (int i = 0; i < 5; i++) w.WriteBit(true);
        return w.ToArray();
    }

    public void Dispose() { _running = false; }
}
