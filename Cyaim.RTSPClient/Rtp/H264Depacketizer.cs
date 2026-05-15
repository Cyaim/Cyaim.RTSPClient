using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// H.264 RTP解包器 (RFC 6184)
    /// 支持Single NAL, STAP-A, FU-A模式
    /// </summary>
    public class H264Depacketizer : IRTPDepacketizer
    {
        private byte[]? _fragmentBuffer;
        private int _fragmentOffset;
        private uint _currentTimestamp;
        private bool _fragmentStarted;

        /// <summary>
        /// 输入RTP包，输出完整的H.264 NAL单元
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            if (packet.Payload == null || packet.Payload.Length == 0)
                yield break;

            byte nalHeader = packet.Payload[0];
            byte nalType = (byte)(nalHeader & 0x1F);

            switch (nalType)
            {
                case byte n when n >= 1 && n <= 23:
                    // Single NAL unit
                    yield return CreateFrame(packet.Payload, packet.Timestamp, nalType, packet.TrackId);
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
                        yield return fuFrame.Value;
                    break;

                case 29: // FU-B
                    // 不常见，暂不支持
                    break;
            }
        }

        private IEnumerable<MediaFrame> ParseStapA(RTPPacket packet)
        {
            int offset = 1; // Skip STAP-A header

            while (offset + 2 <= packet.Payload.Length)
            {
                // 2-byte length prefix
                int naluLength = (packet.Payload[offset] << 8) | packet.Payload[offset + 1];
                offset += 2;

                if (offset + naluLength > packet.Payload.Length)
                    break;

                byte[] nalu = new byte[naluLength];
                Array.Copy(packet.Payload, offset, nalu, 0, naluLength);
                offset += naluLength;

                byte naluType = (byte)(nalu[0] & 0x1F);
                yield return CreateFrame(nalu, packet.Timestamp, naluType, packet.TrackId);
            }
        }

        private MediaFrame? ParseFuA(RTPPacket packet)
        {
            if (packet.Payload.Length < 2)
                return null;

            byte fuIndicator = packet.Payload[0];
            byte fuHeader = packet.Payload[1];

            bool isStart = (fuHeader & 0x80) != 0;
            bool isEnd = (fuHeader & 0x40) != 0;
            bool isReserved = (fuHeader & 0x20) != 0;
            byte nalType = (byte)(fuHeader & 0x1F);

            if (isReserved)
                return null;

            if (isStart)
            {
                // 开始新的分片
                _fragmentBuffer = new byte[65536]; // 预分配缓冲区
                _fragmentOffset = 0;
                _currentTimestamp = packet.Timestamp;
                _fragmentStarted = true;

                // 重建NAL头: F|NRI|Type from FU indicator + FU header
                byte reconstructedNal = (byte)((fuIndicator & 0xE0) | nalType);
                _fragmentBuffer[_fragmentOffset++] = reconstructedNal;
            }

            if (!_fragmentStarted || _fragmentBuffer is null)
                return null;

            // 检查时间戳连续性
            if (packet.Timestamp != _currentTimestamp)
            {
                // 时间戳不连续，丢弃当前分片
                _fragmentStarted = false;
                return null;
            }

            // 复制分片数据 (跳过FU indicator和FU header)
            int payloadLen = packet.Payload.Length - 2;
            if (_fragmentOffset + payloadLen > _fragmentBuffer.Length)
            {
                // 缓冲区溢出，丢弃
                _fragmentStarted = false;
                return null;
            }

            Array.Copy(packet.Payload, 2, _fragmentBuffer, _fragmentOffset, payloadLen);
            _fragmentOffset += payloadLen;

            if (isEnd)
            {
                // 分片结束，返回完整NAL
                _fragmentStarted = false;
                byte[] completeNal = new byte[_fragmentOffset];
                Array.Copy(_fragmentBuffer, completeNal, _fragmentOffset);

                byte completeNalType = (byte)(completeNal[0] & 0x1F);
                return CreateFrame(completeNal, _currentTimestamp, completeNalType, packet.TrackId);
            }

            return null;
        }

        private static MediaFrame CreateFrame(byte[] nal, uint timestamp, byte nalType, int trackId)
        {
            bool isKeyFrame = nalType == 5; // IDR
            bool isSps = nalType == 7;
            bool isPps = nalType == 8;

            // SPS/PPS也标记为关键帧
            if (isSps || isPps)
                isKeyFrame = true;

            return new MediaFrame(nal, timestamp, isKeyFrame, StreamType.Video, trackId);
        }

        /// <summary>
        /// 重置解包器状态
        /// </summary>
        public void Reset()
        {
            _fragmentBuffer = null;
            _fragmentOffset = 0;
            _fragmentStarted = false;
        }
    }
}
