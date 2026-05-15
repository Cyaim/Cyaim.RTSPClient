using System.Threading;

namespace Cyaim.RTSPClient.Session
{
    /// <summary>
    /// RTSP连接状态机，基于CAS（Compare-And-Swap）实现线程安全的状态转换。
    /// 不使用锁，通过 <see cref="Interlocked"/> 和 <see cref="Volatile"/> 保证原子性和可见性。
    /// </summary>
    internal sealed class RTSPStateMachine
    {
        /// <summary>
        /// 当前状态的内部存储（以int表示 <see cref="RTSPConnectionState"/> 枚举值）
        /// </summary>
        private int _state = (int)RTSPConnectionState.Disconnected;

        /// <summary>
        /// 获取当前连接状态
        /// </summary>
        public RTSPConnectionState Current => (RTSPConnectionState)_state;

        /// <summary>
        /// 尝试原子地从一个状态转换到另一个状态。
        /// 仅当当前状态等于 <paramref name="from"/> 时才会转换为 <paramref name="to"/>。
        /// </summary>
        /// <param name="from">期望的当前状态</param>
        /// <param name="to">目标状态</param>
        /// <returns>转换成功返回 true；如果当前状态不是 <paramref name="from"/>，则返回 false</returns>
        public bool TryTransition(RTSPConnectionState from, RTSPConnectionState to)
        {
            int fromInt = (int)from;
            int toInt = (int)to;
            return Interlocked.CompareExchange(ref _state, toInt, fromInt) == fromInt;
        }

        /// <summary>
        /// 强制设置状态，用于错误处理和释放资源路径。
        /// 使用 <see cref="Volatile.Write"/> 确保写入对其他线程立即可见。
        /// </summary>
        /// <param name="state">要设置的状态</param>
        public void ForceSet(RTSPConnectionState state)
        {
            Volatile.Write(ref _state, (int)state);
        }
    }
}
