using System;
using System.Linq;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Decoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Encoders;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio
{
    /// <summary>
    /// FFmpeg 音频解码器工厂
    /// </summary>
    public class FFmpegAudioDecoderFactory : IAudioDecoderFactory
    {
        public string Name => "FFmpeg Audio";
        public int Priority => 100;
        public bool IsHardwareAccelerated => false;

        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.AAC, AudioCodec.OPUS, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.SPEEX, AudioCodec.VORBIS, AudioCodec.MPA
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable();
        }

        public IAudioDecoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.AAC => new AACDecoder(),
                AudioCodec.OPUS => new OpusDecoder(),
                AudioCodec.AMR => new AMRDecoder(),
                AudioCodec.AMR_WB => new AMRWBDecoder(),
                AudioCodec.SPEEX => new SpeexDecoder(),
                AudioCodec.VORBIS => new VorbisDecoder(),
                AudioCodec.MPA => new MP3Decoder(),
                _ => throw new NotSupportedException($"FFmpeg audio decoder not available for {codec}")
            };
        }
    }

    /// <summary>
    /// FFmpeg 音频编码器工厂
    /// </summary>
    public class FFmpegAudioEncoderFactory : IAudioEncoderFactory
    {
        public string Name => "FFmpeg Audio";
        public int Priority => 100;
        public bool IsHardwareAccelerated => false;

        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.AAC, AudioCodec.OPUS, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.SPEEX, AudioCodec.VORBIS, AudioCodec.MPA
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable();
        }

        public IAudioEncoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.AAC => new AACEncoder(),
                AudioCodec.OPUS => new OpusEncoder(),
                AudioCodec.AMR => new AMREncoder(),
                AudioCodec.AMR_WB => new AMRWBEncoder(),
                AudioCodec.SPEEX => new SpeexEncoder(),
                AudioCodec.VORBIS => new VorbisEncoder(),
                AudioCodec.MPA => new MP3Encoder(),
                _ => throw new NotSupportedException($"FFmpeg audio encoder not available for {codec}")
            };
        }
    }
}
