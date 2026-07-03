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
            req.Append(Sanitize(request.URI));
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
            request.HeaderMaps["Content-Type"] = request.ContentType ?? string.Empty;

            // Content-Length 始终由内容体的 UTF-8 字节数决定，避免字符数/字节数不一致导致帧错位
            request.HeaderMaps["Content-Length"] = string.IsNullOrEmpty(request.Content)
                ? (request.ContentLength ?? string.Empty)
                : Encoding.UTF8.GetByteCount(request.Content).ToString();

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
                req.Append(Sanitize(headValue));
                req.Append(RTSPConst.CRLF);
            }

            if (request.Headers != null && request.Headers.Count > 0)
            {
                foreach (var item in request.Headers)
                {
                    req.Append(Sanitize(item));
                    req.Append(RTSPConst.CRLF);
                }
            }

            req.Append(RTSPConst.CRLF);

            // 追加内容体（旧实现设置了 Content-Length 却从不发送 body，服务器会阻塞等待）
            if (!string.IsNullOrEmpty(request.Content))
            {
                req.Append(request.Content);
            }

            return req.ToString();
        }

        /// <summary>
        /// 剔除 CR/LF，防止外部值（如相机下发的控制 URI）注入头部或分裂请求
        /// </summary>
        private static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value!.IndexOfAny(new[] { '\r', '\n' }) < 0
                ? value
                : value.Replace("\r", "").Replace("\n", "");
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
        /// Request content body.
        /// Set this to have the body emitted after the headers;
        /// Content-Length is computed automatically from its UTF-8 byte count.
        /// </summary>
        public string? Content { get; set; }
    }

}
