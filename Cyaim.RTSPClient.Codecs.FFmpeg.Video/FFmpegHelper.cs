using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video
{
    /// <summary>
    /// FFmpeg 原生库加载器
    /// </summary>
    public static class FFmpegHelper
    {
        private static bool _initialized;

        /// <summary>
        /// 初始化 FFmpeg 库
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            // 设置 FFmpeg 库路径
            ffmpeg.RootPath = GetFFmpegPath();

            // 设置日志级别
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);

            _initialized = true;
        }

        /// <summary>
        /// 获取 FFmpeg 库路径
        /// </summary>
        private static string GetFFmpegPath()
        {
            // 尝试从环境变量获取
            var path = Environment.GetEnvironmentVariable("FFMPEG_PATH");
            if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path))
                return path;

            // 默认路径
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: 检查常见位置
                var candidates = new[]
                {
                    "ffmpeg",
                    @"C:\ffmpeg\bin",
                    @"C:\Program Files\ffmpeg\bin",
                    System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg")
                };

                foreach (var candidate in candidates)
                {
                    if (System.IO.Directory.Exists(candidate))
                        return candidate;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux: /usr/lib 或 /usr/local/lib
                return "/usr/lib/x86_64-linux-gnu";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: Homebrew
                return "/usr/local/lib";
            }

            return "ffmpeg";
        }

        /// <summary>
        /// 检查 FFmpeg 是否可用
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                Initialize();
                var version = ffmpeg.av_version_info();
                return !string.IsNullOrEmpty(version);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取 FFmpeg 版本
        /// </summary>
        public static string GetVersion()
        {
            Initialize();
            return ffmpeg.av_version_info();
        }

        /// <summary>
        /// 检查 FFmpeg 错误
        /// </summary>
        public static unsafe void CheckError(int error)
        {
            if (error < 0)
            {
                var bufferSize = 1024;
                var buffer = stackalloc byte[bufferSize];
                ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
                var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
                throw new FFmpegException(message ?? "Unknown FFmpeg error", error);
            }
        }

        /// <summary>
        /// 分配 AVFormatContext
        /// </summary>
        public static unsafe AVFormatContext* AllocFormatContext()
        {
            return ffmpeg.avformat_alloc_context();
        }

        /// <summary>
        /// 分配 AVCodecContext
        /// </summary>
        public static unsafe AVCodecContext* AllocCodecContext(AVCodec* codec)
        {
            return ffmpeg.avcodec_alloc_context3(codec);
        }

        /// <summary>
        /// 分配 AVFrame
        /// </summary>
        public static unsafe AVFrame* AllocFrame()
        {
            return ffmpeg.av_frame_alloc();
        }

        /// <summary>
        /// 分配 AVPacket
        /// </summary>
        public static unsafe AVPacket* AllocPacket()
        {
            return ffmpeg.av_packet_alloc();
        }

        /// <summary>
        /// 释放 AVFrame
        /// </summary>
        public static unsafe void FreeFrame(AVFrame** frame)
        {
            ffmpeg.av_frame_free(frame);
        }

        /// <summary>
        /// 释放 AVPacket
        /// </summary>
        public static unsafe void FreePacket(AVPacket** packet)
        {
            ffmpeg.av_packet_free(packet);
        }

        /// <summary>
        /// 释放 AVCodecContext
        /// </summary>
        public static unsafe void FreeCodecContext(AVCodecContext** ctx)
        {
            ffmpeg.avcodec_free_context(ctx);
        }
    }

    /// <summary>
    /// FFmpeg 异常
    /// </summary>
    public class FFmpegException : Exception
    {
        public int ErrorCode { get; }

        public FFmpegException(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public FFmpegException(string message) : base(message) { }
    }
}
