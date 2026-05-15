using Cyaim.RTSPClient.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.KeepAlive
{
    /// <summary>
    /// Manages RTSP keep-alive heartbeat.
    /// Sends OPTIONS or GET_PARAMETER periodically to prevent session timeout.
    /// </summary>
    internal sealed class KeepAliveManager : IDisposable
    {
        private readonly Func<CancellationToken, Task<RTSPResponse>> _sendKeepAlive;
        private Timer? _timer;
        private CancellationTokenSource? _cts;
        private int _intervalMs;

        /// <summary>
        /// Raised after each keep-alive attempt with the result.
        /// </summary>
        public event EventHandler<KeepAliveEventArgs>? KeepAliveResult;

        /// <summary>
        /// Creates a new keep-alive manager with the specified send callback.
        /// </summary>
        /// <param name="sendKeepAlive">
        /// Async function that sends a keep-alive request (e.g., OPTIONS or GET_PARAMETER).
        /// Receives a <see cref="CancellationToken"/> and returns the RTSP response.
        /// </param>
        public KeepAliveManager(Func<CancellationToken, Task<RTSPResponse>> sendKeepAlive)
        {
            _sendKeepAlive = sendKeepAlive ?? throw new ArgumentNullException(nameof(sendKeepAlive));
        }

        /// <summary>
        /// Start keep-alive heartbeat. Interval is server timeout / 2, minimum 30 seconds.
        /// </summary>
        /// <param name="serverTimeoutSeconds">The server session timeout in seconds.</param>
        public void Start(int serverTimeoutSeconds)
        {
            Stop();

            _intervalMs = Math.Max(30000, (serverTimeoutSeconds * 1000) / 2);
            _cts = new CancellationTokenSource();
            _timer = new Timer(async _ => await SendKeepAliveAsync(), null, _intervalMs, _intervalMs);
        }

        /// <summary>
        /// Sends a keep-alive request and raises the <see cref="KeepAliveResult"/> event.
        /// </summary>
        private async Task SendKeepAliveAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _sendKeepAlive(_cts!.Token);
                sw.Stop();
                KeepAliveResult?.Invoke(this, new KeepAliveEventArgs(true, (int)sw.ElapsedMilliseconds));
            }
            catch (Exception)
            {
                sw.Stop();
                KeepAliveResult?.Invoke(this, new KeepAliveEventArgs(false, (int)sw.ElapsedMilliseconds));
            }
        }

        /// <summary>
        /// Stop keep-alive heartbeat and release timer resources.
        /// </summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// Disposes the keep-alive manager and stops the heartbeat.
        /// </summary>
        public void Dispose() => Stop();
    }
}
