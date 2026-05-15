using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// 编解码器工厂 - 统一创建和管理编解码器实例
    /// </summary>
    public sealed class CodecFactory
    {
        private static readonly Lazy<CodecFactory> _instance = new(() => new CodecFactory());
        public static CodecFactory Instance => _instance.Value;

        private readonly List<IVideoDecoderFactory> _videoDecoderFactories = new();
        private readonly List<IVideoEncoderFactory> _videoEncoderFactories = new();
        private readonly List<IAudioDecoderFactory> _audioDecoderFactories = new();
        private readonly List<IAudioEncoderFactory> _audioEncoderFactories = new();

        private CodecFactory()
        {
            // 注册内置软件实现
            RegisterFactory(new SoftwareVideoDecoderFactory());
            RegisterFactory(new SoftwareVideoEncoderFactory());
            RegisterFactory(new SoftwareAudioDecoderFactory());
            RegisterFactory(new SoftwareAudioEncoderFactory());
        }

        #region 注册工厂

        public void RegisterFactory(IVideoDecoderFactory factory)
        {
            _videoDecoderFactories.Add(factory);
            _videoDecoderFactories.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public void RegisterFactory(IVideoEncoderFactory factory)
        {
            _videoEncoderFactories.Add(factory);
            _videoEncoderFactories.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public void RegisterFactory(IAudioDecoderFactory factory)
        {
            _audioDecoderFactories.Add(factory);
            _audioDecoderFactories.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public void RegisterFactory(IAudioEncoderFactory factory)
        {
            _audioEncoderFactories.Add(factory);
            _audioEncoderFactories.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        #endregion

        #region 创建解码器/编码器

        /// <summary>
        /// 创建视频解码器（自动选择最佳实现）
        /// </summary>
        public IVideoDecoder CreateVideoDecoder(VideoCodec codec, bool preferHardware = true)
        {
            var factory = _videoDecoderFactories.FirstOrDefault(f => f.CanCreate(codec, preferHardware));
            return factory?.Create(codec)
                ?? throw new NotSupportedException($"No decoder found for {codec}");
        }

        /// <summary>
        /// 创建视频解码器（指定硬件设备）
        /// </summary>
        public IVideoDecoder CreateVideoDecoder(VideoDecoderConfig config)
        {
            var factory = _videoDecoderFactories.FirstOrDefault(f =>
                f.CanCreate(config.Codec, config.EnableHardwareAcceleration) &&
                (string.IsNullOrEmpty(config.HardwareDevice) || f.SupportsDevice(config.HardwareDevice)));

            var decoder = factory?.Create(config.Codec)
                ?? throw new NotSupportedException($"No decoder found for {config.Codec}");

            decoder.InitializeAsync(config).GetAwaiter().GetResult();
            return decoder;
        }

        /// <summary>
        /// 创建视频编码器（自动选择最佳实现）
        /// </summary>
        public IVideoEncoder CreateVideoEncoder(VideoCodec codec, bool preferHardware = true)
        {
            var factory = _videoEncoderFactories.FirstOrDefault(f => f.CanCreate(codec, preferHardware));
            return factory?.Create(codec)
                ?? throw new NotSupportedException($"No encoder found for {codec}");
        }

        /// <summary>
        /// 创建音频解码器
        /// </summary>
        public IAudioDecoder CreateAudioDecoder(AudioCodec codec, bool preferHardware = true)
        {
            var factory = _audioDecoderFactories.FirstOrDefault(f => f.CanCreate(codec, preferHardware));
            return factory?.Create(codec)
                ?? throw new NotSupportedException($"No decoder found for {codec}");
        }

        /// <summary>
        /// 创建音频编码器
        /// </summary>
        public IAudioEncoder CreateAudioEncoder(AudioCodec codec, bool preferHardware = true)
        {
            var factory = _audioEncoderFactories.FirstOrDefault(f => f.CanCreate(codec, preferHardware));
            return factory?.Create(codec)
                ?? throw new NotSupportedException($"No encoder found for {codec}");
        }

        #endregion

        #region 查询能力

        /// <summary>
        /// 获取支持的视频解码器
        /// </summary>
        public IEnumerable<CodecInfo> GetSupportedVideoDecoders()
        {
            return _videoDecoderFactories
                .SelectMany(f => f.SupportedCodecs.Select(c => new CodecInfo
                {
                    Codec = c.ToString(),
                    IsHardwareAccelerated = f.IsHardwareAccelerated,
                    Name = f.Name
                }))
                .Distinct();
        }

        /// <summary>
        /// 获取支持的视频编码器
        /// </summary>
        public IEnumerable<CodecInfo> GetSupportedVideoEncoders()
        {
            return _videoEncoderFactories
                .SelectMany(f => f.SupportedCodecs.Select(c => new CodecInfo
                {
                    Codec = c.ToString(),
                    IsHardwareAccelerated = f.IsHardwareAccelerated,
                    Name = f.Name
                }))
                .Distinct();
        }

        /// <summary>
        /// 检查硬件加速支持
        /// </summary>
        public HardwareSupport CheckHardwareSupport()
        {
            return new HardwareSupport
            {
                HasNvidia = CheckNvidiaSupport(),
                HasIntelQsv = CheckIntelQsvSupport(),
                HasAmdAmf = CheckAmdAmfSupport(),
                HasDxva = CheckDxvaSupport(),
                HasVideoToolbox = CheckVideoToolboxSupport(),
                HasV4L2 = CheckV4L2Support()
            };
        }

        private static bool CheckNvidiaSupport()
        {
            // 检查 NVIDIA GPU 是否可用
            try
            {
                return Environment.GetEnvironmentVariable("CUDA_PATH") != null ||
                       System.IO.Directory.Exists("/usr/local/cuda");
            }
            catch { return false; }
        }

        private static bool CheckIntelQsvSupport()
        {
            try
            {
                return Environment.GetEnvironmentVariable("ONEAPI_ROOT") != null ||
                       System.IO.File.Exists("/dev/dri/renderD128");
            }
            catch { return false; }
        }

        private static bool CheckAmdAmfSupport()
        {
            try
            {
                return Environment.GetEnvironmentVariable("AMD_AMF_SDK_ROOT") != null;
            }
            catch { return false; }
        }

        private static bool CheckDxvaSupport()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        private static bool CheckVideoToolboxSupport()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                   RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
        }

        private static bool CheckV4L2Support()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        }

        #endregion
    }

    #region 工厂接口

    public interface IVideoDecoderFactory
    {
        string Name { get; }
        int Priority { get; }
        bool IsHardwareAccelerated { get; }
        VideoCodec[] SupportedCodecs { get; }
        bool CanCreate(VideoCodec codec, bool preferHardware = true);
        bool SupportsDevice(string deviceName);
        IVideoDecoder Create(VideoCodec codec);
    }

    public interface IVideoEncoderFactory
    {
        string Name { get; }
        int Priority { get; }
        bool IsHardwareAccelerated { get; }
        VideoCodec[] SupportedCodecs { get; }
        bool CanCreate(VideoCodec codec, bool preferHardware = true);
        bool SupportsDevice(string deviceName);
        IVideoEncoder Create(VideoCodec codec);
    }

    public interface IAudioDecoderFactory
    {
        string Name { get; }
        int Priority { get; }
        bool IsHardwareAccelerated { get; }
        AudioCodec[] SupportedCodecs { get; }
        bool CanCreate(AudioCodec codec, bool preferHardware = true);
        IAudioDecoder Create(AudioCodec codec);
    }

    public interface IAudioEncoderFactory
    {
        string Name { get; }
        int Priority { get; }
        bool IsHardwareAccelerated { get; }
        AudioCodec[] SupportedCodecs { get; }
        bool CanCreate(AudioCodec codec, bool preferHardware = true);
        IAudioEncoder Create(AudioCodec codec);
    }

    #endregion

    #region 辅助类型

    public class CodecInfo
    {
        public string Codec { get; init; } = "";
        public bool IsHardwareAccelerated { get; init; }
        public string Name { get; init; } = "";
    }

    public class HardwareSupport
    {
        public bool HasNvidia { get; init; }
        public bool HasIntelQsv { get; init; }
        public bool HasAmdAmf { get; init; }
        public bool HasDxva { get; init; }
        public bool HasVideoToolbox { get; init; }
        public bool HasV4L2 { get; init; }
    }

    #endregion
}
