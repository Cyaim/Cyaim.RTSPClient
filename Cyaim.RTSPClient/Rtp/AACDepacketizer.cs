using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// AAC RTP 解包器 (RFC 3640)
    /// 支持 AAC-hbr (High Bitrate) 模式
    /// </summary>
    public class AACDepacketizer : IRTPDepacketizer
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _sizeLength;
        private readonly int _indexLength;
        private readonly int _indexDeltaLength;

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
            if (packet.Payload == null || packet.Payload.Length < 4)
                yield break;

            // 解析 AU Header Section
            // 前 2 字节是 AU-headers-length (以 bit 为单位)
            int auHeadersLength = (packet.Payload[0] << 8) | packet.Payload[1];
            int auHeadersLengthBytes = (auHeadersLength + 7) / 8;

            // AU Header 的大小 (每个 AU Header)
            int auHeaderSize = _sizeLength + _indexLength;

            // 计算 AU 数量
            int auCount = 0;
            if (auHeaderSize > 0)
            {
                // 第一个 AU 使用 indexLength，后续使用 indexDeltaLength
                int firstHeaderSize = _sizeLength + _indexLength;
                int subsequentHeaderSize = _sizeLength + _indexDeltaLength;

                if (firstHeaderSize > 0)
                {
                    // 简化计算：假设所有 AU header 大小相同
                    auCount = auHeadersLength / (_sizeLength + _indexLength);
                }
            }

            if (auCount <= 0)
                auCount = 1; // 至少一个 AU

            // 解析 AU 数据
            int offset = 2 + auHeadersLengthBytes;
            int auIndex = 0;

            while (offset < packet.Payload.Length && auIndex < auCount)
            {
                // 读取 AU-size
                int auSize = ReadBits(packet.Payload, 2 * 8 + auIndex * auHeaderSize, _sizeLength);

                if (auSize > 0 && offset + auSize <= packet.Payload.Length)
                {
                    byte[] aacFrame = new byte[auSize];
                    Array.Copy(packet.Payload, offset, aacFrame, 0, auSize);

                    yield return new MediaFrame(
                        aacFrame,
                        packet.Timestamp,
                        false,
                        StreamType.Audio,
                        packet.TrackId
                    );

                    offset += auSize;
                }
                else
                {
                    break;
                }

                auIndex++;
            }
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
            // 无状态需要重置
        }
    }
}
