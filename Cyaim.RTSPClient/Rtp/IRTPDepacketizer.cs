using System.Collections.Generic;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// RTP解包器接口
    /// 将RTP包聚合为完整的媒体帧
    /// </summary>
    public interface IRTPDepacketizer
    {
        /// <summary>
        /// 输入RTP包，输出0个或多个完整媒体帧
        /// (FU-A分片在收到最后一个分片前输出0帧; STAP-A可能输出多帧)
        /// </summary>
        IEnumerable<MediaFrame> Feed(RTPPacket packet);

        /// <summary>
        /// 重置状态 (seek或重连时调用)
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// 解包后的媒体帧
    /// </summary>
    public readonly struct MediaFrame
    {
        public MediaFrame(byte[] data, uint timestamp, bool isKeyFrame, StreamType streamType, int trackId)
        {
            Data = data;
            Timestamp = timestamp;
            IsKeyFrame = isKeyFrame;
            StreamType = streamType;
            TrackId = trackId;
        }

        /// <summary>
        /// 帧数据 (NAL unit或音频采样)
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// RTP时间戳
        /// </summary>
        public uint Timestamp { get; }

        /// <summary>
        /// 是否为关键帧 (IDR for H.264/H.265)
        /// </summary>
        public bool IsKeyFrame { get; }

        /// <summary>
        /// 流类型
        /// </summary>
        public StreamType StreamType { get; }

        /// <summary>
        /// Track ID
        /// </summary>
        public int TrackId { get; }
    }
}
