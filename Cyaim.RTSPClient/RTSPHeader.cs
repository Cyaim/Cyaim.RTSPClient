using System;

namespace Cyaim.RTSPClient
{
    public struct RTSPHeader
    {
        /// <summary>
        /// Rtsp Magic
        /// RTSP index 1
        /// </summary>
        public byte Magic { get; set; }

        /// <summary>
        /// Channel
        /// RTSP index 2
        /// </summary>
        public byte Channel { get; set; }

        /// <summary>
        /// Length1
        /// RTSP index 3
        /// </summary>
        public byte Length1 { get; set; }

        /// <summary>
        /// Length2
        /// RTSP index 4
        /// </summary>
        public byte Length2 { get; set; }

    }
}
