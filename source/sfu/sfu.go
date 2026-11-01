package main

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"net"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// SFU forwards every participant's audio and base-layer video to everybody else in the same
// call, keeping the sender's SSRC: tgcalls opens an incoming audio channel for an unknown opus
// SSRC once it has looked it up with phone.getGroupParticipants, and renders a participant's video
// from the first SSRC of the SIM group the participant advertised.
type SFU struct {
	api              *webrtc.API
	keyframeInterval time.Duration

	mu    sync.Mutex
	calls map[int64]*Call
}

type Call struct {
	ID int64

	mu           sync.RWMutex
	participants map[string]*Participant
}

type Participant struct {
	Key      string
	Source   uint32
	Endpoint string
	call     *Call
	pc       *webrtc.PeerConnection
	audioOut *forwardTrack
	videoOut *forwardTrack

	videoMain uint32
	videoRtx  uint32

	connected atomic.Bool
	received  atomic.Uint64
	forwarded atomic.Uint64
	keyframes atomic.Uint64

	mu      sync.Mutex
	sources map[uint32]struct{}
	closed  bool
}

// JoinResult is what the messenger turns into updateGroupCallConnection.params.
type JoinResult struct {
	Transport *Transport     `json:"transport"`
	Video     map[string]any `json:"video"`
	Endpoint  string         `json:"endpoint"`
}

type Config struct {
	PublicIPs []string
	UDP       net.PacketConn
	// IncludeLoopback lets tests reach the SFU over 127.0.0.1.
	IncludeLoopback bool
	// KeyframeInterval is how often a camera somebody watches is asked for a keyframe
	// (default 3s). A viewer's own picture-loss requests name the camera's SSRC, which pion
	// never hands to the SFU, so the SFU asks on a schedule instead of relaying them.
	KeyframeInterval time.Duration
}

var videoRTCPFeedback = []webrtc.RTCPFeedback{
	{Type: "goog-remb"}, {Type: "transport-cc"}, {Type: "ccm", Parameter: "fir"}, {Type: "nack"}, {Type: "nack", Parameter: "pli"},
}

func NewSFU(cfg Config) (*SFU, error) {
	me := &webrtc.MediaEngine{}
	if err := me.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType:     webrtc.MimeTypeOpus,
			ClockRate:    48000,
			Channels:     2,
			SDPFmtpLine:  "minptime=10;useinbandfec=1",
			RTCPFeedback: []webrtc.RTCPFeedback{{Type: "transport-cc"}},
		},
		PayloadType: opusPayloadType,
	}, webrtc.RTPCodecTypeAudio); err != nil {
		return nil, err
	}
	for _, c := range videoCodecs {
		if err := me.RegisterCodec(webrtc.RTPCodecParameters{
			RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: "video/" + c.name, ClockRate: 90000, SDPFmtpLine: c.fmtpLine, RTCPFeedback: videoRTCPFeedback},
			PayloadType:        webrtc.PayloadType(c.pt),
		}, webrtc.RTPCodecTypeVideo); err != nil {
			return nil, err
		}
		if err := me.RegisterCodec(webrtc.RTPCodecParameters{
			RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeRTX, ClockRate: 90000, SDPFmtpLine: fmt.Sprintf("apt=%d", c.pt)},
			PayloadType:        webrtc.PayloadType(c.rtxPT),
		}, webrtc.RTPCodecTypeVideo); err != nil {
			return nil, err
		}
	}
	// Registration order is id order: 1 audio level, 2 abs-send-time, 3 transport-cc. Video
	// orientation (13) is not registered: forwarded packets keep their extensions untouched.
	for _, uri := range []string{extAudioLevel, extAbsSendTime, extTransportCC} {
		if err := me.RegisterHeaderExtension(webrtc.RTPHeaderExtensionCapability{URI: uri}, webrtc.RTPCodecTypeAudio); err != nil {
			return nil, err
		}
	}

	se := webrtc.SettingEngine{}
	se.SetICEUDPMux(webrtc.NewICEUDPMux(nil, cfg.UDP))
	se.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	if len(cfg.PublicIPs) > 0 {
		se.SetNAT1To1IPs(cfg.PublicIPs, webrtc.ICECandidateTypeHost)
	}
	se.SetIncludeLoopbackCandidate(cfg.IncludeLoopback)
	// Telegram clients never send their candidates; the SFU learns them from
	// their connectivity checks (peer-reflexive), so keep waiting a little longer.
	se.SetICETimeouts(10*time.Second, 30*time.Second, 2*time.Second)

	interval := cfg.KeyframeInterval
	if interval <= 0 {
		interval = 3 * time.Second
	}
	s := &SFU{
		api:              webrtc.NewAPI(webrtc.WithMediaEngine(me), webrtc.WithSettingEngine(se)),
		keyframeInterval: interval,
		calls:            map[int64]*Call{},
	}
	go s.keyframeLoop()
	return s, nil
}

// keyframeLoop asks every camera that has a viewer for a keyframe now and then, so a viewer that
// lost a packet, or joined between requests, recovers within one interval.
func (s *SFU) keyframeLoop() {
	ticker := time.NewTicker(s.keyframeInterval)
	defer ticker.Stop()
	for range ticker.C {
		s.mu.Lock()
		calls := make([]*Call, 0, len(s.calls))
		for _, c := range s.calls {
			calls = append(calls, c)
		}
		s.mu.Unlock()
		for _, c := range calls {
			c.mu.RLock()
			viewers := 0
			for _, p := range c.participants {
				if p.connected.Load() {
					viewers++
				}
			}
			if viewers > 1 {
				for _, p := range c.participants {
					if p.connected.Load() && p.videoMain != 0 {
						p.requestKeyframe()
					}
				}
			}
			c.mu.RUnlock()
		}
	}
}

func (s *SFU) call(id int64, create bool) *Call {
	s.mu.Lock()
	defer s.mu.Unlock()
	c := s.calls[id]
	if c == nil && create {
		c = &Call{ID: id, participants: map[string]*Participant{}}
		s.calls[id] = c
	}
	return c
}

// Join allocates (or replaces) the participant's connection and returns the
// transport the client must connect to, with the video section of the answer.
func (s *SFU) Join(callID int64, key string, params *JoinParams) (*JoinResult, error) {
	c := s.call(callID, true)

	pc, err := s.api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		return nil, err
	}
	p := &Participant{
		Key:      key,
		Source:   params.Ssrc32(),
		Endpoint: newEndpointID(),
		call:     c,
		pc:       pc,
		sources:  map[uint32]struct{}{params.Ssrc32(): {}},
	}
	if main, rtx, ok := params.VideoSsrcs(); ok {
		p.videoMain, p.videoRtx = main, rtx
		p.sources[main] = struct{}{}
	}
	p.audioOut = &forwardTrack{id: fmt.Sprintf("audio-%d-%s", callID, key), kind: webrtc.RTPCodecTypeAudio}
	p.videoOut = &forwardTrack{id: fmt.Sprintf("video-%d-%s", callID, key), kind: webrtc.RTPCodecTypeVideo}

	audioSender, err := pc.AddTrack(p.audioOut)
	if err != nil {
		pc.Close()
		return nil, err
	}
	go drainRTCP(audioSender)
	videoSender, err := pc.AddTrack(p.videoOut)
	if err != nil {
		pc.Close()
		return nil, err
	}
	go drainRTCP(videoSender)

	pc.OnTrack(func(track *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		go p.receive(track)
	})
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		log.Printf("call %d participant %s: %s", callID, key, state)
		switch state {
		case webrtc.PeerConnectionStateConnected:
			p.connected.Store(true)
			// A newcomer needs a keyframe from every camera before it can show anything.
			c.mu.RLock()
			for _, other := range c.participants {
				if other != p && other.videoMain != 0 {
					other.requestKeyframe()
				}
			}
			c.mu.RUnlock()
		case webrtc.PeerConnectionStateFailed:
			p.connected.Store(false)
			// Forget a connection that failed: the client's next phone.checkGroupCall then misses
			// its source and tgcalls rejoins with a fresh connection.
			s.dropIfCurrent(callID, p)
		case webrtc.PeerConnectionStateClosed:
			p.connected.Store(false)
		}
	})

	offer, err := pc.CreateOffer(nil)
	if err != nil {
		pc.Close()
		return nil, err
	}
	gathered := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(offer); err != nil {
		pc.Close()
		return nil, err
	}
	select {
	case <-gathered:
	case <-time.After(5 * time.Second):
		pc.Close()
		return nil, errors.New("ICE gathering timed out")
	}

	transport, mids, err := TransportFromOffer(pc.LocalDescription().SDP)
	if err != nil {
		pc.Close()
		return nil, err
	}
	if len(mids) != 2 {
		pc.Close()
		return nil, fmt.Errorf("offer has %d media sections, want audio and video", len(mids))
	}
	if err := pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: params.RemoteAnswer(mids[0], mids[1])}); err != nil {
		pc.Close()
		return nil, fmt.Errorf("client params do not form a usable answer: %w", err)
	}

	c.mu.Lock()
	previous := c.participants[key]
	c.participants[key] = p
	c.mu.Unlock()
	if previous != nil {
		previous.close()
	}
	return &JoinResult{Transport: transport, Video: VideoDescription(p.Endpoint, probingSsrc()), Endpoint: p.Endpoint}, nil
}

// receive forwards one incoming RTP stream to every other participant.
func (p *Participant) receive(track *webrtc.TrackRemote) {
	ssrc := uint32(track.SSRC())
	p.mu.Lock()
	p.sources[ssrc] = struct{}{}
	p.mu.Unlock()
	video := track.Kind() == webrtc.RTPCodecTypeVideo
	if video {
		p.requestKeyframe()
	}

	buf := make([]byte, 1500)
	var pkt rtp.Packet
	for {
		n, _, err := track.Read(buf)
		if err != nil {
			return
		}
		if err := pkt.Unmarshal(buf[:n]); err != nil {
			continue
		}
		p.received.Add(1)

		p.call.mu.RLock()
		for _, other := range p.call.participants {
			if other == p {
				continue
			}
			out := other.audioOut
			if video {
				out = other.videoOut
			}
			if out.write(&pkt.Header, pkt.Payload) {
				other.forwarded.Add(1)
			}
		}
		p.call.mu.RUnlock()
	}
}

// requestKeyframe asks the participant's encoder for a keyframe of its base video layer.
func (p *Participant) requestKeyframe() {
	if p.videoMain == 0 {
		return
	}
	if err := p.pc.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: p.videoMain}}); err == nil {
		p.keyframes.Add(1)
	}
}

func (p *Participant) close() {
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return
	}
	p.closed = true
	p.mu.Unlock()
	_ = p.pc.Close()
}

func (s *SFU) Leave(callID int64, key string) bool {
	c := s.call(callID, false)
	if c == nil {
		return false
	}
	c.mu.Lock()
	p := c.participants[key]
	delete(c.participants, key)
	empty := len(c.participants) == 0
	c.mu.Unlock()
	if p != nil {
		p.close()
	}
	if empty {
		s.mu.Lock()
		if cur := s.calls[callID]; cur == c {
			c.mu.RLock()
			if len(c.participants) == 0 {
				delete(s.calls, callID)
			}
			c.mu.RUnlock()
		}
		s.mu.Unlock()
	}
	return p != nil
}

func (s *SFU) dropIfCurrent(callID int64, p *Participant) {
	c := s.call(callID, false)
	if c == nil {
		return
	}
	c.mu.Lock()
	current := c.participants[p.Key] == p
	c.mu.Unlock()
	if current {
		log.Printf("call %d participant %s: connection failed, dropping it", callID, p.Key)
		s.Leave(callID, p.Key)
	}
}

func (s *SFU) End(callID int64) {
	s.mu.Lock()
	c := s.calls[callID]
	delete(s.calls, callID)
	s.mu.Unlock()
	if c == nil {
		return
	}
	c.mu.Lock()
	ps := c.participants
	c.participants = map[string]*Participant{}
	c.mu.Unlock()
	for _, p := range ps {
		p.close()
	}
}

// Sources lists the SSRCs the call's participants hold, connected or still
// connecting: what phone.checkGroupCall answers with. Telegram's SFU counts a
// source as soon as it is allocated, and tgcalls rejoins when its own goes missing.
func (s *SFU) Sources(callID int64) []int32 {
	c := s.call(callID, false)
	if c == nil {
		return []int32{}
	}
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := []int32{}
	for _, p := range c.participants {
		p.mu.Lock()
		for ssrc := range p.sources {
			out = append(out, int32(ssrc))
		}
		p.mu.Unlock()
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

type ParticipantStats struct {
	Key       string `json:"key"`
	Source    int32  `json:"source"`
	Endpoint  string `json:"endpoint"`
	Connected bool   `json:"connected"`
	Received  uint64 `json:"received_packets"`
	Forwarded uint64 `json:"forwarded_packets"`
	Keyframes uint64 `json:"keyframe_requests"`
}

func (s *SFU) Stats(callID int64) []ParticipantStats {
	c := s.call(callID, false)
	if c == nil {
		return []ParticipantStats{}
	}
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := []ParticipantStats{}
	for _, p := range c.participants {
		out = append(out, ParticipantStats{p.Key, int32(p.Source), p.Endpoint, p.connected.Load(), p.received.Load(), p.forwarded.Load(), p.keyframes.Load()})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Key < out[j].Key })
	return out
}

func drainRTCP(sender *webrtc.RTPSender) {
	buf := make([]byte, 1500)
	for {
		if _, _, err := sender.Read(buf); err != nil {
			return
		}
	}
}

// newEndpointID mints a participant's video endpoint id, shaped like Telegram's (24 hex digits).
func newEndpointID() string {
	b := make([]byte, 12)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// probingSsrc is the server_sources entry: an SSRC no participant sends on.
func probingSsrc() int32 {
	var b [4]byte
	_, _ = rand.Read(b[:])
	return int32(binary.BigEndian.Uint32(b[:]) | 0x80000000)
}

// forwardTrack is the one outgoing track per participant and kind. Unlike pion's
// static tracks it keeps each packet's own SSRC, so every speaker and camera reaches
// the listener as a separate stream, as from Telegram's SFU.
type forwardTrack struct {
	id   string
	kind webrtc.RTPCodecType

	mu     sync.RWMutex
	writer webrtc.TrackLocalWriter
}

func (t *forwardTrack) Bind(ctx webrtc.TrackLocalContext) (webrtc.RTPCodecParameters, error) {
	codecs := ctx.CodecParameters()
	if len(codecs) == 0 {
		return webrtc.RTPCodecParameters{}, webrtc.ErrUnsupportedCodec
	}
	t.mu.Lock()
	t.writer = ctx.WriteStream()
	t.mu.Unlock()
	return codecs[0], nil
}

func (t *forwardTrack) Unbind(webrtc.TrackLocalContext) error {
	t.mu.Lock()
	t.writer = nil
	t.mu.Unlock()
	return nil
}

func (t *forwardTrack) ID() string                { return t.id }
func (t *forwardTrack) RID() string               { return "" }
func (t *forwardTrack) StreamID() string          { return t.id }
func (t *forwardTrack) Kind() webrtc.RTPCodecType { return t.kind }

// write forwards a packet as it came, payload type included: every client uses the
// SFU's payload type table, so the numbers mean the same thing on both legs.
func (t *forwardTrack) write(h *rtp.Header, payload []byte) bool {
	t.mu.RLock()
	w := t.writer
	t.mu.RUnlock()
	if w == nil {
		return false
	}
	header := *h
	_, err := w.WriteRTP(&header, payload)
	return err == nil
}
