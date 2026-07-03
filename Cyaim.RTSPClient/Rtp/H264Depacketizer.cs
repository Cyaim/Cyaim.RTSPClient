using System;
using System.Buffers;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// H.264 RTP解包器 (RFC 6184)
    /// 支持Single NAL, STAP-A, FU-A模式
    ///
    /// 可靠性说明：
    /// - FU-A 重组缓冲从 ArrayPool 租用并按需增长（旧实现固定 64KB，
    ///   高分辨率关键帧超限被静默丢弃，表现为长时间绿屏/花屏）
    /// - 依据 RTP 序列号连续性检测分片丢失，缺口时丢弃整个 NAL
    ///   而不是拼出损坏数据上抛（旧实现只比较时间戳）
    /// - 支持注入 SDP sprop-parameter-sets 中的 SPS/PPS：部分相机不在码流内
    ///   发送参数集，不注入则无法解码
    /// </summary>
    public class H264Depacketizer : IRTPDepacketizer, IDisposable
    {
        private byte[]? _fragmentBuffer;
        private int _fragmentOffset;
        private uint _currentTimestamp;
        private bool _fragmentStarted;
        private ushort _expectedSeq;

        // SDP 提供的参数集（码流内没有时在首个 IDR 前注入）
        private readonly byte[]? _spsFromSdp;
        private readonly byte[]? _ppsFromSdp;
        private bool _inbandParamsSeen;
        private bool _sdpParamsInjected;

        private const int InitialFragmentBufferSize = 128 * 1024;

        public H264Depacketizer()
        {
        }

        /// <summary>
        /// 使用 SDP 中的 SPS/PPS 构造（sprop-parameter-sets）。
        /// 码流内未携带参数集时会在首个关键帧前注入，保证可解码。
        /// </summary>
        public H264Depacketizer(byte[]? sps, byte[]? pps)
        {
            _spsFromSdp = sps;
            _ppsFromSdp = pps;
        }

        /// <summary>
        /// 从 SDP 编码信息构造
        /// </summary>
        public static H264Depacketizer CreateFromCodecInfo(Media.CodecInfo? codecInfo)
        {
            return new H264Depacketizer(codecInfo?.H264Sps, codecInfo?.H264Pps);
        }

        /// <summary>
        /// 输入RTP包，输出完整的H.264 NAL单元
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            if (seg.Array == null || seg.Count == 0)
                yield break;

            byte nalHeader = seg.Array[seg.Offset];
            byte nalType = (byte)(nalHeader & 0x1F);

            switch (nalType)
            {
                case byte n when n >= 1 && n <= 23:
                    // Single NAL unit（帧数据外泄给消费者，此处必须实体化一份拷贝）
                    if (n == 7 || n == 8)
                        _inbandParamsSeen = true;

                    foreach (var injected in MaybeInjectSdpParams(n, packet))
                        yield return injected;

                    yield return CreateFrame(packet.Payload, packet.Timestamp, nalType, packet.TrackId, packet.Marker);
                    break;

                case 24: // STAP-A (Single-Time Aggregation Packet)
                    foreach (var frame in ParseStapA(packet))
                        yield return frame;
                    break;

                case 25: // STAP-B
                case 26: // MTAP-16
                case 27: // MTAP-24
                    // 不常见，暂不支持
                    break;

                case 28: // FU-A (Fragmentation Unit)
                    var fuFrame = ParseFuA(packet);
                    if (fuFrame.HasValue)
                    {
                        foreach (var injected in MaybeInjectSdpParams((byte)(fuFrame.Value.Data[0] & 0x1F), packet))
                            yield return injected;
                        yield return fuFrame.Value;
                    }
                    break;

                case 29: // FU-B
                    // 不常见，暂不支持
                    break;
            }
        }

        /// <summary>
        /// 首个 IDR 前注入 SDP 提供的 SPS/PPS（码流内自带参数集时不注入）
        /// </summary>
        private IEnumerable<MediaFrame> MaybeInjectSdpParams(byte nalType, RTPPacket packet)
        {
            if (nalType != 5 || _inbandParamsSeen || _sdpParamsInjected)
                yield break;

            _sdpParamsInjected = true;

            if (_spsFromSdp is { Length: > 0 })
                yield return CreateFrame(_spsFromSdp, packet.Timestamp, 7, packet.TrackId, false);
            if (_ppsFromSdp is { Length: > 0 })
                yield return CreateFrame(_ppsFromSdp, packet.Timestamp, 8, packet.TrackId, false);
        }

        private IEnumerable<MediaFrame> ParseStapA(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            var buf = seg.Array!;
            int baseOffset = seg.Offset;
            int offset = 1; // Skip STAP-A header

            while (offset + 2 <= seg.Count)
            {
                // 2-byte length prefix
                int naluLength = (buf[baseOffset + offset] << 8) | buf[baseOffset + offset + 1];
                offset += 2;

                if (naluLength <= 0 || offset + naluLength > seg.Count)
                    break;

                byte[] nalu = new byte[naluLength];
                Array.Copy(buf, baseOffset + offset, nalu, 0, naluLength);
                offset += naluLength;

                byte naluType = (byte)(nalu[0] & 0x1F);
                if (naluType == 7 || naluType == 8)
                    _inbandParamsSeen = true;

                // marker 只标记聚合包中的最后一个 NAL
                bool isLast = offset + 2 > seg.Count;
                yield return CreateFrame(nalu, packet.Timestamp, naluType, packet.TrackId, packet.Marker && isLast);
            }
        }

        private MediaFrame? ParseFuA(RTPPacket packet)
        {
            var seg = packet.PayloadSegment;
            var buf = seg.Array!;
            int baseOffset = seg.Offset;
            if (seg.Count < 2)
                return null;

            byte fuIndicator = buf[baseOffset];
            byte fuHeader = buf[baseOffset + 1];

            bool isStart = (fuHeader & 0x80) != 0;
            bool isEnd = (fuHeader & 0x40) != 0;
            bool isReserved = (fuHeader & 0x20) != 0;
            byte nalType = (byte)(fuHeader & 0x1F);

            if (isReserved)
                return null;

            if (isStart)
            {
                // 开始新的分片（丢弃可能未完成的旧分片）
                EnsureFragmentBuffer(InitialFragmentBufferSize);
                _fragmentOffset = 0;
                _currentTimestamp = packet.Timestamp;
                _fragmentStarted = true;
                _expectedSeq = (ushort)(packet.SequenceNumber + 1);

                // 重建NAL头: F|NRI|Type from FU indicator + FU header
                byte reconstructedNal = (byte)((fuIndicator & 0xE0) | nalType);
                _fragmentBuffer![_fragmentOffset++] = reconstructedNal;
            }
            else
            {
                if (!_fragmentStarted || _fragmentBuffer is null)
                    return null;

                // 序列号必须连续：丢中间分片时拼出的 NAL 是坏的，必须整体丢弃
                if (packet.SequenceNumber != _expectedSeq)
                {
                    _fragmentStarted = false;
                    return null;
                }
                _expectedSeq = (ushort)(packet.SequenceNumber + 1);

                // 时间戳必须一致（同一帧的所有分片共享时间戳）
                if (packet.Timestamp != _currentTimestamp)
                {
                    _fragmentStarted = false;
                    return null;
                }
            }

            // 复制分片数据 (跳过FU indicator和FU header)——直接从零拷贝切片读取
            int payloadLen = seg.Count - 2;
            EnsureFragmentCapacity(_fragmentOffset + payloadLen);

            Array.Copy(buf, baseOffset + 2, _fragmentBuffer!, _fragmentOffset, payloadLen);
            _fragmentOffset += payloadLen;

            if (isEnd)
            {
                // 分片结束，返回完整NAL
                _fragmentStarted = false;
                byte[] completeNal = new byte[_fragmentOffset];
                Array.Copy(_fragmentBuffer!, completeNal, _fragmentOffset);

                byte completeNalType = (byte)(completeNal[0] & 0x1F);
                if (completeNalType == 7 || completeNalType == 8)
                    _inbandParamsSeen = true;

                return CreateFrame(completeNal, _currentTimestamp, completeNalType, packet.TrackId, packet.Marker);
            }

            return null;
        }

        /// <summary>
        /// 确保分片缓冲已租用且不小于指定容量
        /// </summary>
        private void EnsureFragmentBuffer(int minCapacity)
        {
            if (_fragmentBuffer == null || _fragmentBuffer.Length < minCapacity)
            {
                if (_fragmentBuffer != null)
                    ArrayPool<byte>.Shared.Return(_fragmentBuffer);
                _fragmentBuffer = ArrayPool<byte>.Shared.Rent(minCapacity);
            }
        }

        /// <summary>
        /// 按需增长分片缓冲（保留已写入数据）——大关键帧不再被丢弃
        /// </summary>
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
            bool isKeyFrame = nalType == 5; // IDR
            bool isSps = nalType == 7;
            bool isPps = nalType == 8;

            // SPS/PPS也标记为关键帧
            if (isSps || isPps)
                isKeyFrame = true;

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
