using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Common
{
    /// <summary>
    /// RTP Payload Type (RFC 3551)
    /// https://datatracker.ietf.org/doc/html/rfc3551#section-4.5.14
    /// </summary>
    public enum RTPPayloadType
    {
        // ===== 静态载荷类型 (0-23) =====
        
        /// <summary>PCMU (G.711 μ-law), 8kHz, mono</summary>
        PCMU = 0,
        
        /// <summary>GSM 06.10, 8kHz</summary>
        GSM = 3,
        
        /// <summary>G.723.1, 8kHz</summary>
        G723 = 4,
        
        /// <summary>DVI4, 8kHz</summary>
        DVI4_8K = 5,
        
        /// <summary>DVI4, 16kHz</summary>
        DVI4_16K = 6,
        
        /// <summary>LPC, 8kHz</summary>
        LPC = 7,
        
        /// <summary>PCMA (G.711 A-law), 8kHz, mono</summary>
        PCMA = 8,
        
        /// <summary>G.722, 16kHz</summary>
        G722 = 9,
        
        /// <summary>L16, 44.1kHz, stereo</summary>
        L16_STEREO = 10,
        
        /// <summary>L16, 44.1kHz, mono</summary>
        L16_MONO = 11,
        
        /// <summary>QCELP, 8kHz</summary>
        QCELP = 12,
        
        /// <summary>Comfort Noise</summary>
        CN = 13,
        
        /// <summary>MPEG-1/2 Audio</summary>
        MPA = 14,
        
        /// <summary>G.728, 8kHz</summary>
        G728 = 15,
        
        /// <summary>DVI4, 11.025kHz</summary>
        DVI4_11025 = 16,
        
        /// <summary>DVI4, 22.05kHz</summary>
        DVI4_22050 = 17,
        
        /// <summary>G.729, 8kHz</summary>
        G729 = 18,

        // ===== 视频静态载荷类型 =====
        
        /// <summary>CellB</summary>
        CELLB = 25,
        
        /// <summary>JPEG</summary>
        JPEG = 26,
        
        /// <summary>nv</summary>
        NV = 28,
        
        /// <summary>H.261</summary>
        H261 = 31,
        
        /// <summary>MPV (MPEG-1/2 Video)</summary>
        MPV = 32,
        
        /// <summary>MP2T (MPEG-2 Transport Stream)</summary>
        MP2T = 33,
        
        /// <summary>H.263</summary>
        H263 = 34,

        // ===== 动态载荷类型 (96-127) =====
        
        /// <summary>动态载荷类型起始</summary>
        DYNAMIC = 96
    }

    /// <summary>
    /// RTP 载荷类型工具类
    /// </summary>
    public static class RTPPayloadTypeHelper
    {
        /// <summary>
        /// 静态载荷类型到 AudioCodec 的映射
        /// </summary>
        private static readonly Dictionary<int, AudioCodec> StaticAudioMap = new()
        {
            [0] = AudioCodec.PCMU,
            [3] = AudioCodec.GSM,
            [4] = AudioCodec.G723,
            [5] = AudioCodec.DVI4_8K,
            [6] = AudioCodec.DVI4_16K,
            [7] = AudioCodec.LPC,
            [8] = AudioCodec.PCMA,
            [9] = AudioCodec.G722,
            [10] = AudioCodec.L16_STEREO,
            [11] = AudioCodec.L16_MONO,
            [12] = AudioCodec.QCELP,
            [13] = AudioCodec.CN,
            [14] = AudioCodec.MPA,
            [15] = AudioCodec.G728,
            [16] = AudioCodec.DVI4_11025,
            [17] = AudioCodec.DVI4_22050,
            [18] = AudioCodec.G729
        };

        /// <summary>
        /// 静态载荷类型到 VideoCodec 的映射
        /// </summary>
        private static readonly Dictionary<int, VideoCodec> StaticVideoMap = new()
        {
            [25] = VideoCodec.Unknown, // CellB
            [26] = VideoCodec.MJPEG,   // JPEG
            [31] = VideoCodec.Unknown, // H.261
            [34] = VideoCodec.Unknown  // H.263
        };

        /// <summary>
        /// 编码名称到 AudioCodec 的映射 (用于动态载荷)
        /// </summary>
        private static readonly Dictionary<string, AudioCodec> AudioNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PCMU"] = AudioCodec.PCMU,
            ["G726"] = AudioCodec.G726,
            ["G726-16"] = AudioCodec.G726,
            ["G726-24"] = AudioCodec.G726,
            ["G726-32"] = AudioCodec.G726,
            ["G726-40"] = AudioCodec.G726,
            ["PCMA"] = AudioCodec.PCMA,
            ["G722"] = AudioCodec.G722,
            ["G728"] = AudioCodec.G728,
            ["G729"] = AudioCodec.G729,
            ["G7291"] = AudioCodec.G7291,
            ["G729D"] = AudioCodec.G729,
            ["G729E"] = AudioCodec.G729,
            ["GSM"] = AudioCodec.GSM,
            ["GSM-EFR"] = AudioCodec.GSM,
            ["LPC"] = AudioCodec.LPC,
            ["DVI4"] = AudioCodec.DVI4_8K,
            ["DVI4/8000"] = AudioCodec.DVI4_8K,
            ["DVI4/16000"] = AudioCodec.DVI4_16K,
            ["DVI4/11025"] = AudioCodec.DVI4_11025,
            ["DVI4/22050"] = AudioCodec.DVI4_22050,
            ["L16"] = AudioCodec.L16_MONO,
            ["L16/44100/2"] = AudioCodec.L16_STEREO,
            ["L16/44100/1"] = AudioCodec.L16_MONO,
            ["QCELP"] = AudioCodec.QCELP,
            ["CN"] = AudioCodec.CN,
            ["MPA"] = AudioCodec.MPA,
            ["AAC"] = AudioCodec.AAC,
            ["MP4A-LATM"] = AudioCodec.AAC,
            ["MPEG4-GENERIC"] = AudioCodec.MPEG4_GENERIC,
            ["AMR"] = AudioCodec.AMR,
            ["AMR/8000"] = AudioCodec.AMR,
            ["AMR-WB"] = AudioCodec.AMR_WB,
            ["AMR-WB/16000"] = AudioCodec.AMR_WB,
            ["OPUS"] = AudioCodec.OPUS,
            ["OPUS/48000/2"] = AudioCodec.OPUS,
            ["VORBIS"] = AudioCodec.VORBIS,
            ["SPEEX"] = AudioCodec.SPEEX,
            ["SPEEX/8000"] = AudioCodec.SPEEX,
            ["SPEEX/16000"] = AudioCodec.SPEEX,
            ["SPEEX/32000"] = AudioCodec.SPEEX,
            ["AC3"] = AudioCodec.AC3,
            ["EAC3"] = AudioCodec.EAC3,
            ["DTS"] = AudioCodec.DTS,
            ["FLAC"] = AudioCodec.FLAC
        };

        /// <summary>
        /// 编码名称到 VideoCodec 的映射
        /// </summary>
        private static readonly Dictionary<string, VideoCodec> VideoNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["H264"] = VideoCodec.H264,
            ["H265"] = VideoCodec.H265,
            ["MP4V-ES"] = VideoCodec.MPEG4,
            ["MPV"] = VideoCodec.MPEG4,
            ["JPEG"] = VideoCodec.MJPEG,
            ["VP8"] = VideoCodec.VP8,
            ["VP9"] = VideoCodec.VP9,
            ["AV1"] = VideoCodec.Unknown
        };

        /// <summary>
        /// 根据载荷类型号获取音频编码
        /// </summary>
        public static AudioCodec GetAudioCodec(int payloadType)
        {
            return StaticAudioMap.TryGetValue(payloadType, out var codec) ? codec : AudioCodec.Unknown;
        }

        /// <summary>
        /// 根据载荷类型号获取视频编码
        /// </summary>
        public static VideoCodec GetVideoCodec(int payloadType)
        {
            return StaticVideoMap.TryGetValue(payloadType, out var codec) ? codec : VideoCodec.Unknown;
        }

        /// <summary>
        /// 根据编码名称获取音频编码
        /// </summary>
        public static AudioCodec GetAudioCodec(string encodingName)
        {
            return AudioNameMap.TryGetValue(encodingName, out var codec) ? codec : AudioCodec.Unknown;
        }

        /// <summary>
        /// 根据编码名称获取视频编码
        /// </summary>
        public static VideoCodec GetVideoCodec(string encodingName)
        {
            return VideoNameMap.TryGetValue(encodingName, out var codec) ? codec : VideoCodec.Unknown;
        }

        /// <summary>
        /// 是否为静态音频载荷类型
        /// </summary>
        public static bool IsStaticAudioPayload(int payloadType)
        {
            return payloadType >= 0 && payloadType <= 18 && StaticAudioMap.ContainsKey(payloadType);
        }

        /// <summary>
        /// 是否为静态视频载荷类型
        /// </summary>
        public static bool IsStaticVideoPayload(int payloadType)
        {
            return StaticVideoMap.ContainsKey(payloadType);
        }

        /// <summary>
        /// 是否为动态载荷类型
        /// </summary>
        public static bool IsDynamicPayload(int payloadType)
        {
            return payloadType >= 96 && payloadType <= 127;
        }

        /// <summary>
        /// 获取编码的默认时钟频率
        /// </summary>
        public static int GetDefaultClockRate(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.PCMU => 8000,
                AudioCodec.PCMA => 8000,
                AudioCodec.GSM => 8000,
                AudioCodec.G723 => 8000,
                AudioCodec.G722 => 8000, // 注意: G.722 使用 8kHz 时钟但实际采样 16kHz
                AudioCodec.G728 => 8000,
                AudioCodec.G729 => 8000,
                AudioCodec.G7291 => 8000,
                AudioCodec.G726 => 8000,
                AudioCodec.AMR => 8000,
                AudioCodec.AMR_WB => 16000,
                AudioCodec.DVI4_8K => 8000,
                AudioCodec.DVI4_16K => 16000,
                AudioCodec.DVI4_11025 => 11025,
                AudioCodec.DVI4_22050 => 22050,
                AudioCodec.L16_MONO => 44100,
                AudioCodec.L16_STEREO => 44100,
                AudioCodec.QCELP => 8000,
                AudioCodec.CN => 8000,
                AudioCodec.LPC => 8000,
                AudioCodec.MPA => 90000,
                AudioCodec.AAC => 44100,
                AudioCodec.AAC_LD => 44100,
                AudioCodec.AAC_ELD => 44100,
                AudioCodec.OPUS => 48000,
                AudioCodec.VORBIS => 44100,
                AudioCodec.SPEEX => 16000,
                AudioCodec.AC3 => 48000,
                AudioCodec.EAC3 => 48000,
                AudioCodec.DTS => 48000,
                AudioCodec.FLAC => 44100,
                _ => 8000
            };
        }

        /// <summary>
        /// 获取编码的默认声道数
        /// </summary>
        public static int GetDefaultChannels(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.L16_STEREO => 2,
                AudioCodec.OPUS => 2,
                AudioCodec.AAC => 2,
                AudioCodec.AAC_LD => 2,
                AudioCodec.AAC_ELD => 2,
                AudioCodec.AC3 => 6,
                AudioCodec.EAC3 => 8,
                AudioCodec.DTS => 6,
                _ => 1
            };
        }
    }
}
