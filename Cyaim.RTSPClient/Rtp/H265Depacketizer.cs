using System;
using System.Buffers;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// H.265 RTP解包器 (RFC 7798)
    /// 支持Single NAL, AP (Aggregation Packet), FU (Fragmentation Unit)
    ///
    /// 可靠性说明（与 H264Depacketizer 相同的修复）：
    /// - FU 重组缓冲从 ArrayPool 租用并按需增长（4K/8K IDR 单 NAL 常超旧实现的 64KB 上限）
    /// - 依据 RTP 序列号连续性检测分片丢失，缺口时丢弃整个 NAL
    /// - 支持注入 SDP sprop-vps/sps/pps
    /// </summary>
    public class H265Depacketizer : IRTPDepacketizer, IDisposable
    {
        private byte[]? _fragmentBuffer;
        private int _fragmentOffset;
        private uint _currentTimestamp;
        private bool _fragmentStarted;
        private ushort _expectedSeq;

        private readonly byte[]? _vpsFromSdp;
        private readonly byte[]? _spsFromSdp;
        private readonly byte[]? _ppsFromSdp;
        private bool _inbandParamsSeen;
        private bool _sdpParamsInjected;

        // RFC 7798：sprop-max-don-diff > 0 时 AP/FU 携带 DONL/DOND 字段
        private readonly bool _donPresent;

        private const int InitialFragmentBufferSize = 256 * 1024;

        // H.265 NAL unit types
        private const byte NAL_TYPE_VPS = 32;
        private const byte NAL_TYPE_SPS = 33;
        private const byte NAL_TYPE_PPS = 34;
        private const byte NAL_TYPE_IDR_W_RADL = 19;
        private const byte NAL_TYPE_IDR_N_LP = 20;
        private const byte NAL_TYPE_CRA = 21;

        public H265Depacketizer()
        {
        }

        /// <summary>
        /// 使用 SDP 中的 VPS/SPS/PPS 构造（sprop-vps/sprop-sps/sprop-pps）
        /// </summary>
        /// <param name="vps">SDP sprop-vps 解码后的字节</param>
        /// <param name="sps">SDP sprop-sps 解码后的字节</param>
        /// <param name="pps">SDP sprop-pps 解码后的字节</param>
        /// <param name="donPresent">sprop-max-don-diff &gt; 0 时为 true，AP/FU 中携带 DONL/DOND 字段</param>
        public H265Depacketizer(byte[]? vps, byte[]? sps, byte[]? pps, bool donPresent = false)
        {
            _vpsFromSdp = vps;
            _spsFromSdp = sps;
            _ppsFromSdp = pps;
            _donPresent = donPresent;
        }

        /// <summary>
        /// 从 SDP 编码信息构造
        /// </summary>
        public static H265Depacketizer CreateFromCodecInfo(Media.CodecInfo? codecInfo)
        {
            bool donPresent = false;
            if (codecInfo != null &&
                codecInfo.FmtpParameters.TryGetValue("sprop-max-don-diff", out var donDiff) &&
                int.TryParse(donDiff, out int diff))
            {
                donPresent = diff > 0;
            }

            return new H265Depacketizer(codecInfo?.H265Vps, codecInfo?.H265Sps, codecInfo?.H265Pps, donPresent);
        }

        /// <summary>
        /// 输入RTP包，输出完整的H.265 NAL单元
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            if (seg.Array == null || seg.Count < 2)
                yield break;

            // H.265 RTP头: 2 bytes (F|Type|LayerId|TID)
            byte firstByte = seg.Array[seg.Offset];
            byte nalType = (byte)((firstByte >> 1) & 0x3F);

            switch (nalType)
            {
                case byte n when n <= 47:
                    // Single NAL unit (包括VPS=32, SPS=33, PPS=34)
                    if (n is NAL_TYPE_VPS or NAL_TYPE_SPS or NAL_TYPE_PPS)
                        _inbandParamsSeen = true;

                    foreach (var injected in MaybeInjectSdpParams(n, packet))
                        yield return injected;

                    yield return CreateFrame(packet.Payload, packet.Timestamp, nalType, packet.TrackId, packet.Marker);
                    break;

                case 48: // AP (Aggregation Packet)
                    foreach (var frame in ParseAggregationPacket(packet))
                        yield return frame;
                    break;

                case 49: // FU (Fragmentation Unit)
                    var fuFrame = ParseFu(packet);
                    if (fuFrame.HasValue)
                    {
                        byte completeType = (byte)((fuFrame.Value.Data[0] >> 1) & 0x3F);
                        foreach (var injected in MaybeInjectSdpParams(completeType, packet))
                            yield return injected;
                        yield return fuFrame.Value;
                    }
                    break;

                case 50: // PACI (Packing Information)
                    // 不常见，暂不支持
                    break;
            }
        }

        private IEnumerable<MediaFrame> MaybeInjectSdpParams(byte nalType, RTPPacket packet)
        {
            bool isIdr = nalType is NAL_TYPE_IDR_W_RADL or NAL_TYPE_IDR_N_LP or NAL_TYPE_CRA;
            if (!isIdr || _inbandParamsSeen || _sdpParamsInjected)
                yield break;

            _sdpParamsInjected = true;

            if (_vpsFromSdp is { Length: > 0 })
                yield return CreateFrame(_vpsFromSdp, packet.Timestamp, NAL_TYPE_VPS, packet.TrackId, false);
            if (_spsFromSdp is { Length: > 0 })
                yield return CreateFrame(_spsFromSdp, packet.Timestamp, NAL_TYPE_SPS, packet.TrackId, false);
            if (_ppsFromSdp is { Length: > 0 })
                yield return CreateFrame(_ppsFromSdp, packet.Timestamp, NAL_TYPE_PPS, packet.TrackId, false);
        }

        private IEnumerable<MediaFrame> ParseAggregationPacket(RTPPacket packet)
        {
            // AP格式: 2-byte header + NAL units with 2-byte size prefix
            // sprop-max-don-diff>0 时首个 NAL 前有 16 位 DONL、后续各 NAL 前有 8 位 DOND（此处跳过，
            // 解码顺序按到达顺序处理）
            var seg = packet.PayloadSegment;
            var buf = seg.Array!;
            int baseOffset = seg.Offset;
            int offset = 2;
            bool firstNal = true;

            while (offset + 2 <= seg.Count)
            {
                if (_donPresent)
                {
                    offset += firstNal ? 2 : 1; // DONL(16bit) / DOND(8bit)
                    if (offset + 2 > seg.Count)
                        break;
                }
                firstNal = false;

                int naluSize = (buf[baseOffset + offset] << 8) | buf[baseOffset + offset + 1];
                offset += 2;

                if (naluSize <= 0 || offset + naluSize > seg.Count)
                    break;

                byte[] nalu = new byte[naluSize];
                Array.Copy(buf, baseOffset + offset, nalu, 0, naluSize);
                offset += naluSize;

                byte nalType = (byte)((nalu[0] >> 1) & 0x3F);
                if (nalType is NAL_TYPE_VPS or NAL_TYPE_SPS or NAL_TYPE_PPS)
                    _inbandParamsSeen = true;

                bool isLast = offset + 2 > seg.Count;
                yield return CreateFrame(nalu, packet.Timestamp, nalType, packet.TrackId, packet.Marker && isLast);
            }
        }

        private MediaFrame? ParseFu(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            var buf = seg.Array!;
            int baseOffset = seg.Offset;
            if (seg.Count < 3)
                return null;

            // FU header: S|E|Type (1 byte)
            byte fuHeader = buf[baseOffset + 2];

            bool isStart = (fuHeader & 0x80) != 0;
            bool isEnd = (fuHeader & 0x40) != 0;
            byte nalType = (byte)(fuHeader & 0x3F);

            // FU 携带 DONL 时（仅起始分片），数据区在 FU header 后偏移 2 字节
            int fuDataOffset = 3;
            if (_donPresent && isStart)
                fuDataOffset = 5;

            if (isStart)
            {
                if (seg.Count < fuDataOffset)
                    return null;

                EnsureFragmentBuffer(InitialFragmentBufferSize);
                _fragmentOffset = 0;
                _currentTimestamp = packet.Timestamp;
                _fragmentStarted = true;
                _expectedSeq = (ushort)(packet.SequenceNumber + 1);

                // 重建NAL头: 2 bytes (Type from FU + original LayerId/TID)
                _fragmentBuffer![0] = (byte)((buf[baseOffset] & 0x81) | (nalType << 1));
                _fragmentBuffer[1] = buf[baseOffset + 1];
                _fragmentOffset = 2;
            }
            else
            {
                if (!_fragmentStarted || _fragmentBuffer is null)
                    return null;

                // 序列号缺口 → 丢弃整个分片，避免上抛损坏 NAL
                if (packet.SequenceNumber != _expectedSeq)
                {
                    _fragmentStarted = false;
                    return null;
                }
                _expectedSeq = (ushort)(packet.SequenceNumber + 1);

                if (packet.Timestamp != _currentTimestamp)
                {
                    _fragmentStarted = false;
                    return null;
                }
            }

            // 复制分片数据 (跳过2-byte RTP头 + 1-byte FU header + 可选 DONL)——直接从零拷贝切片读取
            int payloadLen = seg.Count - fuDataOffset;
            EnsureFragmentCapacity(_fragmentOffset + payloadLen);

            Array.Copy(buf, baseOffset + fuDataOffset, _fragmentBuffer!, _fragmentOffset, payloadLen);
            _fragmentOffset += payloadLen;

            if (isEnd)
            {
                _fragmentStarted = false;
                byte[] completeNal = new byte[_fragmentOffset];
                Array.Copy(_fragmentBuffer!, completeNal, _fragmentOffset);

                byte completeNalType = (byte)((completeNal[0] >> 1) & 0x3F);
                if (completeNalType is NAL_TYPE_VPS or NAL_TYPE_SPS or NAL_TYPE_PPS)
                    _inbandParamsSeen = true;

                return CreateFrame(completeNal, _currentTimestamp, completeNalType, packet.TrackId, packet.Marker);
            }

            return null;
        }

        private void EnsureFragmentBuffer(int minCapacity)
        {
            if (_fragmentBuffer == null || _fragmentBuffer.Length < minCapacity)
            {
                if (_fragmentBuffer != null)
                    ArrayPool<byte>.Shared.Return(_fragmentBuffer);
                _fragmentBuffer = ArrayPool<byte>.Shared.Rent(minCapacity);
            }
        }

        private void EnsureFragmentCapacity(int required)
        {
            if (_fragmentBuffer != null && _fragmentBuffer.Length >= required)
                return;

            int newSize = Math.Max(required, (_fragmentBuffer?.Length ?? InitialFragmentBufferSize) * 2);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            if (_fragmentBuffer != null)
            {
                Array.Copy(_fragmentBuffer, newBuffer, _fragmentOffset);
                ArrayPool<byte>.Shared.Return(_fragmentBuffer);
            }
            _fragmentBuffer = newBuffer;
        }

        private static MediaFrame CreateFrame(byte[] nal, uint timestamp, byte nalType, int trackId, bool isAccessUnitEnd)
        {
            bool isKeyFrame = nalType == NAL_TYPE_IDR_W_RADL ||
                              nalType == NAL_TYPE_IDR_N_LP ||
                              nalType == NAL_TYPE_CRA ||
                              nalType == NAL_TYPE_VPS ||
                              nalType == NAL_TYPE_SPS ||
                              nalType == NAL_TYPE_PPS;

            return new MediaFrame(nal, timestamp, isKeyFrame, StreamType.Video, trackId, isAccessUnitEnd);
        }

        /// <summary>
        /// 重置解包器状态
        /// </summary>
        public void Reset()
        {
            ReturnBuffer();
            _fragmentOffset = 0;
            _fragmentStarted = false;
            _inbandParamsSeen = false;
            _sdpParamsInjected = false;
        }

        private void ReturnBuffer()
        {
            if (_fragmentBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_fragmentBuffer);
                _fragmentBuffer = null;
            }
        }

        public void Dispose()
        {
            ReturnBuffer();
        }
    }
}
