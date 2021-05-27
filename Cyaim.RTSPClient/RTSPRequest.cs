using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cyaim.RTSPClient
{

    public class RTSPRequest
    {
        public Dictionary<string, string> HeaderMaps = new Dictionary<string, string>()
            {
                { "CSeq",string.Empty},
                { "User-Agent",string.Empty},
                { "Authorization",string.Empty},
                { "Accept",string.Empty},
                { "Require",string.Empty},
                { "Session",string.Empty},
                { "Transport",string.Empty},
                { "Range",string.Empty},
                { "Content-Base",string.Empty},
                { "Content-Length",string.Empty},
                { "Content-Type",string.Empty},
            };

        public static string GetRequest(RTSPRequest request)
        {
            StringBuilder req = new StringBuilder();
            req.Append(request.Method);
            req.Append(RTSPConst.Space);
            req.Append(request.URI);
            req.Append(RTSPConst.Space);
            req.Append(request.Version);
            req.Append(RTSPConst.CRLF);

            request.HeaderMaps["CSeq"] = request.CSeq + string.Empty;
            request.HeaderMaps["User-Agent"] = request.UserAgent;
            request.HeaderMaps["Authorization"] = request.Authorization;
            request.HeaderMaps["Accept"] = request.Accept;
            request.HeaderMaps["Require"] = request.Require;
            request.HeaderMaps["Session"] = request.Session;
            request.HeaderMaps["Transport"] = request.Transport;
            request.HeaderMaps["Range"] = request.Range;
            request.HeaderMaps["Content-Base"] = request.ContentBase;
            request.HeaderMaps["Content-Length"] = request.ContentLength;
            request.HeaderMaps["Content-Type"] = request.ContentType;

            var hmKeys = request.HeaderMaps.Keys;
            foreach (var item in hmKeys)
            {
                string headValue = request.HeaderMaps[item];
                if (string.IsNullOrEmpty(headValue))
                {
                    continue;
                }

                req.Append(item);
                req.Append(RTSPConst.HeaderSplit);
                req.Append(GetRequestHeaderValue(request.Headers, item, headValue));
                req.Append(RTSPConst.CRLF);
            }

            if (request.Headers != null && request.Headers.Count > 0)
            {
                foreach (var item in request.Headers)
                {
                    req.Append(item);
                    req.Append(RTSPConst.CRLF);
                }
            }

            req.Append(RTSPConst.CRLF);
            return req.ToString();
        }

        private static string GetRequestHeaderValue(List<string> headers, string headerKey, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                string keyValue = headers.Where(x => x != null).Where(x => x.ToLower().Contains(headerKey.ToLower())).LastOrDefault();

                return keyValue ?? string.Empty;
            }
            else
            {
                return value;
            }
        }

        /// <summary>
        /// Reuest method
        /// </summary>
        public string Method { get; set; }


        public string URI { get; set; }

        public string Version { get; set; }


        /// <summary>
        /// TCP seq
        /// </summary>
        public int CSeq { get; set; }

        public string UserAgent { get; set; } = "Cyaim RTSP Client 1.0";

        public string Authorization { get; set; }

        public string Accept { get; set; }

        public string Require { get; set; }

        public string Session { get; set; }

        public string Transport { get; set; }

        public string Range { get; set; }

        public string ContentBase { get; set; }
        public string ContentLength { get; set; }
        public string ContentType { get; set; }

        /// <summary>
        /// Add other request parameters
        /// </summary>
        public List<string> Headers { get; set; }

        /// <summary>
        /// Add other request content
        /// </summary>
        public List<string> Content { get; set; }
    }

}
