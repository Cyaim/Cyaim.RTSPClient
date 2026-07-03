using System;
using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// RTP 乱序重排缓冲（UDP 传输用）。
    /// 按序列号缓存乱序到达的包，按序输出；窗口满或等待超时时跳过缺口继续。
    /// TCP interleaved 天然有序，无需使用。
    /// </summary>
    public sealed class RtpReorderBuffer
    {
        private readonly SortedDictionary<ushort, (RTPPacket packet, long arrivalTicks)> _buffer = new(new SeqComparer());
        private readonly int _maxWindow;
        private readonly long _maxWaitTicks;
        private ushort _expectedSeq;
        private bool _initialized;

        /// <summary>因缺口跳过（视为丢失）的包计数</summary>
        public long PacketsLost { get; private set; }

        /// <summary>乱序到达但被成功重排的包计数</summary>
        public long PacketsReordered { get; private set; }

        /// <param name="maxWindow">最大缓存包数，超过时强制推进（默认 64）</param>
        /// <param name="maxWaitMs">缺口最长等待毫秒数（默认 100ms）</param>
        public RtpReorderBuffer(int maxWindow = 64, int maxWaitMs = 100)
        {
            _maxWindow = Math.Max(4, maxWindow);
            _maxWaitTicks = maxWaitMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        }

        /// <summary>
        /// 输入一个包，输出当前可按序交付的所有包
        /// </summary>
        public IEnumerable<RTPPacket> Feed(RTPPacket packet)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (!_initialized)
            {
                _initialized = true;
                _expectedSeq = (ushort)(packet.SequenceNumber + 1);
                yield return packet;
                yield break;
            }

            ushort seq = packet.SequenceNumber;
            ushort distance = (ushort)(seq - _expectedSeq);

            if (distance == 0)
            {
                // 正好按序
                _expectedSeq++;
                yield return packet;
            }
            else if (distance < 0x8000)
            {
                // 未来的包：缓存等待缺口填补
                _buffer[seq] = (packet, now);
                if (_buffer.Count > 0)
                    PacketsReordered++;
            }
            // else: 迟到的旧包/重复包，直接丢弃

            // 尝试连续交付
            foreach (var p in Drain(now))
                yield return p;
        }

        /// <summary>
        /// 从缓冲中交付可用的包：连续的直接交付；缺口超时或窗口过大时跳过
        /// </summary>
        private IEnumerable<RTPPacket> Drain(long now)
        {
            while (_buffer.Count > 0)
            {
                bool advanced = false;

                // 交付所有已连续的
                while (_buffer.TryGetValue(_expectedSeq, out var entry))
                {
                    _buffer.Remove(_expectedSeq);
                    _expectedSeq++;
                    advanced = true;
                    yield return entry.packet;
                }

                if (_buffer.Count == 0)
                    yield break;

                // 判断是否放弃等待缺口
                var oldest = OldestEntry();
                bool timeout = now - oldest.arrivalTicks > _maxWaitTicks;
                bool overflow = _buffer.Count > _maxWindow;

                if (!timeout && !overflow)
                {
                    if (!advanced)
                        yield break;
                    continue;
                }

                // 跳到缓冲中最早的序列号，缺口计为丢包
                ushort nextSeq = oldest.seq;
                PacketsLost += (ushort)(nextSeq - _expectedSeq);
                _expectedSeq = nextSeq;
            }
        }

        private (ushort seq, long arrivalTicks) OldestEntry()
        {
            foreach (var kv in _buffer)
            {
                return (kv.Key, kv.Value.arrivalTicks);
            }
            return default;
        }

        /// <summary>
        /// 重置状态（重连/seek 后调用）
        /// </summary>
        public void Reset()
        {
            _buffer.Clear();
            _initialized = false;
        }

        /// <summary>
        /// 序列号环形比较器（RFC 3550：距离小于 2^15 视为"之后"）
        /// </summary>
        private sealed class SeqComparer : IComparer<ushort>
        {
            public int Compare(ushort x, ushort y)
            {
                if (x == y) return 0;
                return (ushort)(y - x) < 0x8000 ? -1 : 1;
            }
        }
    }
}
