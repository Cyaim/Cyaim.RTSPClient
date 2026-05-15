using System;
using System.Linq;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video.Decoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video.Encoders;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video
{
    /// <summary>
    /// FFmpeg 视频解码器工厂
    /// </summary>
    public class FFmpegVideoDecoderFactory : IVideoDecoderFactory
    {
        public string Name => "FFmpeg Video";
        public int Priority => 100; // 高于软件实现
        public bool IsHardwareAccelerated => false;

        public VideoCodec[] SupportedCodecs => new[]
        {
            VideoCodec.H264, VideoCodec.H265, VideoCodec.VP8, VideoCodec.VP9, VideoCodec.MJPEG
        };

        public bool CanCreate(VideoCodec codec, bool preferHardware = true)
        {
            // FFmpeg 作为后备，当硬件不可用时使用
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable();
        }

        public bool SupportsDevice(string deviceName) => false;

        public IVideoDecoder Create(VideoCodec codec)
        {
            return codec switch
            {
                VideoCodec.H264 => new H264Decoder(),
                VideoCodec.H265 => new H265Decoder(),
                VideoCodec.VP8 => new VP8Decoder(),
                VideoCodec.VP9 => new VP9Decoder(),
                VideoCodec.MJPEG => new MJPEGDecoder(),
                _ => throw new NotSupportedException($"FFmpeg decoder not available for {codec}")
            };
        }
    }

    /// <summary>
    /// FFmpeg 视频编码器工厂
    /// </summary>
    public class FFmpegVideoEncoderFactory : IVideoEncoderFactory
    {
        public string Name => "FFmpeg Video";
        public int Priority => 100;
        public bool IsHardwareAccelerated => false;

        public VideoCodec[] SupportedCodecs => new[]
        {
            VideoCodec.H264, VideoCodec.H265, VideoCodec.VP8, VideoCodec.VP9
        };

        public bool CanCreate(VideoCodec codec, bool preferHardware = true)
        {
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable();
        }

        public bool SupportsDevice(string deviceName) => false;

        public IVideoEncoder Create(VideoCodec codec)
        {
            return codec switch
            {
                VideoCodec.H264 => new H264Encoder(),
                VideoCodec.H265 => new H265Encoder(),
                VideoCodec.VP8 => new VP8Encoder(),
                VideoCodec.VP9 => new VP9Encoder(),
                _ => throw new NotSupportedException($"FFmpeg encoder not available for {codec}")
            };
        }
    }
}
