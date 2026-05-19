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
/// 支持 H.264 Annex-B 文件和 MP4 容器
/// </summary>
public class FileMediaSource : IMediaSource
{
    private readonly StreamConfig _config;
    private bool _running;
    private uint _timestamp;
    private ushort _sequenceNumber;

    // H.264 NAL unit types
    private const byte NAL_SPS = 7;
    private const byte NAL_PPS = 8;
    private const byte NAL_IDR = 5;
    private const byte NAL_SLICE = 1;

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
        if (string.IsNullOrEmpty(_config.Source) || !File.Exists(_config.Source))
        {
            yield break;
        }

        string ext = Path.GetExtension(_config.Source).ToLowerInvariant();
        List<byte[]> nalUnits;

        if (ext == ".mp4" || ext == ".m4v" || ext == ".mov")
        {
            // MP4 容器格式
            nalUnits = await ParseMp4FileAsync(_config.Source, ct);
        }
        else
        {
            // H.264 Annex-B 格式 (.h264, .264, .avc 等)
            byte[] fileData = await File.ReadAllBytesAsync(_config.Source, ct);
            nalUnits = ParseAnnexBNalUnits(fileData);
        }

        if (nalUnits.Count == 0)
        {
            yield break;
        }

        int frameCount = 0;
        int nalIndex = 0;
        bool loop = _config.Loop;

        while (_running && !ct.IsCancellationRequested)
        {
            byte[] nalData = nalUnits[nalIndex];
            byte nalType = (byte)(nalData[0] & 0x1F);

            bool isKeyFrame = nalType == NAL_IDR;
            bool isSpsPps = nalType == NAL_SPS || nalType == NAL_PPS;

            // 设置 marker bit: 只在帧的最后一个 NAL 设置
            bool isLastNalInFrame = false;
            if (nalIndex + 1 >= nalUnits.Count)
            {
                isLastNalInFrame = true; // 文件末尾
            }
            else
            {
                byte nextNalType = (byte)(nalUnits[nalIndex + 1][0] & 0x1F);
                // 如果下一个 NAL 是 SPS/PPS 或 IDR，说明当前是帧的最后一个
                if (nextNalType == NAL_SPS || nextNalType == NAL_PPS || nextNalType == NAL_IDR)
                {
                    isLastNalInFrame = true;
                }
                else if (nalType == NAL_IDR || nalType == NAL_SLICE)
                {
                    // 对于 slice NAL，检查时间戳是否应该更新
                    isLastNalInFrame = true;
                }
            }

            byte[] rtpPacket = CreateRtpPacket(nalData, _sequenceNumber, _timestamp, isLastNalInFrame);
            yield return new RtpPacket
            {
                Data = rtpPacket,
                TrackId = 0,
                Timestamp = _timestamp,
                SequenceNumber = _sequenceNumber++,
                IsKeyFrame = isKeyFrame
            };

            // 更新时间戳
            if (isKeyFrame || nalType == NAL_SLICE)
            {
                _timestamp += (uint)(90000 / _config.Framerate);
                frameCount++;
            }

            nalIndex++;
            if (nalIndex >= nalUnits.Count)
            {
                if (loop)
                    nalIndex = 0;
                else
                    break;
            }

            if (isKeyFrame || nalType == NAL_SLICE)
            {
                await Task.Delay(1000 / _config.Framerate, ct);
            }
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
            using var stream = new MemoryStream(fileData);

            // 解析 MP4 atoms
            var atoms = ParseAtoms(stream, 0, fileData.Length);

            // 查找 moov atom
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");
            if (moov == null) return nalUnits;

            // 解析 moov 内部的 atoms
            stream.Position = moov.DataOffset;
            var moovAtoms = ParseAtoms(stream, moov.DataOffset, moov.DataSize);

            // 查找 trak atom (视频轨道)
            foreach (var trak in moovAtoms.Where(a => a.Type == "trak"))
            {
                stream.Position = trak.DataOffset;
                var trakAtoms = ParseAtoms(stream, trak.DataOffset, trak.DataSize);

                // 检查是否为视频轨道
                var mdia = trakAtoms.FirstOrDefault(a => a.Type == "mdia");
                if (mdia == null) continue;

                stream.Position = mdia.DataOffset;
                var mdiaAtoms = ParseAtoms(stream, mdia.DataOffset, mdia.DataSize);

                // 检查 hdlr 类型
                var hdlr = mdiaAtoms.FirstOrDefault(a => a.Type == "hdlr");
                if (hdlr != null)
                {
                    stream.Position = hdlr.DataOffset + 8; // skip version/flags
                    byte[] hdlrData = new byte[4];
                    stream.Read(hdlrData, 0, 4);
                    // "vide" = video track
                    if (hdlrData[0] != 'v' || hdlrData[1] != 'i' || hdlrData[2] != 'd' || hdlrData[3] != 'e')
                        continue;
                }

                // 解析 minf -> stbl
                var minf = mdiaAtoms.FirstOrDefault(a => a.Type == "minf");
                if (minf == null) continue;

                stream.Position = minf.DataOffset;
                var minfAtoms = ParseAtoms(stream, minf.DataOffset, minf.DataSize);

                var stbl = minfAtoms.FirstOrDefault(a => a.Type == "stbl");
                if (stbl == null) continue;

                stream.Position = stbl.DataOffset;
                var stblAtoms = ParseAtoms(stream, stbl.DataOffset, stbl.DataSize);

                // 解析 stsd 获取 SPS/PPS
                byte[]? sps = null;
                byte[]? pps = null;
                int nalLengthSize = 4; // 默认 4 字节长度前缀

                var stsd = stblAtoms.FirstOrDefault(a => a.Type == "stsd");
                if (stsd != null)
                {
                    stream.Position = stsd.DataOffset;
                    (sps, pps, nalLengthSize) = ParseStsdForAvc(stream, stsd.DataOffset, stsd.DataSize);
                }

                // 解析 stts (time-to-sample)
                var stts = stblAtoms.FirstOrDefault(a => a.Type == "stts");
                List<(uint count, uint delta)>? timeSamples = null;
                if (stts != null)
                {
                    stream.Position = stts.DataOffset;
                    timeSamples = ParseStts(stream, stts.DataOffset, stts.DataSize);
                }

                // 解析 stsc (sample-to-chunk)
                var stsc = stblAtoms.FirstOrDefault(a => a.Type == "stsc");
                List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)>? sampleToChunk = null;
                if (stsc != null)
                {
                    stream.Position = stsc.DataOffset;
                    sampleToChunk = ParseStsc(stream, stsc.DataOffset, stsc.DataSize);
                }

                // 解析 stsz (sample sizes)
                var stsz = stblAtoms.FirstOrDefault(a => a.Type == "stsz");
                uint[]? sampleSizes = null;
                if (stsz != null)
                {
                    stream.Position = stsz.DataOffset;
                    sampleSizes = ParseStsz(stream, stsz.DataOffset, stsz.DataSize);
                }

                // 解析 stco/co64 (chunk offsets)
                var stco = stblAtoms.FirstOrDefault(a => a.Type == "stco" || a.Type == "co64");
                long[]? chunkOffsets = null;
                if (stco != null)
                {
                    stream.Position = stco.DataOffset;
                    chunkOffsets = ParseChunkOffsets(stream, stco.DataOffset, stco.DataSize, stco.Type);
                }

                // 提取 mdat 数据
                var mdat = atoms.FirstOrDefault(a => a.Type == "mdat");
                if (mdat == null) continue;

                // 如果有 SPS/PPS，添加到 NAL 单元列表
                if (sps != null) nalUnits.Add(sps);
                if (pps != null) nalUnits.Add(pps);

                // 提取样本数据
                if (sampleSizes != null && chunkOffsets != null && sampleToChunk != null)
                {
                    var samples = ExtractSamples(fileData, sampleSizes, chunkOffsets, sampleToChunk, nalLengthSize, mdat.DataOffset);
                    nalUnits.AddRange(samples);
                }

                break; // 只处理第一个视频轨道
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MP4 parsing error: {ex.Message}");
        }

        return nalUnits;
    }

    private List<Mp4Atom> ParseAtoms(Stream stream, long offset, long size)
    {
        var atoms = new List<Mp4Atom>();
        long end = offset + size;

        while (stream.Position < end - 8)
        {
            long atomStart = stream.Position;
            byte[] sizeBytes = new byte[4];
            stream.Read(sizeBytes, 0, 4);
            uint atomSize = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);

            byte[] typeBytes = new byte[4];
            stream.Read(typeBytes, 0, 4);
            string atomType = System.Text.Encoding.ASCII.GetString(typeBytes);

            if (atomSize < 8 || atomStart + atomSize > end)
                break;

            atoms.Add(new Mp4Atom
            {
                Type = atomType,
                Offset = atomStart,
                Size = atomSize,
                DataOffset = atomStart + 8,
                DataSize = atomSize - 8
            });

            stream.Position = atomStart + atomSize;
        }

        return atoms;
    }

    private (byte[]? sps, byte[]? pps, int nalLengthSize) ParseStsdForAvc(Stream stream, long offset, long size)
    {
        stream.Position = offset;

        // stsd header: version(1) + flags(3) + entry_count(4)
        byte[] header = new byte[8];
        stream.Read(header, 0, 8);

        // 读取第一个 sample entry
        byte[] entrySizeBytes = new byte[4];
        stream.Read(entrySizeBytes, 0, 4);
        uint entrySize = (uint)((entrySizeBytes[0] << 24) | (entrySizeBytes[1] << 16) | (entrySizeBytes[2] << 8) | entrySizeBytes[3]);

        byte[] formatBytes = new byte[4];
        stream.Read(formatBytes, 0, 4);
        string format = System.Text.Encoding.ASCII.GetString(formatBytes);

        // 检查是否为 avc1
        if (!format.StartsWith("avc"))
            return (null, null, 4);

        // 跳到 avcC box
        stream.Position = offset + 8 + entrySize - 8; // 跳过保留字节

        // 查找 avcC box
        long entryEnd = offset + 8 + entrySize;
        while (stream.Position < entryEnd - 8)
        {
            long boxStart = stream.Position;
            byte[] boxSizeBytes = new byte[4];
            stream.Read(boxSizeBytes, 0, 4);
            uint boxSize = (uint)((boxSizeBytes[0] << 24) | (boxSizeBytes[1] << 16) | (boxSizeBytes[2] << 8) | boxSizeBytes[3]);

            byte[] boxTypeBytes = new byte[4];
            stream.Read(boxTypeBytes, 0, 4);
            string boxType = System.Text.Encoding.ASCII.GetString(boxTypeBytes);

            if (boxType == "avcC")
            {
                // 解析 AVCDecoderConfigurationRecord
                byte[] avccData = new byte[boxSize - 8];
                stream.Read(avccData, 0, avccData.Length);

                return ParseAvcDecoderConfig(avccData);
            }

            stream.Position = boxStart + boxSize;
        }

        return (null, null, 4);
    }

    private (byte[] sps, byte[] pps, int nalLengthSize) ParseAvcDecoderConfig(byte[] data)
    {
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

        // numOfSequenceParameterSets
        byte numSps = (byte)(data[offset++] & 0x1F);

        byte[]? sps = null;
        for (int i = 0; i < numSps; i++)
        {
            ushort spsLength = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            sps = new byte[spsLength];
            Array.Copy(data, offset, sps, 0, spsLength);
            offset += spsLength;
        }

        // numOfPictureParameterSets
        byte numPps = data[offset++];

        byte[]? pps = null;
        for (int i = 0; i < numPps; i++)
        {
            ushort ppsLength = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            pps = new byte[ppsLength];
            Array.Copy(data, offset, pps, 0, ppsLength);
            offset += ppsLength;
        }

        return (sps, pps, nalLengthSize);
    }

    private List<(uint count, uint delta)> ParseStts(Stream stream, long offset, long size)
    {
        var entries = new List<(uint, uint)>();
        stream.Position = offset;

        byte[] header = new byte[8];
        stream.Read(header, 0, 8);
        uint entryCount = (uint)((header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7]);

        for (int i = 0; i < entryCount; i++)
        {
            byte[] entry = new byte[8];
            stream.Read(entry, 0, 8);
            uint count = (uint)((entry[0] << 24) | (entry[1] << 16) | (entry[2] << 8) | entry[3]);
            uint delta = (uint)((entry[4] << 24) | (entry[5] << 16) | (entry[6] << 8) | entry[7]);
            entries.Add((count, delta));
        }

        return entries;
    }

    private List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)> ParseStsc(Stream stream, long offset, long size)
    {
        var entries = new List<(uint, uint, uint)>();
        stream.Position = offset;

        byte[] header = new byte[8];
        stream.Read(header, 0, 8);
        uint entryCount = (uint)((header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7]);

        for (int i = 0; i < entryCount; i++)
        {
            byte[] entry = new byte[12];
            stream.Read(entry, 0, 12);
            uint firstChunk = (uint)((entry[0] << 24) | (entry[1] << 16) | (entry[2] << 8) | entry[3]);
            uint samplesPerChunk = (uint)((entry[4] << 24) | (entry[5] << 16) | (entry[6] << 8) | entry[7]);
            uint sampleDescIndex = (uint)((entry[8] << 24) | (entry[9] << 16) | (entry[10] << 8) | entry[11]);
            entries.Add((firstChunk, samplesPerChunk, sampleDescIndex));
        }

        return entries;
    }

    private uint[] ParseStsz(Stream stream, long offset, long size)
    {
        stream.Position = offset;

        byte[] header = new byte[12];
        stream.Read(header, 0, 12);
        uint sampleSize = (uint)((header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7]);
        uint sampleCount = (uint)((header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11]);

        if (sampleSize != 0)
        {
            // 所有样本大小相同
            return Enumerable.Repeat(sampleSize, (int)sampleCount).ToArray();
        }

        var sizes = new uint[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            byte[] sizeBytes = new byte[4];
            stream.Read(sizeBytes, 0, 4);
            sizes[i] = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);
        }

        return sizes;
    }

    private long[] ParseChunkOffsets(Stream stream, long offset, long size, string type)
    {
        stream.Position = offset;

        byte[] header = new byte[8];
        stream.Read(header, 0, 8);
        uint entryCount = (uint)((header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7]);

        var offsets = new long[entryCount];
        int bytesPerEntry = type == "co64" ? 8 : 4;

        for (int i = 0; i < entryCount; i++)
        {
            byte[] entry = new byte[bytesPerEntry];
            stream.Read(entry, 0, bytesPerEntry);

            if (bytesPerEntry == 8)
            {
                offsets[i] = ((long)entry[0] << 56) | ((long)entry[1] << 48) | ((long)entry[2] << 40) | ((long)entry[3] << 32) |
                             ((long)entry[4] << 24) | ((long)entry[5] << 16) | ((long)entry[6] << 8) | entry[7];
            }
            else
            {
                offsets[i] = (entry[0] << 24) | (entry[1] << 16) | (entry[2] << 8) | entry[3];
            }
        }

        return offsets;
    }

    private List<byte[]> ExtractSamples(byte[] fileData, uint[] sampleSizes, long[] chunkOffsets,
        List<(uint firstChunk, uint samplesPerChunk, uint sampleDescIndex)> sampleToChunk,
        int nalLengthSize, long mdatOffset)
    {
        var samples = new List<byte[]>();
        int sampleIndex = 0;
        int stscIndex = 0;

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

                // 读取样本数据
                byte[] sampleData = new byte[sampleSize];
                Array.Copy(fileData, chunkOffset, sampleData, 0, sampleSize);

                // 转换 AVCC 格式到 Annex-B 格式
                var nalUnits = ConvertAvccToAnnexB(sampleData, nalLengthSize);
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
    /// Annex-B: [00 00 00 01][NAL][00 00 00 01][NAL]...
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

            if (offset + nalLength > sampleData.Length)
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

    private byte[] CreateRtpPacket(byte[] nalData, ushort sequenceNumber, uint timestamp, bool isMarker)
    {
        int rtpHeaderSize = 12;
        int maxPacketSize = 1400;

        if (nalData.Length <= maxPacketSize)
        {
            var packet = new byte[rtpHeaderSize + nalData.Length];
            WriteRtpHeader(packet, sequenceNumber, timestamp, isMarker);
            Array.Copy(nalData, 0, packet, rtpHeaderSize, nalData.Length);
            return packet;
        }
        else
        {
            // TODO: 实现完整的 FU-A 分片
            var packet = new byte[rtpHeaderSize + maxPacketSize];
            WriteRtpHeader(packet, sequenceNumber, timestamp, isMarker);
            Array.Copy(nalData, 0, packet, rtpHeaderSize, maxPacketSize);
            return packet;
        }
    }

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

    #endregion

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
