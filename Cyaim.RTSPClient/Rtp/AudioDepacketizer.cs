using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// 音频RTP解包器
    /// 支持G.711A/U、AAC等音频编码
    /// </summary>
    public class AudioDepacketizer : IRTPDepacketizer
    {
        private readonly AudioCodec _codec;
        private readonly int _sampleRate;

        public AudioDepacketizer(AudioCodec codec, int sampleRate = 8000)
        {
            _codec = codec;
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// 输入RTP包，输出音频帧
        /// 音频通常是1个RTP包 = 1个音频帧
        /// </summary>
        public IEnumerable<MediaFrame> Feed(RTPPacket packet)
        {
            if (packet.Payload == null || packet.Payload.Length == 0)
                yield break;

            // 音频通常不需要分片，直接返回
            yield return new MediaFrame(
                packet.Payload,
                packet.Timestamp,
                false, // 音频没有关键帧概念
                StreamType.Audio,
                packet.TrackId
            );
        }

        /// <summary>
        /// 重置解包器状态
        /// </summary>
        public void Reset()
        {
            // 音频解包器无需状态
        }

        /// <summary>
        /// 根据编码类型创建解包器
        /// </summary>
        public static AudioDepacketizer Create(AudioCodec codec, int sampleRate = 8000)
        {
            return new AudioDepacketizer(codec, sampleRate);
        }

        /// <summary>
        /// 根据SDP信息创建解包器
        /// </summary>
        public static AudioDepacketizer CreateFromCodecInfo(Media.CodecInfo codecInfo)
        {
            if (codecInfo == null)
                return new AudioDepacketizer(AudioCodec.Unknown);

            return new AudioDepacketizer(codecInfo.AudioCodec, codecInfo.ClockRate);
        }
    }
}
