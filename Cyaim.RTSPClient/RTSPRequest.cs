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
            request.HeaderMaps["User-Agent"] = request.UserAgent ?? string.Empty;
            request.HeaderMaps["Authorization"] = request.Authorization ?? string.Empty;
            request.HeaderMaps["Accept"] = request.Accept ?? string.Empty;
            request.HeaderMaps["Require"] = request.Require ?? string.Empty;
            request.HeaderMaps["Session"] = request.Session ?? string.Empty;
            request.HeaderMaps["Transport"] = request.Transport ?? string.Empty;
            request.HeaderMaps["Range"] = request.Range ?? string.Empty;
            request.HeaderMaps["Content-Base"] = request.ContentBase ?? string.Empty;
            request.HeaderMaps["Content-Length"] = request.ContentLength ?? string.Empty;
            request.HeaderMaps["Content-Type"] = request.ContentType ?? string.Empty;

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

        private static string GetRequestHeaderValue(List<string>? headers, string headerKey, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                string? keyValue = headers?
                    .Where(x => x != null)
                    .Where(x => x.ToLower().Contains(headerKey.ToLower()))
                    .LastOrDefault();

                return keyValue ?? string.Empty;
            }
            else
            {
                return value;
            }
        }

        /// <summary>
        /// Request method
        /// </summary>
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Request URI
        /// </summary>
        public string URI { get; set; } = string.Empty;

        /// <summary>
        /// RTSP version
        /// </summary>
        public string Version { get; set; } = "RTSP/1.0";

        /// <summary>
        /// CSeq number
        /// </summary>
        public int CSeq { get; set; }

        /// <summary>
        /// User-Agent header
        /// </summary>
        public string UserAgent { get; set; } = "Cyaim RTSP Client 2.0";

        /// <summary>
        /// Authorization header
        /// </summary>
        public string? Authorization { get; set; }

        /// <summary>
        /// Accept header
        /// </summary>
        public string? Accept { get; set; }

        /// <summary>
        /// Require header
        /// </summary>
        public string? Require { get; set; }

        /// <summary>
        /// Session header
        /// </summary>
        public string? Session { get; set; }

        /// <summary>
        /// Transport header
        /// </summary>
        public string? Transport { get; set; }

        /// <summary>
        /// Range header
        /// </summary>
        public string? Range { get; set; }

        /// <summary>
        /// Content-Base header
        /// </summary>
        public string? ContentBase { get; set; }

        /// <summary>
        /// Content-Length header
        /// </summary>
        public string? ContentLength { get; set; }

        /// <summary>
        /// Content-Type header
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Additional request headers
        /// </summary>
        public List<string>? Headers { get; set; }

        /// <summary>
        /// Request content body
        /// </summary>
        public List<string>? Content { get; set; }
    }

}
