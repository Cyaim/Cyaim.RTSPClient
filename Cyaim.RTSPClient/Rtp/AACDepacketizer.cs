using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// AAC RTP 解包器 (RFC 3640)
    /// 支持 AAC-hbr (High Bitrate) 模式
    ///
    /// 正确性说明：
    /// - AU-header 步长：首个为 sizeLength+indexLength 位，后续为
    ///   sizeLength+indexDeltaLength 位（旧实现按统一步长读取，
    ///   indexLength != indexDeltaLength 的流会解出垃圾长度）
    /// - 支持单个大 AU 跨多个 RTP 包分片（marker 位标记最后一片）
    /// </summary>
    public class AACDepacketizer : IRTPDepacketizer
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _sizeLength;
        private readonly int _indexLength;
        private readonly int _indexDeltaLength;

        // 跨包 AU 分片重组状态
        private byte[]? _pendingAu;
        private int _pendingAuOffset;
        private uint _pendingTimestamp;
        private ushort _expectedSeq;

        /// <summary>
        /// 创建 AAC 解包器
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        /// <param name="channels">声道数</param>
        /// <param name="sizeLength">AU-size 字段长度 (bit)，默认 13</param>
        /// <param name="indexLength">AU-index 字段长度 (bit)，默认 3</param>
        /// <param name="indexDeltaLength">AU-index-delta 字段长度 (bit)，默认 3</param>
        public AACDepacketizer(
            int sampleRate = 44100,
            int channels = 2,
            int sizeLength = 13,
            int indexLength = 3,
            int indexDeltaLength = 3)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _sizeLength = sizeLength;
            _indexLength = indexLength;
            _indexDeltaLength = indexDeltaLength;
        }

        /// <summary>
        /// 从 CodecInfo 创建解包器
        /// </summary>
        public static AACDepacketizer CreateFromCodecInfo(Media.CodecInfo codecInfo)
        {
            int sizeLength = 13;
            int indexLength = 3;
            int indexDeltaLength = 3;

            // 从 fmtp 解析参数
            if (codecInfo.FmtpParameters.TryGetValue("sizeLength", out string? sl) && int.TryParse(sl, out int slVal))
                sizeLength = slVal;
            if (codecInfo.FmtpParameters.TryGetValue("indexLength", out string? il) && int.TryParse(il, out int ilVal))
                indexLength = ilVal;
            if (codecInfo.FmtpParameters.TryGetValue("indexDeltaLength", out string? idl) && int.TryParse(idl, out int idlVal))
                indexDeltaLength = idlVal;

            return new AACDepacketizer(
                codecInfo.ClockRate > 0 ? codecInfo.ClockRate : 44100,
                codecInfo.Channels > 0 ? codecInfo.Channels : 2,
                sizeLength,
                indexLength,
                indexDeltaLength);
        }

        /// <summary>
        /// 输入 RTP 包，输出 AAC 帧
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            var buf = seg.Array;
            int baseOffset = seg.Offset;
            if (buf == null || seg.Count < 3)
                yield break;

            // AU Header Section: 前 2 字节是 AU-headers-length（以 bit 为单位）
            int auHeadersLengthBits = (buf[baseOffset] << 8) | buf[baseOffset + 1];
            int auHeadersLengthBytes = (auHeadersLengthBits + 7) / 8;
            int dataOffset = 2 + auHeadersLengthBytes;
            if (dataOffset > seg.Count || auHeadersLengthBits <= 0)
                yield break;

            // 解析 AU headers：首个 sizeLength+indexLength 位，后续 sizeLength+indexDeltaLength 位。
            // 同时读取 AU-index / AU-index-delta 计算每个 AU 的序号（RFC 3640 交织模式下
            // 序号不连续，AU 时间戳 = 包时间戳 + 序号 × 1024）
            var auSizes = new List<int>();
            var auSerials = new List<long>();
            int bitOffset = baseOffset * 8 + 16; // 跳过 AU-headers-length 本身
            int bitsRemaining = auHeadersLengthBits;
            bool first = true;
            long serial = 0;

            while (bitsRemaining > 0)
            {
                int headerBits = _sizeLength + (first ? _indexLength : _indexDeltaLength);
                if (bitsRemaining < headerBits)
                    break;

                int auSize = ReadBits(buf, bitOffset, _sizeLength);

                if (first)
                {
                    serial = _indexLength > 0 ? ReadBits(buf, bitOffset + _sizeLength, _indexLength) : 0;
                }
                else
                {
                    int delta = _indexDeltaLength > 0 ? ReadBits(buf, bitOffset + _sizeLength, _indexDeltaLength) : 0;
                    serial += delta + 1;
                }

                auSizes.Add(auSize);
                auSerials.Add(serial);

                bitOffset += headerBits;
                bitsRemaining -= headerBits;
                first = false;
            }

            if (auSizes.Count == 0)
                yield break;

            int available = seg.Count - dataOffset;

            // 单 AU 且数据不足声明的大小 → 跨包分片，累积到 marker
            if (auSizes.Count == 1 && (available < auSizes[0] || _pendingAu != null))
            {
                var frame = FeedFragment(packet, buf, baseOffset + dataOffset, available, auSizes[0]);
                if (frame.HasValue)
                    yield return frame.Value;
                yield break;
            }

            // 常规：一个包内一个或多个完整 AU
            int offset = dataOffset;
            for (int i = 0; i < auSizes.Count; i++)
            {
                int auSize = auSizes[i];
                if (auSize <= 0 || offset + auSize > seg.Count)
                    break;

                byte[] aacFrame = new byte[auSize];
                Array.Copy(buf, baseOffset + offset, aacFrame, 0, auSize);
                offset += auSize;

                // 每个 AU 时长 1024 采样；交织流按 AU 序号计算时间戳（下游按时间戳重排）
                uint auTimestamp = (uint)(packet.Timestamp + auSerials[i] * 1024);
                bool isLast = i == auSizes.Count - 1;

                yield return new MediaFrame(
                    aacFrame,
                    auTimestamp,
                    false,
                    StreamType.Audio,
                    packet.TrackId,
                    isLast && packet.Marker);
            }
        }

        /// <summary>
        /// 跨包 AU 分片重组（AAC-hbr 允许单个大 AU 分多个 RTP 包发送，marker 标记最后一片）
        /// </summary>
        private MediaFrame? FeedFragment(RTPPacket packet, byte[] payload, int absoluteDataOffset, int available, int auSize)
        {
            if (_pendingAu == null)
            {
                // 第一片
                if (auSize <= 0 || auSize > 64 * 1024 * 1024)
                    return null;
                _pendingAu = new byte[auSize];
                _pendingAuOffset = 0;
                _pendingTimestamp = packet.Timestamp;
            }
            else
            {
                // 序列号/时间戳必须连续，否则丢弃整个 AU
                if (packet.SequenceNumber != _expectedSeq || packet.Timestamp != _pendingTimestamp)
                {
                    _pendingAu = null;
                    return null;
                }
            }

            _expectedSeq = (ushort)(packet.SequenceNumber + 1);

            int copyLen = Math.Min(available, _pendingAu.Length - _pendingAuOffset);
            if (copyLen > 0)
            {
                Array.Copy(payload, absoluteDataOffset, _pendingAu, _pendingAuOffset, copyLen);
                _pendingAuOffset += copyLen;
            }

            if (packet.Marker || _pendingAuOffset >= _pendingAu.Length)
            {
                var complete = _pendingAu;
                int length = _pendingAuOffset;
                _pendingAu = null;

                if (length != complete.Length)
                {
                    var trimmed = new byte[length];
                    Array.Copy(complete, trimmed, length);
                    complete = trimmed;
                }

                return new MediaFrame(complete, _pendingTimestamp, false, StreamType.Audio, packet.TrackId, true);
            }

            return null;
        }

        /// <summary>
        /// 从字节数组中读取指定位数
        /// </summary>
        private static int ReadBits(byte[] data, int bitOffset, int bitCount)
        {
            int result = 0;
            int byteIndex = bitOffset / 8;
            int bitIndex = bitOffset % 8;

            for (int i = 0; i < bitCount; i++)
            {
                if (byteIndex >= data.Length)
                    break;

                result <<= 1;
                if ((data[byteIndex] & (0x80 >> bitIndex)) != 0)
                {
                    result |= 1;
                }

                bitIndex++;
                if (bitIndex >= 8)
                {
                    bitIndex = 0;
                    byteIndex++;
                }
            }

            return result;
        }

        /// <summary>
        /// 重置解包器状态
        /// </summary>
        public void Reset()
        {
            _pendingAu = null;
            _pendingAuOffset = 0;
        }
    }
}
