using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.RTSPClient
{

    /// <summary>
    /// RTPHeader
    /// https://datatracker.ietf.org/doc/html/rfc3550#section-5.1
    /// </summary>
    public struct RTPHeader
    {
        /// <summary>
        /// 2bits
        /// </summary>
        public byte[] Version { get; set; }

        public byte Padding { get; set; }

        public byte Extension { get; set; }

        /// <summary>
        /// CSRC count (CC): 4 bits
        /// The CSRC count contains the number of CSRC identifiers that follow the fixed header.
        /// </summary>
        public byte[] CSRCCount { get; set; }

        /// <summary>
        /// 1bit
        /// </summary>
        public byte Maker { get; set; }

        /// <summary>
        /// 7bits
        /// </summary>
        public byte[] PayloadType { get; set; }

        /// <summary>
        /// 16bits
        /// </summary>
        public byte[] SequenceNumber { get; set; }

        /// <summary>
        /// 32bits
        /// </summary>
        public byte[] Timestamp { get; set; }

        /// <summary>
        /// 32bits
        /// </summary>
        public byte[] SSRC { get; set; }


        /// <summary>
        /// CSRC list: 0 to 15 items, 32 bits each
        /// </summary>
        public byte[] CSRC { get; set; }
    }

}
