using System;
using System.Collections.Generic;
using System.Linq;

namespace Cyaim.RTSPClient.Auth
{
    /// <summary>
    /// HTTP Digest authentication for RTSP (RFC 2617).
    /// Extracts realm/nonce from WWW-Authenticate challenges and computes digest responses.
    /// </summary>
    public class DigestAuthenticator : IRTSPAuthenticator
    {
        private string? _username;
        private string? _password;
        private string? _realm;
        private string? _nonce;

        /// <summary>
        /// Checks whether the response contains a Digest WWW-Authenticate challenge.
        /// </summary>
        /// <param name="challenge">The 401 RTSP response.</param>
        /// <returns><c>true</c> if the WWW-Authenticate header starts with "Digest".</returns>
        public bool CanHandle(RTSPResponse challenge)
        {
            if (challenge?.Headers == null)
                return false;

            var auth = challenge.Headers.FirstOrDefault(x => x.Key == "WWW-Authenticate");
            return auth.Value?.StartsWith("Digest") == true;
        }

        /// <summary>
        /// Update stored credentials after successful auth.
        /// </summary>
        /// <param name="username">The RTSP username.</param>
        /// <param name="password">The RTSP password.</param>
        /// <param name="realm">The authentication realm from the server.</param>
        /// <param name="nonce">The nonce value from the server.</param>
        public void UpdateCredentials(string username, string password, string realm, string nonce)
        {
            _username = username;
            _password = password;
            _realm = realm;
            _nonce = nonce;
        }

        /// <summary>
        /// Given a 401 response, produce the Digest Authorization header value.
        /// Parses realm and nonce from the challenge if present, then computes the digest response.
        /// </summary>
        /// <param name="challenge">The 401 RTSP response containing WWW-Authenticate header. May be <c>null</c>.</param>
        /// <param name="method">The RTSP method being authenticated (e.g., DESCRIBE, SETUP).</param>
        /// <param name="uri">The request URI.</param>
        /// <returns>The Digest Authorization header value.</returns>
        public string ComputeAuthorization(RTSPResponse challenge, string method, string uri)
        {
            if (challenge?.Headers != null)
            {
                var auth = challenge.Headers.FirstOrDefault(x => x.Key == "WWW-Authenticate");
                if (!string.IsNullOrEmpty(auth.Key) && !string.IsNullOrEmpty(auth.Value))
                {
                    ParseDigestParams(auth.Value, out string? realm, out string? nonce);
                    if (!string.IsNullOrEmpty(realm)) _realm = realm;
                    if (!string.IsNullOrEmpty(nonce)) _nonce = nonce;
                }
            }

            return ComputeDigestAuth(_username ?? string.Empty, _password ?? string.Empty, uri, _realm ?? string.Empty, _nonce ?? string.Empty, method);
        }

        /// <summary>
        /// Compute Digest authorization header value per RFC 2617.
        /// </summary>
        /// <param name="username">The RTSP username.</param>
        /// <param name="password">The RTSP password.</param>
        /// <param name="uri">The request URI.</param>
        /// <param name="realm">The authentication realm.</param>
        /// <param name="nonce">The server nonce.</param>
        /// <param name="method">The RTSP method (e.g., DESCRIBE, SETUP).</param>
        /// <returns>Formatted Digest Authorization header value.</returns>
        public static string ComputeDigestAuth(string username, string password, string uri, string realm, string nonce, string method)
        {
            string ha1 = $"{username}:{realm}:{password}".Md532().ToLower();
            string ha2 = $"{method}:{uri}".Md532().ToLower();
            string response = $"{ha1}:{nonce}:{ha2}".Md532().ToLower();

            return $"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }

        /// <summary>
        /// Parse Digest parameters from WWW-Authenticate header.
        /// </summary>
        /// <param name="authHeader">The full WWW-Authenticate header value (e.g., "Digest realm=...,nonce=...").</param>
        /// <param name="realm">Parsed realm value, or <c>null</c> if not found.</param>
        /// <param name="nonce">Parsed nonce value, or <c>null</c> if not found.</param>
        public static void ParseDigestParams(string authHeader, out string? realm, out string? nonce)
        {
            realm = null;
            nonce = null;

            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Digest"))
                return;

            string[] parts = authHeader.Substring(7).Split(',');
            foreach (var part in parts)
            {
                int eqIndex = part.IndexOf('=');
                if (eqIndex < 0) continue;

                string key = part.Substring(0, eqIndex).Trim();
                string value = part.Substring(eqIndex + 1).Trim().Trim('"');

                switch (key)
                {
                    case "realm": realm = value; break;
                    case "nonce": nonce = value; break;
                }
            }
        }
    }
}
