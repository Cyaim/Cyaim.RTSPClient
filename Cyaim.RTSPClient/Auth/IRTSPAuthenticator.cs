using Cyaim.RTSPClient;

namespace Cyaim.RTSPClient.Auth
{
    /// <summary>
    /// RTSP authentication interface.
    /// Implementations handle specific auth schemes (e.g., Digest) for RTSP 401 challenges.
    /// </summary>
    [System.Obsolete("认证已内建于 RTSPSession（自动 401 重试，支持 Digest qop=auth 与 Basic）。此类型将在后续版本移除。")]
    public interface IRTSPAuthenticator
    {
        /// <summary>
        /// Given a 401 response, produce the Authorization header value.
        /// </summary>
        /// <param name="challenge">The 401 RTSP response containing WWW-Authenticate header.</param>
        /// <param name="method">The RTSP method being authenticated (e.g., DESCRIBE, SETUP).</param>
        /// <param name="uri">The request URI.</param>
        /// <returns>The Authorization header value (e.g., "Digest username=...").</returns>
        string ComputeAuthorization(RTSPResponse challenge, string method, string uri);

        /// <summary>
        /// Whether this authenticator can handle the given challenge.
        /// </summary>
        /// <param name="challenge">The 401 RTSP response to check.</param>
        /// <returns><c>true</c> if this authenticator supports the auth scheme in the challenge.</returns>
        bool CanHandle(RTSPResponse challenge);

        /// <summary>
        /// Update stored credentials after successful auth.
        /// </summary>
        /// <param name="username">The RTSP username.</param>
        /// <param name="password">The RTSP password.</param>
        /// <param name="realm">The authentication realm from the server.</param>
        /// <param name="nonce">The nonce value from the server.</param>
        void UpdateCredentials(string username, string password, string realm, string nonce);
    }
}
