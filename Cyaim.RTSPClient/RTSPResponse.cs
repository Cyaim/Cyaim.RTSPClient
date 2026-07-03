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
        /// <param name="response">Response text</param>
        /// <param name="raw">Raw bytes</param>
        public RTSPResponse(string response, byte[]? raw)
        {
            this.Raw = raw;

            if (string.IsNullOrEmpty(response))
            {
                return;
            }
            string[] resLine = response.Split(new char[] { '\r', '\n' });
            if (resLine.Length < 1)
            {
                return;
            }

            string[] res = resLine[0].Split(' ');
            if (res.Length >= 3)
            {
                this.Version = res[0];
                this.StatusCode = res[1];
                this.StatusMsg = res[2];
            }

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
                            if (keyIndex > 0)
                            {
                                string k = item.Substring(0, keyIndex);
                                string v = item.Substring(keyIndex + 1, item.Length - keyIndex - 1).TrimStart();

                                if (k.ToLower() == "cseq")
                                {
                                    // 容错：非数字 CSeq 不抛异常（否则单条畸形响应会杀死整个接收循环）
                                    int.TryParse(v, out int cseq);
                                    CSeq = cseq;
                                    spaceNum = 0;
                                    break;
                                }

                                Headers.Add(new KeyValuePair<string, string>(k, v));
                            }
                            spaceNum = 0;
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
        public byte[]? Raw { get; set; }

        /// <summary>
        /// RTSP version
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Status code
        /// </summary>
        public string StatusCode { get; set; } = string.Empty;

        /// <summary>
        /// Status message
        /// </summary>
        public string StatusMsg { get; set; } = string.Empty;

        /// <summary>
        /// CSeq number
        /// </summary>
        public int CSeq { get; set; }

        /// <summary>
        /// Response headers
        /// </summary>
        public List<KeyValuePair<string, string>> Headers { get; set; } = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Response content body
        /// </summary>
        public string Response { get; set; } = string.Empty;

        private StringBuilder ResponseBuilder { get; set; } = new StringBuilder();
    }

}
