using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cyaim.RTSPClient
{
    public class SDP
    {
        public SDP()
        {
        }

        public SDP(string sdp)
        {
            //v=0
            //o=- 0 0 IN IP4 192.168.1.127
            //s=SDP Descrption
            //i=SDP Description
            //a=type:broadcast
            //a=tool:RTSP Streaming Media
            //a=x-qt-text-nam:session descriped
            //a=x-qt-text-inf:1
            //e=NONE
            //c=IN IP4 192.168.1.101
            //t=0 0
            //a=control:*
            //a=range:npt=0-
            //a=x-broadcastcontrol:TIME
            //a=x-copyright: RTSP
            //m=video 0 RTP/AVP 96
            //a=rtpmap:96 H264/90000
            //a=fmtp:96 packetization-mode=1;profile-level-id=420032;sprop-parameter-sets=Z0IAMukASAFHQgAAB9IAAE40CA==,aMqPIA==
            //a=recvonly
            //a=control:trackID=1
            //a=framerate:1
            //a=x-framerate:1
            //m=audio 0 RTP/AVP 8
            //a=rtpmap:8 PCMA/16000/1
            //a=control:trackID=2
            //a=recvonly
            //m=audio 0 RTP/AVP 8
            //a=control:trackID=3
            //a=rtpmap:8 PCMA/16000/1
            //a=sendonly

            MediaDescribes = new List<SDP.MediaDescribe>();
            a = new List<string>();
            r = new List<string>();

            string[] sdps = sdp.Split('\r', '\n');

            bool inMedia = false;
            foreach (var item in sdps)
            {
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }
                int splitIndex = item.IndexOf('=');
                string k = item.Substring(0, splitIndex);
                string v = item.Substring(splitIndex + 1, item.Length - splitIndex - 1);

                switch (k)
                {
                    case "v":
                        this.v = v;
                        break;
                    case "o":
                        o = v;
                        break;
                    case "s":
                        s = v;
                        break;
                    case "i":
                        if (inMedia)
                        {
                            var mediaObj = MediaDescribes.LastOrDefault();
                            mediaObj.i = v;
                            MediaDescribes[MediaDescribes.Count - 1] = mediaObj;
                        }
                        else
                        {
                            i = v;
                        }

                        break;
                    case "u":
                        u = v;
                        break;
                    case "e":
                        e = v;
                        break;
                    case "p":
                        p = v; break;
                    case "c":
                        if (inMedia)
                        {
                            var mediaObj = MediaDescribes.LastOrDefault();
                            mediaObj.c = v;
                            MediaDescribes[MediaDescribes.Count - 1] = mediaObj;
                        }
                        else
                        {
                            c = v;
                        }
                        break;
                    case "b":
                        if (inMedia)
                        {
                            var mediaObj = MediaDescribes.LastOrDefault();
                            mediaObj.b = v;
                            MediaDescribes[MediaDescribes.Count - 1] = mediaObj;
                        }
                        else
                        {
                            b = v;
                        }
                        break;
                    case "z":
                        z = v; break;
                    case "k":
                        if (inMedia)
                        {
                            var mediaObj = MediaDescribes.LastOrDefault();
                            mediaObj.k = v;
                            MediaDescribes[MediaDescribes.Count - 1] = mediaObj;
                        }
                        else
                        {
                            k = v;
                        }
                        break;
                    case "a":
                        if (inMedia)
                        {
                            var mediaObj = MediaDescribes.LastOrDefault();
                            mediaObj.a.Add(v);
                            MediaDescribes[MediaDescribes.Count - 1] = mediaObj;
                        }
                        else
                        {
                            a.Add(v);
                        }
                        break;
                    case "t":
                        t = v;
                        break;
                    case "r":
                        r.Add(v);
                        break;
                    case "m":
                        inMedia = true;
                        MediaDescribes.Add(new MediaDescribe() { m = v });
                        break;
                    default:
                        break;
                }

            }

        }

        /// <summary>
        /// Protocol version
        /// </summary>
        public string v { get; set; }

        /// <summary>
        /// Owner
        /// </summary>
        public string o { get; set; }

        /// <summary>
        /// Session name
        /// </summary>
        public string s { get; set; }

        /// <summary>
        /// Session information
        /// </summary>
        public string i { get; set; }

        /// <summary>
        /// URI of description
        /// </summary>
        public string u { get; set; }

        /// <summary>
        /// Email address
        /// </summary>
        public string e { get; set; }

        /// <summary>
        /// Phone number
        /// </summary>
        public string p { get; set; }

        /// <summary>
        /// connection information
        /// </summary>
        public string c { get; set; }

        /// <summary>
        /// bandwidth information
        /// </summary>
        public string b { get; set; }

        /// <summary>
        /// Time zone adjustments
        /// </summary>
        public string z { get; set; }

        /// <summary>
        /// Encryption key
        /// </summary>
        public string k { get; set; }

        /// <summary>
        /// Zero or more session attributelines
        /// </summary>
        public List<string> a { get; set; }

        /// <summary>
        /// Time the session is active
        /// </summary>
        public string t { get; set; }

        /// <summary>
        /// Zero or more repeat times
        /// </summary>
        public List<string> r { get; set; }

        /// <summary>
        /// Media describe
        /// </summary>
        public List<MediaDescribe> MediaDescribes { get; set; }

        public class MediaDescribe
        {
            /// <summary>
            /// Media name and transport address
            /// </summary>
            public string m { get; set; }

            /// <summary>
            /// Media title
            /// </summary>
            public string i { get; set; }

            /// <summary>
            /// Conection information
            /// </summary>
            public string c { get; set; }

            /// <summary>
            /// Bandwidth information
            /// </summary>
            public string b { get; set; }

            /// <summary>
            /// Encryption key
            /// </summary>
            public string k { get; set; }

            /// <summary>
            /// Zero or more media attributelines
            /// </summary>
            public List<string> a { get; set; } = new List<string>();
        }

    }

}
