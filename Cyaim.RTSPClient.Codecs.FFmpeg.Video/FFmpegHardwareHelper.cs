using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video
{
    /// <summary>
    /// 硬件加速类型
    /// </summary>
    public enum HardwareAccelerationType
    {
        /// <summary>无硬件加速</summary>
        None,

        /// <summary>NVIDIA CUDA (NVDEC)</summary>
        Cuda,

        /// <summary>NVIDIA NVENC</summary>
        Nvenc,

        /// <summary>Intel Quick Sync Video</summary>
        Qsv,

        /// <summary>AMD AMF</summary>
        Amf,

        /// <summary>VA-API (Linux)</summary>
        Vaapi,

        /// <summary>VideoToolbox (macOS/iOS)</summary>
        VideoToolbox,

        /// <summary>DirectX Video Acceleration (Windows)</summary>
        Dxva2,

        /// <summary>Direct3D 11 Video Acceleration (Windows)</summary>
        D3d11va,

        /// <summary>MediaCodec (Android)</summary>
        MediaCodec,

        /// <summary>Vulkan</summary>
        Vulkan
    }

    /// <summary>
    /// 硬件加速配置
    /// </summary>
    public class HardwareAccelerationConfig
    {
        /// <summary>
        /// 硬件加速类型
        /// </summary>
        public HardwareAccelerationType Type { get; set; } = HardwareAccelerationType.None;

        /// <summary>
        /// 设备索引 (多 GPU 时使用)
        /// </summary>
        public int DeviceIndex { get; set; }

        /// <summary>
        /// 输出像素格式 (硬件解码后是否需要下载到 CPU)
        /// </summary>
        public bool DownloadToCpu { get; set; } = true;

        /// <summary>
        /// 优先使用的硬件加速类型列表 (按优先级排序)
        /// </summary>
        public List<HardwareAccelerationType> PreferredTypes { get; set; } = new();
    }

    /// <summary>
    /// FFmpeg 硬件加速工具类
    /// </summary>
    public static unsafe class FFmpegHardwareHelper
    {
        /// <summary>
        /// 获取支持的硬件加速类型
        /// </summary>
        public static List<HardwareAccelerationType> GetSupportedTypes()
        {
            var supported = new List<HardwareAccelerationType>();

            // 检查每种硬件加速
            if (IsCudaSupported()) supported.Add(HardwareAccelerationType.Cuda);
            if (IsQsvSupported()) supported.Add(HardwareAccelerationType.Qsv);
            if (IsVaapiSupported()) supported.Add(HardwareAccelerationType.Vaapi);
            if (IsDxva2Supported()) supported.Add(HardwareAccelerationType.Dxva2);
            if (IsD3d11vaSupported()) supported.Add(HardwareAccelerationType.D3d11va);
            if (IsVideoToolboxSupported()) supported.Add(HardwareAccelerationType.VideoToolbox);
            if (IsAmfSupported()) supported.Add(HardwareAccelerationType.Amf);
            if (IsVulkanSupported()) supported.Add(HardwareAccelerationType.Vulkan);

            return supported;
        }

        /// <summary>
        /// 获取最佳硬件加速类型
        /// </summary>
        public static HardwareAccelerationType GetBestType(HardwareAccelerationConfig? config = null)
        {
            var supported = GetSupportedTypes();
            if (supported.Count == 0) return HardwareAccelerationType.None;

            // 如果有优先列表，按优先级选择
            if (config?.PreferredTypes?.Count > 0)
            {
                foreach (var preferred in config.PreferredTypes)
                {
                    if (supported.Contains(preferred))
                        return preferred;
                }
            }

            // 默认优先级: NVDEC > QSV > D3D11VA > DXVA2 > VAAPI > VideoToolbox > AMF
            var defaultOrder = new[]
            {
                HardwareAccelerationType.Cuda,
                HardwareAccelerationType.Qsv,
                HardwareAccelerationType.D3d11va,
                HardwareAccelerationType.Dxva2,
                HardwareAccelerationType.Vaapi,
                HardwareAccelerationType.VideoToolbox,
                HardwareAccelerationType.Amf,
                HardwareAccelerationType.Vulkan
            };

            foreach (var type in defaultOrder)
            {
                if (supported.Contains(type))
                    return type;
            }

            return HardwareAccelerationType.None;
        }

        /// <summary>
        /// 获取硬件加速的像素格式
        /// </summary>
        public static AVPixelFormat GetHardwarePixelFormat(HardwareAccelerationType type)
        {
            return type switch
            {
                HardwareAccelerationType.Cuda => AVPixelFormat.AV_PIX_FMT_CUDA,
                HardwareAccelerationType.Qsv => AVPixelFormat.AV_PIX_FMT_QSV,
                HardwareAccelerationType.Vaapi => AVPixelFormat.AV_PIX_FMT_VAAPI,
                HardwareAccelerationType.Dxva2 => AVPixelFormat.AV_PIX_FMT_DXVA2_VLD,
                HardwareAccelerationType.D3d11va => AVPixelFormat.AV_PIX_FMT_D3D11,
                HardwareAccelerationType.VideoToolbox => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
                HardwareAccelerationType.Amf => AVPixelFormat.AV_PIX_FMT_D3D11, // AMF uses D3D11 on Windows
                HardwareAccelerationType.Vulkan => AVPixelFormat.AV_PIX_FMT_VULKAN,
                _ => AVPixelFormat.AV_PIX_FMT_NONE
            };
        }

        /// <summary>
        /// 获取硬件设备类型名称
        /// </summary>
        public static string GetDeviceTypeName(HardwareAccelerationType type)
        {
            return type switch
            {
                HardwareAccelerationType.Cuda => "cuda",
                HardwareAccelerationType.Qsv => "qsv",
                HardwareAccelerationType.Vaapi => "vaapi",
                HardwareAccelerationType.Dxva2 => "dxva2",
                HardwareAccelerationType.D3d11va => "d3d11va",
                HardwareAccelerationType.VideoToolbox => "videotoolbox",
                HardwareAccelerationType.Amf => "amf",
                HardwareAccelerationType.Vulkan => "vulkan",
                _ => ""
            };
        }

        /// <summary>
        /// 配置硬件加速上下文
        /// </summary>
        public static int ConfigureHardwareContext(
            AVCodecContext* codecCtx,
            AVCodec* codec,
            HardwareAccelerationType type,
            int deviceIndex = 0)
        {
            if (type == HardwareAccelerationType.None)
                return 0;

            // 查找硬件设备类型
            var deviceType = GetDeviceTypeName(type);
            if (string.IsNullOrEmpty(deviceType))
                return -1;

            // 创建硬件设备上下文
            AVBufferRef* hwDeviceCtx = null;
            var error = ffmpeg.av_hwdevice_ctx_create(
                &hwDeviceCtx,
                GetHWDeviceType(type),
                null,
                null,
                0);

            if (error < 0)
                return error;

            // 设置解码器的硬件上下文
            codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
            codecCtx->pix_fmt = GetHardwarePixelFormat(type);

            // 释放临时引用
            ffmpeg.av_buffer_unref(&hwDeviceCtx);

            return 0;
        }

        /// <summary>
        /// 从硬件帧下载到 CPU 内存
        /// </summary>
        public static int DownloadFrame(AVFrame* hwFrame, AVFrame* swFrame)
        {
            if (hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA ||
                hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_VAAPI ||
                hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                // 需要转换格式
                return ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
            }

            // 已经是软件帧
            return ffmpeg.av_frame_copy(swFrame, hwFrame);
        }

        #region 平台检测

        private static bool IsCudaSupported()
        {
            try
            {
                // 检查 NVIDIA 驱动
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return Environment.GetEnvironmentVariable("CUDA_PATH") != null ||
                           System.IO.File.Exists(@"C:\Windows\System32\nvcuda.dll");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return System.IO.File.Exists("/dev/nvidia0") ||
                           System.IO.File.Exists("/usr/lib/x86_64-linux-gnu/libcuda.so");
                }
                return false;
            }
            catch { return false; }
        }

        private static bool IsQsvSupported()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return System.IO.File.Exists(@"C:\Windows\System32\libmfxhw64.dll") ||
                           System.IO.File.Exists(@"C:\Windows\System32\libmfx.dll");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return System.IO.File.Exists("/dev/dri/renderD128");
                }
                return false;
            }
            catch { return false; }
        }

        private static bool IsVaapiSupported()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                   System.IO.File.Exists("/dev/dri/renderD128");
        }

        private static bool IsDxva2Supported()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        private static bool IsD3d11vaSupported()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        private static bool IsVideoToolboxSupported()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                   RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
        }

        private static bool IsAmfSupported()
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                       (System.IO.File.Exists(@"C:\Windows\System32\amfrt64.dll") ||
                        Environment.GetEnvironmentVariable("AMD_AMF_SDK_ROOT") != null);
            }
            catch { return false; }
        }

        private static bool IsVulkanSupported()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return System.IO.File.Exists(@"C:\Windows\System32\vulkan-1.dll");
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return System.IO.File.Exists("/usr/lib/x86_64-linux-gnu/libvulkan.so");
                return false;
            }
            catch { return false; }
        }

        #endregion

        private static AVHWDeviceType GetHWDeviceType(HardwareAccelerationType type)
        {
            return type switch
            {
                HardwareAccelerationType.Cuda => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                HardwareAccelerationType.Qsv => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                HardwareAccelerationType.Vaapi => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
                HardwareAccelerationType.Dxva2 => AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                HardwareAccelerationType.D3d11va => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                HardwareAccelerationType.VideoToolbox => AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
                HardwareAccelerationType.Amf => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, // AMF uses D3D11 on Windows
                HardwareAccelerationType.Vulkan => AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,
                _ => AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
            };
        }
    }
}
