using System;
using System.Collections.Generic;
using System.Linq;

namespace Cyaim.RTSPClient.Media
{
    /// <summary>
    /// RFC 4566 规范的 SDP 解析器
    /// </summary>
    public class SDPParser
    {
        /// <summary>
        /// 解析 SDP 文本
        /// </summary>
        public static SDPSession Parse(string sdpText)
        {
            if (string.IsNullOrEmpty(sdpText))
                throw new ArgumentNullException(nameof(sdpText));

            var session = new SDPSession();
            var lines = sdpText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            MediaDescription? currentMedia = null;

            foreach (var line in lines)
            {
                if (line.Length < 2 || line[1] != '=')
                    continue;

                char type = line[0];
                string value = line.Substring(2);

                switch (type)
                {
                    case 'v':
                        session.Version = ParseVersion(value);
                        break;
                    case 'o':
                        session.Origin = ParseOrigin(value);
                        break;
                    case 's':
                        session.SessionName = value;
                        break;
                    case 'i':
                        if (currentMedia != null)
                            currentMedia.MediaTitle = value;
                        else
                            session.SessionInformation = value;
                        break;
                    case 'u':
                        session.Uri = value;
                        break;
                    case 'e':
                        session.Email = value;
                        break;
                    case 'p':
                        session.Phone = value;
                        break;
                    case 'c':
                        var conn = ParseConnection(value);
                        if (currentMedia != null)
                            currentMedia.Connection = conn;
                        else
                            session.Connection = conn;
                        break;
                    case 'b':
                        var bw = ParseBandwidth(value);
                        if (currentMedia != null)
                            currentMedia.Bandwidth = bw;
                        else
                            session.Bandwidth = bw;
                        break;
                    case 't':
                        session.Timing = ParseTiming(value);
                        break;
                    case 'r':
                        session.RepeatTimes.Add(ParseRepeatTime(value));
                        break;
                    case 'z':
                        session.TimeZoneAdjustments = value;
                        break;
                    case 'k':
                        if (currentMedia != null)
                            currentMedia.EncryptionKey = value;
                        else
                            session.EncryptionKey = value;
                        break;
                    case 'a':
                        var attr = ParseAttribute(value);
                        if (currentMedia != null)
                            currentMedia.Attributes.Add(attr);
                        else
                            session.Attributes.Add(attr);
                        break;
                    case 'm':
                        currentMedia = ParseMedia(value);
                        session.MediaDescriptions.Add(currentMedia);
                        break;
                }
            }

            // 后处理：解析编码信息
            foreach (var media in session.MediaDescriptions)
            {
                PostProcessMedia(media);
            }

            return session;
        }

        private static int ParseVersion(string value)
        {
            return int.TryParse(value, out int v) ? v : 0;
        }

        private static SDPOrigin ParseOrigin(string value)
        {
            // o=<username> <sess-id> <sess-version> <nettype> <addrtype> <unicast-address>
            var parts = value.Split(' ');
            return new SDPOrigin
            {
                Username = parts.Length > 0 ? parts[0] : "-",
                SessionId = parts.Length > 1 ? parts[1] : "0",
                SessionVersion = parts.Length > 2 ? parts[2] : "0",
                NetworkType = parts.Length > 3 ? parts[3] : "IN",
                AddressType = parts.Length > 4 ? parts[4] : "IP4",
                Address = parts.Length > 5 ? parts[5] : "0.0.0.0"
            };
        }

        private static SDPConnection ParseConnection(string value)
        {
            // c=<nettype> <addrtype> <connection-address>
            var parts = value.Split(' ');
            var conn = new SDPConnection
            {
                NetworkType = parts.Length > 0 ? parts[0] : "IN",
                AddressType = parts.Length > 1 ? parts[1] : "IP4",
                Address = parts.Length > 2 ? parts[2] : "0.0.0.0"
            };

            // 解析 TTL (多播)
            if (conn.Address.Contains('/'))
            {
                var addrParts = conn.Address.Split('/');
                conn.Address = addrParts[0];
                if (addrParts.Length > 1 && int.TryParse(addrParts[1], out int ttl))
                    conn.Ttl = ttl;
                if (addrParts.Length > 2 && int.TryParse(addrParts[2], out int num))
                    conn.NumAddresses = num;
            }

            return conn;
        }

        private static SDPBandwidth ParseBandwidth(string value)
        {
            // b=<bwtype>:<bandwidth>
            var parts = value.Split(':');
            return new SDPBandwidth
            {
                Type = parts.Length > 0 ? parts[0] : "AS",
                Value = parts.Length > 1 && int.TryParse(parts[1], out int bw) ? bw : 0
            };
        }

        private static SDPTiming ParseTiming(string value)
        {
            // t=<start-time> <stop-time>
            var parts = value.Split(' ');
            return new SDPTiming
            {
                StartTime = parts.Length > 0 && long.TryParse(parts[0], out long start) ? start : 0,
                StopTime = parts.Length > 1 && long.TryParse(parts[1], out long stop) ? stop : 0
            };
        }

        private static SDPRepeatTime ParseRepeatTime(string value)
        {
            // r=<repeat interval> <active duration> <offsets from start-time>
            var parts = value.Split(' ');
            return new SDPRepeatTime
            {
                RepeatInterval = parts.Length > 0 ? parts[0] : "",
                ActiveDuration = parts.Length > 1 ? parts[1] : "",
                Offsets = parts.Skip(2).ToList()
            };
        }

        private static SDPAttribute ParseAttribute(string value)
        {
            // a=<attribute>:<value> 或 a=<attribute>
            int colonIndex = value.IndexOf(':');
            if (colonIndex >= 0)
            {
                return new SDPAttribute
                {
                    Name = value.Substring(0, colonIndex),
                    Value = value.Substring(colonIndex + 1)
                };
            }
            return new SDPAttribute { Name = value };
        }

        private static MediaDescription ParseMedia(string value)
        {
            // m=<media> <port> <proto> <fmt> ...
            var parts = value.Split(' ');
            var media = new MediaDescription
            {
                MediaType = parts.Length > 0 ? parts[0] : "",
                Port = parts.Length > 1 && int.TryParse(parts[1].Split('/')[0], out int port) ? port : 0,
                Protocol = parts.Length > 2 ? parts[2] : "",
                Formats = parts.Skip(3).ToList()
            };

            // 解析端口数量
            if (parts.Length > 1 && parts[1].Contains('/'))
            {
                var portParts = parts[1].Split('/');
                if (portParts.Length > 1 && int.TryParse(portParts[1], out int numPorts))
                    media.NumPorts = numPorts;
            }

            return media;
        }

        private static void PostProcessMedia(MediaDescription media)
        {
            // 解析 rtpmap 获取编码信息
            foreach (var attr in media.Attributes.Where(a => a.Name == "rtpmap"))
            {
                if (attr.Value != null)
                {
                    var codec = CodecInfo.ParseFromRtpMap(attr.Value);
                    if (codec != null)
                    {
                        media.Codecs[codec.PayloadType] = codec;
                    }
                }
            }

            // 静态载荷类型（PCMU=0、PCMA=8、G722=9 等）允许省略 rtpmap，
            // 从 m= 行的格式号合成编码信息，否则合法的 G.711 轨会拿不到编码
            foreach (var format in media.Formats)
            {
                if (!int.TryParse(format, out int pt) || media.Codecs.ContainsKey(pt))
                    continue;

                var audioCodec = Common.RTPPayloadTypeHelper.GetAudioCodec(pt);
                var videoCodec = Common.RTPPayloadTypeHelper.GetVideoCodec(pt);
                if (audioCodec == AudioCodec.Unknown && videoCodec == VideoCodec.Unknown)
                    continue;

                media.Codecs[pt] = new CodecInfo
                {
                    PayloadType = pt,
                    AudioCodec = audioCodec,
                    VideoCodec = videoCodec,
                    EncodingName = audioCodec != AudioCodec.Unknown ? audioCodec.ToString() : videoCodec.ToString(),
                    // RFC 3551：静态音频载荷默认 8000Hz（G.711/G.722 时钟均为 8000），L16 除外
                    ClockRate = audioCodec switch
                    {
                        AudioCodec.L16_STEREO or AudioCodec.L16_MONO => 44100,
                        AudioCodec.Unknown => 90000,
                        _ => 8000
                    },
                    Channels = audioCodec == AudioCodec.L16_STEREO ? 2 : 1
                };
            }

            // 解析 fmtp
            foreach (var attr in media.Attributes.Where(a => a.Name == "fmtp"))
            {
                if (attr.Value != null)
                {
                    int spaceIndex = attr.Value.IndexOf(' ');
                    if (spaceIndex > 0 && int.TryParse(attr.Value.Substring(0, spaceIndex), out int payloadType))
                    {
                        if (media.Codecs.TryGetValue(payloadType, out var codec))
                        {
                            codec.ParseFmtp(attr.Value);
                        }
                    }
                }
            }

            // 解析 control 属性
            var controlAttr = media.Attributes.FirstOrDefault(a => a.Name == "control");
            if (controlAttr?.Value != null)
            {
                media.ControlUri = controlAttr.Value;
            }

            // 解析方向属性
            if (media.Attributes.Any(a => a.Name == "sendonly"))
                media.Direction = MediaDirection.SendOnly;
            else if (media.Attributes.Any(a => a.Name == "recvonly"))
                media.Direction = MediaDirection.RecvOnly;
            else if (media.Attributes.Any(a => a.Name == "sendrecv"))
                media.Direction = MediaDirection.SendRecv;
            else if (media.Attributes.Any(a => a.Name == "inactive"))
                media.Direction = MediaDirection.Inactive;
        }
    }

    /// <summary>
    /// SDP 会话描述
    /// </summary>
    public class SDPSession
    {
        public int Version { get; set; }
        public SDPOrigin? Origin { get; set; }
        public string SessionName { get; set; } = "";
        public string? SessionInformation { get; set; }
        public string? Uri { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public SDPConnection? Connection { get; set; }
        public SDPBandwidth? Bandwidth { get; set; }
        public SDPTiming? Timing { get; set; }
        public List<SDPRepeatTime> RepeatTimes { get; set; } = new();
        public string? TimeZoneAdjustments { get; set; }
        public string? EncryptionKey { get; set; }
        public List<SDPAttribute> Attributes { get; set; } = new();
        public List<MediaDescription> MediaDescriptions { get; set; } = new();

        /// <summary>
        /// 获取视频媒体描述
        /// </summary>
        public MediaDescription? GetVideoMedia()
        {
            return MediaDescriptions.FirstOrDefault(m => m.MediaType == "video");
        }

        /// <summary>
        /// 获取音频媒体描述
        /// </summary>
        public MediaDescription? GetAudioMedia()
        {
            return MediaDescriptions.FirstOrDefault(m => m.MediaType == "audio");
        }

        /// <summary>
        /// 获取所有视频媒体描述
        /// </summary>
        public IEnumerable<MediaDescription> GetVideoMedias()
        {
            return MediaDescriptions.Where(m => m.MediaType == "video");
        }

        /// <summary>
        /// 获取所有音频媒体描述
        /// </summary>
        public IEnumerable<MediaDescription> GetAudioMedias()
        {
            return MediaDescriptions.Where(m => m.MediaType == "audio");
        }

        /// <summary>
        /// 获取回传通道（sendonly）
        /// </summary>
        public MediaDescription? GetBackChannel()
        {
            return MediaDescriptions.FirstOrDefault(m =>
                m.Direction == MediaDirection.SendOnly);
        }
    }

    /// <summary>
    /// SDP Origin (o=)
    /// </summary>
    public class SDPOrigin
    {
        public string Username { get; set; } = "-";
        public string SessionId { get; set; } = "0";
        public string SessionVersion { get; set; } = "0";
        public string NetworkType { get; set; } = "IN";
        public string AddressType { get; set; } = "IP4";
        public string Address { get; set; } = "0.0.0.0";
    }

    /// <summary>
    /// SDP Connection (c=)
    /// </summary>
    public class SDPConnection
    {
        public string NetworkType { get; set; } = "IN";
        public string AddressType { get; set; } = "IP4";
        public string Address { get; set; } = "0.0.0.0";
        public int? Ttl { get; set; }
        public int? NumAddresses { get; set; }
    }

    /// <summary>
    /// SDP Bandwidth (b=)
    /// </summary>
    public class SDPBandwidth
    {
        public string Type { get; set; } = "AS";
        public int Value { get; set; }
    }

    /// <summary>
    /// SDP Timing (t=)
    /// </summary>
    public class SDPTiming
    {
        public long StartTime { get; set; }
        public long StopTime { get; set; }
    }

    /// <summary>
    /// SDP Repeat Time (r=)
    /// </summary>
    public class SDPRepeatTime
    {
        public string RepeatInterval { get; set; } = "";
        public string ActiveDuration { get; set; } = "";
        public List<string> Offsets { get; set; } = new();
    }

    /// <summary>
    /// SDP Attribute (a=)
    /// </summary>
    public class SDPAttribute
    {
        public string Name { get; set; } = "";
        public string? Value { get; set; }
    }

    /// <summary>
    /// 媒体描述 (m=)
    /// </summary>
    public class MediaDescription
    {
        public string MediaType { get; set; } = "";
        public int Port { get; set; }
        public int NumPorts { get; set; } = 1;
        public string Protocol { get; set; } = "";
        public List<string> Formats { get; set; } = new();
        public string? MediaTitle { get; set; }
        public SDPConnection? Connection { get; set; }
        public SDPBandwidth? Bandwidth { get; set; }
        public string? EncryptionKey { get; set; }
        public List<SDPAttribute> Attributes { get; set; } = new();
        public string? ControlUri { get; set; }
        public MediaDirection Direction { get; set; } = MediaDirection.SendRecv;
        public Dictionary<int, CodecInfo> Codecs { get; set; } = new();

        /// <summary>
        /// 获取主要编码信息
        /// </summary>
        public CodecInfo? GetPrimaryCodec()
        {
            return Codecs.Values.FirstOrDefault();
        }

        /// <summary>
        /// 获取指定载荷类型的编码信息
        /// </summary>
        public CodecInfo? GetCodec(int payloadType)
        {
            return Codecs.TryGetValue(payloadType, out var codec) ? codec : null;
        }

        /// <summary>
        /// 是否为视频
        /// </summary>
        public bool IsVideo => MediaType == "video";

        /// <summary>
        /// 是否为音频
        /// </summary>
        public bool IsAudio => MediaType == "audio";

        /// <summary>
        /// 是否为回传通道
        /// </summary>
        public bool IsBackChannel => Direction == MediaDirection.SendOnly;
    }

    /// <summary>
    /// 媒体方向
    /// </summary>
    public enum MediaDirection
    {
        SendRecv,
        SendOnly,
        RecvOnly,
        Inactive
    }
}
