using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// H.265 RTP解包器 (RFC 7798)
    /// 支持Single NAL, AP (Aggregation Packet), FU (Fragmentation Unit)
    /// </summary>
    public class H265Depacketizer : IRTPDepacketizer
    {
        private byte[]? _fragmentBuffer;
        private int _fragmentOffset;
        private uint _currentTimestamp;
        private bool _fragmentStarted;

        // H.265 NAL unit types
        private const byte NAL_TYPE_VPS = 32;
        private const byte NAL_TYPE_SPS = 33;
        private const byte NAL_TYPE_PPS = 34;
        private const byte NAL_TYPE_IDR_W_RADL = 19;
        private const byte NAL_TYPE_IDR_N_LP = 20;
        private const byte NAL_TYPE_CRA = 21;

        /// <summary>
        /// 输入RTP包，输出完整的H.265 NAL单元
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            if (packet.Payload == null || packet.Payload.Length < 2)
                yield break;

            // H.265 RTP头: 2 bytes (F|Type|LayerId|TID)
            byte firstByte = packet.Payload[0];
            byte secondByte = packet.Payload[1];

            byte nalType = (byte)((firstByte >> 1) & 0x3F);

            switch (nalType)
            {
                case byte n when n >= 0 && n <= 47:
                    // Single NAL unit (包括VPS=32, SPS=33, PPS=34)
                    if (n <= 47)
                    {
                        yield return CreateFrame(packet.Payload, packet.Timestamp, nalType, packet.TrackId);
                    }
                    break;

                case 48: // AP (Aggregation Packet)
                    foreach (var frame in ParseAggregationPacket(packet))
                        yield return frame;
                    break;

                case 49: // FU (Fragmentation Unit)
                    var fuFrame = ParseFu(packet);
                    if (fuFrame.HasValue)
                        yield return fuFrame.Value;
                    break;

                case 50: // PACI (Packing Information)
                    // 不常见，暂不支持
                    break;
            }
        }

        private IEnumerable<MediaFrame> ParseAggregationPacket(RTPPacket packet)
        {
            // AP格式: 2-byte header + NAL units with 2-byte size prefix
            int offset = 2;

            while (offset + 2 <= packet.Payload.Length)
            {
                int naluSize = (packet.Payload[offset] << 8) | packet.Payload[offset + 1];
                offset += 2;

                if (offset + naluSize > packet.Payload.Length)
                    break;

                byte[] nalu = new byte[naluSize];
                Array.Copy(packet.Payload, offset, nalu, 0, naluSize);
                offset += naluSize;

                byte nalType = (byte)((nalu[0] >> 1) & 0x3F);
                yield return CreateFrame(nalu, packet.Timestamp, nalType, packet.TrackId);
            }
        }

        private MediaFrame? ParseFu(RTPPacket packet)
        {
            if (packet.Payload.Length < 3)
                return null;

            // FU header: F|Type (1 byte)
            byte fuHeader = packet.Payload[2];

            bool isStart = (fuHeader & 0x80) != 0;
            bool isEnd = (fuHeader & 0x40) != 0;
            byte nalType = (byte)(fuHeader & 0x3F);

            if (isStart)
            {
                // 开始新的分片
                _fragmentBuffer = new byte[65536];
                _fragmentOffset = 0;
                _currentTimestamp = packet.Timestamp;
                _fragmentStarted = true;

                // 重建NAL头: 2 bytes (Type from FU + original LayerId/TID)
                _fragmentBuffer[0] = (byte)((packet.Payload[0] & 0x81) | (nalType << 1));
                _fragmentBuffer[1] = packet.Payload[1];
                _fragmentOffset = 2;
            }

            if (!_fragmentStarted || _fragmentBuffer is null)
                return null;

            // 检查时间戳连续性
            if (packet.Timestamp != _currentTimestamp)
            {
                _fragmentStarted = false;
                return null;
            }

            // 复制分片数据 (跳过2-byte RTP头 + 1-byte FU header)
            int payloadLen = packet.Payload.Length - 3;
            if (_fragmentOffset + payloadLen > _fragmentBuffer.Length)
            {
                _fragmentStarted = false;
                return null;
            }

            Array.Copy(packet.Payload, 3, _fragmentBuffer, _fragmentOffset, payloadLen);
            _fragmentOffset += payloadLen;

            if (isEnd)
            {
                _fragmentStarted = false;
                byte[] completeNal = new byte[_fragmentOffset];
                Array.Copy(_fragmentBuffer, completeNal, _fragmentOffset);

                byte completeNalType = (byte)((completeNal[0] >> 1) & 0x3F);
                return CreateFrame(completeNal, _currentTimestamp, completeNalType, packet.TrackId);
            }

            return null;
        }

        private static MediaFrame CreateFrame(byte[] nal, uint timestamp, byte nalType, int trackId)
        {
            bool isKeyFrame = nalType == NAL_TYPE_IDR_W_RADL ||
                              nalType == NAL_TYPE_IDR_N_LP ||
                              nalType == NAL_TYPE_CRA ||
                              nalType == NAL_TYPE_VPS ||
                              nalType == NAL_TYPE_SPS ||
                              nalType == NAL_TYPE_PPS;

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
