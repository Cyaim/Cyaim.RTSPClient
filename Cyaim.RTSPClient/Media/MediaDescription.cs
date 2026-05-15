using System.Collections.Generic;

namespace Cyaim.RTSPClient.Media
{
    /// <summary>
    /// 解析后的SDP媒体行描述
    /// </summary>
    public class MediaDescription
    {
        /// <summary>
        /// 媒体类型 (video, audio, application, text)
        /// </summary>
        public StreamType MediaType { get; set; }

        /// <summary>
        /// 传输端口
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 传输协议 (RTP/AVP, RTP/SAVP, etc.)
        /// </summary>
        public string? Protocol { get; set; }

        /// <summary>
        /// 格式列表 (payload type numbers)
        /// </summary>
        public List<int> Formats { get; set; } = new List<int>();

        /// <summary>
        /// Track ID (from control attribute)
        /// </summary>
        public string? TrackId { get; set; }

        /// <summary>
        /// 完整的control URI
        /// </summary>
        public string? ControlUri { get; set; }

        /// <summary>
        /// 编码信息 (从rtpmap解析)
        /// </summary>
        public Dictionary<int, CodecInfo> Codecs { get; set; } = new Dictionary<int, CodecInfo>();

        /// <summary>
        /// 方向属性 (sendonly, recvonly, sendrecv)
        /// </summary>
        public string Direction { get; set; } = "recvonly";

        /// <summary>
        /// 帧率 (从framerate属性解析)
        /// </summary>
        public double? FrameRate { get; set; }

        /// <summary>
        /// 原始媒体行 (m=...)
        /// </summary>
        public string? RawMediaLine { get; set; }

        /// <summary>
        /// 所有属性行 (a=...)
        /// </summary>
        public List<string> Attributes { get; set; } = new List<string>();

        /// <summary>
        /// 是否为ONVIF回传通道 (sendonly)
        /// </summary>
        public bool IsBackChannel => Direction == "sendonly";

        /// <summary>
        /// 解析媒体类型字符串
        /// </summary>
        public static StreamType ParseMediaType(string type)
        {
            switch (type?.ToLower())
            {
                case "video": return StreamType.Video;
                case "audio": return StreamType.Audio;
                case "application": return StreamType.Application;
                case "text": return StreamType.Text;
                default: return StreamType.Application;
            }
        }
    }
}
