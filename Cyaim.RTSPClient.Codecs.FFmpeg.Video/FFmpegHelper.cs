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
        private static readonly object _initLock = new();
        private static volatile bool _initialized;
        private static bool? _available;

        /// <summary>
        /// 初始化 FFmpeg 库（线程安全，可重复调用）
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // 只有真正找到含 FFmpeg 动态库的目录才设置 RootPath，
                // 否则保持 FFmpeg.AutoGen 的默认加载策略（系统库路径 / PATH）
                var path = ProbeFFmpegPath();
                if (path != null)
                    ffmpeg.RootPath = path;

                // 首次 P/Invoke 触发原生库加载，失败会抛出（由 IsAvailable 捕获）
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);

                _initialized = true;
            }
        }

        /// <summary>
        /// 探测 FFmpeg 动态库目录。
        /// 顺序：FFMPEG_PATH 环境变量 → 应用输出目录及其 ffmpeg 子目录 → 常见安装位置。
        /// 目录必须真实包含 avcodec 动态库才算命中。
        /// </summary>
        private static string? ProbeFFmpegPath()
        {
            var candidates = new System.Collections.Generic.List<string?>
            {
                Environment.GetEnvironmentVariable("FFMPEG_PATH"),
                AppContext.BaseDirectory,
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            };

            string libPattern;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                libPattern = "avcodec*.dll";
                candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"));
                candidates.Add(@"C:\ffmpeg\bin");
                candidates.Add(@"C:\Program Files\ffmpeg\bin");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                libPattern = "libavcodec*.dylib";
                candidates.Add("/opt/homebrew/lib");
                candidates.Add("/usr/local/lib");
            }
            else
            {
                libPattern = "libavcodec.so*";
                candidates.Add("/usr/lib/x86_64-linux-gnu");
                candidates.Add("/usr/local/lib");
                candidates.Add("/usr/lib");
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate) &&
                        System.IO.Directory.Exists(candidate) &&
                        System.IO.Directory.GetFiles(candidate, libPattern).Length > 0)
                    {
                        return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 检查 FFmpeg 是否可用（结果缓存；原生库缺失/版本不匹配时返回 false 而不抛异常）
        /// </summary>
        public static bool IsAvailable()
        {
            if (_available.HasValue)
                return _available.Value;

            try
            {
                Initialize();
                var version = ffmpeg.av_version_info();
                _available = !string.IsNullOrEmpty(version);
            }
            catch
            {
                _available = false;
            }
            return _available.Value;
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
