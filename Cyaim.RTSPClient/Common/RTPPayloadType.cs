using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.RTSPClient.Common
{
    /// <summary>
    /// RTP Payload type
    /// https://datatracker.ietf.org/doc/html/rfc3551#section-4.5.14
    /// </summary>
    public enum RTPPayloadType
    {
        //8,000 clock rate,1channels
        PCMU = 0,
        //8,000clock rate,1channels
        GSM = 3,
        // 8,000clock rate,1channels
        G723 = 4,
        // 8,000 clock rate, 1channels
        DVI4_1_8k = 5,
        // 16,000clock rate, 1channels
        DVI4_1_16k = 6,
        //8,000 clock rate,1channels
        LPC = 7,
        // 8,000clock rate , 1channels
        PCMA = 8,
        //8,000 clock rate,1channels
        G722 = 9,
        //44,100clock rate,2channels
        L16_2 = 10,
        // 44,100clock rate,1channels
        L16_1 = 11,
        // 8,000 clock rate,1channels
        QCELP = 12,
        // 8,000clock rate,1channels     
        CN = 13,
        // 90,000clock rate,(see text)
        MPA = 14,
        //8,000clock rate,1channels
        G728 = 15,
        // 11,025clock rate，1channels
        DVI4_11025hz = 16,
        //  22,050clock rate ，1channels
        DVI4_22050hz = 17,
        //8,000 clock rate，1channels
        G729 = 18,
    }
}
