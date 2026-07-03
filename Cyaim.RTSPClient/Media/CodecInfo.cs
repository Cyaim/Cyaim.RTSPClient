namespace Cyaim.RTSPClient.Media
{
    /// <summary>
    /// 从SDP rtpmap/fmtp属性解析的编码信息
    /// </summary>
    public class CodecInfo
    {
        /// <summary>
        /// RTP Payload Type (96-127 for dynamic)
        /// </summary>
        public int PayloadType { get; set; }

        /// <summary>
        /// 编码名称 (H264, H265, PCMA, PCMU, AAC, etc.)
        /// </summary>
        public string? EncodingName { get; set; }

        /// <summary>
        /// 时钟频率 (Hz)
        /// </summary>
        public int ClockRate { get; set; }

        /// <summary>
        /// 声道数 (音频)
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// 视频编码类型
        /// </summary>
        public VideoCodec VideoCodec { get; set; }

        /// <summary>
        /// 音频编码类型
        /// </summary>
        public AudioCodec AudioCodec { get; set; }

        /// <summary>
        /// fmtp参数 (packetization-mode, profile-level-id, etc.)
        /// </summary>
        public string? FmtpLine { get; set; }

        /// <summary>
        /// 解析后的fmtp参数字典
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> FmtpParameters { get; set; }
            = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>
        /// H.264 SPS (从sprop-parameter-sets解析)
        /// </summary>
        public byte[]? H264Sps { get; set; }

        /// <summary>
        /// H.264 PPS (从sprop-parameter-sets解析)
        /// </summary>
        public byte[]? H264Pps { get; set; }

        /// <summary>
        /// H.265 VPS (从sprop-vps解析)
        /// </summary>
        public byte[]? H265Vps { get; set; }

        /// <summary>
        /// H.265 SPS (从sprop-sps解析)
        /// </summary>
        public byte[]? H265Sps { get; set; }

        /// <summary>
        /// H.265 PPS (从sprop-pps解析)
        /// </summary>
        public byte[]? H265Pps { get; set; }

        /// <summary>
        /// 从rtpmap行解析编码信息
        /// 格式: "a=rtpmap:96 H264/90000" 或 "a=rtpmap:8 PCMA/16000/1"
        /// </summary>
        public static CodecInfo? ParseFromRtpMap(string rtpMapLine)
        {
            if (string.IsNullOrEmpty(rtpMapLine))
                return null;

            // 格式: payloadType encodingName/clockRate[/channels]
            int spaceIndex = rtpMapLine.IndexOf(' ');
            if (spaceIndex < 0) return null;

            string payloadStr = rtpMapLine.Substring(0, spaceIndex);
            string encodingPart = rtpMapLine.Substring(spaceIndex + 1);

            if (!int.TryParse(payloadStr, out int payloadType))
                return null;

            var info = new CodecInfo { PayloadType = payloadType };

            string[] parts = encodingPart.Split('/');
            if (parts.Length >= 1)
            {
                info.EncodingName = parts[0].ToUpper();
                info.VideoCodec = ParseVideoCodec(info.EncodingName);
                info.AudioCodec = ParseAudioCodec(info.EncodingName);
            }
            if (parts.Length >= 2 && int.TryParse(parts[1], out int clockRate))
            {
                info.ClockRate = clockRate;
            }
            if (parts.Length >= 3 && int.TryParse(parts[2], out int channels))
            {
                info.Channels = channels;
            }

            return info;
        }

        /// <summary>
        /// 解析fmtp行并填充参数
        /// 格式: "a=fmtp:96 packetization-mode=1;profile-level-id=420032;sprop-parameter-sets=Z0IAMukASAFHQgAAB9IAAE40CA==,aMqPIA=="
        /// </summary>
        public void ParseFmtp(string fmtpLine)
        {
            FmtpLine = fmtpLine;

            int spaceIndex = fmtpLine.IndexOf(' ');
            if (spaceIndex < 0) return;

            string paramsPart = fmtpLine.Substring(spaceIndex + 1);
            string[] pairs = paramsPart.Split(';');

            foreach (var pair in pairs)
            {
                int eqIndex = pair.IndexOf('=');
                if (eqIndex < 0) continue;

                string key = pair.Substring(0, eqIndex).Trim();
                string value = pair.Substring(eqIndex + 1).Trim();
                FmtpParameters[key] = value;
            }

            // 解析H.264/H.265特定参数
            if (VideoCodec == VideoCodec.H264)
            {
                ParseH264Parameters();
            }
            else if (VideoCodec == VideoCodec.H265)
            {
                ParseH265Parameters();
            }
        }

        private void ParseH264Parameters()
        {
            if (FmtpParameters.TryGetValue("sprop-parameter-sets", out string? sps) && sps != null)
            {
                string[] parts = sps.Split(',');
                if (parts.Length >= 1)
                {
                    H264Sps = TryFromBase64(parts[0]);
                }
                if (parts.Length >= 2)
                {
                    H264Pps = TryFromBase64(parts[1]);
                }
            }
        }

        private void ParseH265Parameters()
        {
            if (FmtpParameters.TryGetValue("sprop-vps", out string? vps) && vps != null)
                H265Vps = TryFromBase64(vps);
            if (FmtpParameters.TryGetValue("sprop-sps", out string? sps) && sps != null)
                H265Sps = TryFromBase64(sps);
            if (FmtpParameters.TryGetValue("sprop-pps", out string? pps) && pps != null)
                H265Pps = TryFromBase64(pps);
        }

        /// <summary>
        /// 容错的 base64 解码：单台相机的畸形参数集不应让整个 DESCRIBE 失败
        /// </summary>
        private static byte[]? TryFromBase64(string value)
        {
            try
            {
                return System.Convert.FromBase64String(value.Trim());
            }
            catch (System.FormatException)
            {
                return null;
            }
        }

        private static VideoCodec ParseVideoCodec(string name)
        {
            return Common.RTPPayloadTypeHelper.GetVideoCodec(name);
        }

        private static AudioCodec ParseAudioCodec(string name)
        {
            return Common.RTPPayloadTypeHelper.GetAudioCodec(name);
        }
    }
}
