using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.RTSPClient
{

    public class RTSPResponse
    {
        public RTSPResponse() { }

        /// <summary>
        /// Format rtsp control response;Read by line.
        /// </summary>
        /// <param name="response"></param>
        public RTSPResponse(string response, byte[] raw)
        {
            this.Raw = raw;

            if (string.IsNullOrEmpty(response) && raw != null)
            {
                return;
            }
            string[] resLine = response.Split(new char[] { '\r', '\n' });
            if (resLine.Length < 1)
            {
                return;
            }

            string[] res = resLine[0].Split(' ');
            this.Version = res[0];
            this.StatusCode = res[1];
            this.StatusMsg = res[2];

            this.Headers = new List<KeyValuePair<string, string>>();

            for (int i = 1, spaceNum = 0; i < resLine.Length; i++)
            {
                string item = resLine[i];
                bool isSpaces = item == string.Empty;
                if (isSpaces)
                {
                    spaceNum++;
                    continue;
                }

                switch (spaceNum)
                {
                    //header
                    case int _ when spaceNum < 2:
                        {
                            int keyIndex = item.IndexOf(':');
                            string k = item.Substring(0, keyIndex);
                            string v = item.Substring(keyIndex + 1, item.Length - keyIndex - 1).TrimStart();

                            if (k.ToLower() == "cseq")
                            {
                                CSeq = Convert.ToInt32(v);
                                spaceNum = 0;
                                break;
                            }

                            Headers.Add(new KeyValuePair<string, string>(k, v));

                            //if (!isSpaces)
                            //{
                            spaceNum = 0;
                            //}
                        }
                        break;
                    //content
                    case int _ when spaceNum > 1:
                        {
                            ResponseBuilder.Append(item);
                            ResponseBuilder.Append(RTSPConst.CRLF);
                        }
                        break;
                    default:
                        break;
                }


            }

            this.Response = this.ResponseBuilder.ToString();
        }

        /// <summary>
        /// Raw data
        /// </summary>
        public byte[] Raw { get; set; }

        /// <summary>
        /// RTSP version
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// code
        /// </summary>
        public string StatusCode { get; set; }

        /// <summary>
        /// OK
        /// </summary>
        public string StatusMsg { get; set; }

        /// <summary>
        /// seq
        /// </summary>
        public int CSeq { get; set; }

        /// <summary>
        /// Response headers
        /// </summary>
        public List<KeyValuePair<string, string>> Headers { get; set; }

        /// <summary>
        /// Response content
        /// </summary>
        public string Response { get; set; }
        private StringBuilder ResponseBuilder { get; set; } = new StringBuilder();


    }

}
