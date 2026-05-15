# RTSP Client Full Refactor Plan

## Architecture Overview

Based on Oracle consultation, this is a complete RTSP client refactor targeting netstandard2.0.

## Key Design Decisions

1. **Channel<T> for async streaming** (netstandard2.0 compatible via System.Threading.Channels)
2. **TaskCompletionSource for response matching** (replaces busy-wait polling)
3. **CAS-based state machine** (lock-free via Interlocked.CompareExchange)
4. **Interface-based transport abstraction** (TCP interleaved + UDP)
5. **Depacketizer pattern** for codec-specific RTP aggregation

## Directory Structure

```
Cyaim.RTSPClient/
├── Common/           # Constants, enums, utilities
├── Protocol/         # RTSP transport, request/response
├── Rtp/              # RTP packet parsing, depacketizers
├── Rtcp/             # RTCP sender/receiver reports
├── Media/            # SDP parsing, codec info
├── Session/          # Main session orchestrator
├── Auth/             # Digest/Basic authentication
├── KeepAlive/        # Heartbeat manager
├── Events/           # Event args
└── Exceptions/       # Custom exceptions
```

## Implementation Phases

### Phase P0 (Done)
- [x] Add System.Threading.Channels NuGet
- [x] Create directory structure
- [x] Create base enums (RtspConnectionState, TransportMode, StreamType, etc.)
- [x] Create event args (RtspEventArgs.cs)
- [x] Create exceptions (RtspExceptions.cs)

### Phase P1 - Core Types (In Progress)
- [ ] RtpPacket readonly struct
- [ ] RtpPacketParser (RFC 3550)
- [ ] RtspStateMachine
- [ ] RtspSessionConfig

### Phase P2 - Transport & Session
- [ ] IRtspTransport interface
- [ ] RtspTcpTransport implementation
- [ ] IRtspSession interface
- [ ] RtspSession orchestrator
- [ ] DigestAuthenticator

### Phase P3 - RTSP Methods
- [ ] PAUSE, GET_PARAMETER, SET_PARAMETER, ANNOUNCE, RECORD
- [ ] KeepAliveManager
- [ ] Channel<RtpPacket> integration

### Phase P4 - Media Depacketizers
- [ ] IRtpDepacketizer interface
- [ ] H264Depacketizer (RFC 6184)
- [ ] H265Depacketizer (RFC 7798)
- [ ] AudioDepacketizer

### Phase P5 - Advanced
- [ ] RtcpSession (SR/RR)
- [ ] RtspUdpTransport
- [ ] Auto-reconnect

### Phase P6 - Polish
- [ ] SdpParser refactor
- [ ] MediaDescription/CodecInfo
- [ ] README update

## Interface Contracts

### IRtspSession
- State, Uri, SDP, SessionId properties
- Events: StateChanged, DataReceived, Error, KeepAlive
- Methods: ConnectAsync, DisconnectAsync, OptionsAsync, DescribeAsync, SetupAsync, PlayAsync, PauseAsync, TeardownAsync, GetParameterAsync, SetParameterAsync
- GetRtpReader(trackId) -> ChannelReader<RtpPacket>

### IRtspTransport
- Mode, IsConnected properties
- SendRequestAsync, SendRtpAsync, StartReceiveLoopAsync, OpenAsync, CloseAsync

### IRtpDepacketizer
- Feed(RtpPacket) -> IEnumerable<MediaFrame>
- Reset()

## Thread Safety

| Concern | Mechanism |
|---------|-----------|
| State machine | Interlocked.CompareExchange |
| CSeq counter | Interlocked.Increment |
| Pending requests | ConcurrentDictionary<int, TaskCompletionSource> |
| RTP channels | Channel<T> (lock-free) |
