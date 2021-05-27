using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.RTSPClient.Common
{
    public class Timestamp
    {

        public static long GetNowTimestamp()
        {
            return (DateTime.Now.ToUniversalTime().Ticks - 621355968000000000) / 10000000;
        }


    }
}
