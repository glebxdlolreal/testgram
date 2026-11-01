package main

import (
	"errors"
	"fmt"
	"log"
	"net"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// SFU forwards every participant's audio to everybody else in the same call,
// keeping the sender's SSRC: tgcalls opens an incoming audio channel for an
// unknown SSRC once it has looked the SSRC up with phone.getGroupParticipants.
type SFU struct {
	api *webrtc.API

	mu    sync.Mutex
	calls map[int64]*Call
}

type Call struct {
	ID int64

	mu           sync.RWMutex
	participants map[string]*Participant
}

type Participant struct {
	Key    string
	Source uint32
	call   *Call
	pc     *webrtc.PeerConnection
	out    *forwardTrack

	connected atomic.Bool
	received  atomic.Uint64
	forwarded atomic.Uint64
	lastRTP   atomic.Int64

	mu      sync.Mutex
	sources map[uint32]struct{}
	closed  bool
}

type Config struct {
	PublicIPs []string
	UDP       net.PacketConn
	// IncludeLoopback lets tests reach the SFU over 127.0.0.1.
	IncludeLoopback bool
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
	// Registration order is id order: 1 audio level, 2 abs-send-time, 3 transport-cc.
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

	return &SFU{
		api:   webrtc.NewAPI(webrtc.WithMediaEngine(me), webrtc.WithSettingEngine(se)),
		calls: map[int64]*Call{},
	}, nil
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
// transport the client must connect to.
func (s *SFU) Join(callID int64, key string, params *JoinParams) (*Transport, error) {
	c := s.call(callID, true)

	pc, err := s.api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		return nil, err
	}
	p := &Participant{Key: key, Source: params.Ssrc32(), call: c, pc: pc, sources: map[uint32]struct{}{params.Ssrc32(): {}}}
	p.out = &forwardTrack{id: fmt.Sprintf("sfu-%d-%s", callID, key)}

	sender, err := pc.AddTrack(p.out)
	if err != nil {
		pc.Close()
		return nil, err
	}
	go drainRTCP(sender)

	pc.OnTrack(func(track *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		go p.receive(track)
	})
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		log.Printf("call %d participant %s: %s", callID, key, state)
		switch state {
		case webrtc.PeerConnectionStateConnected:
			p.connected.Store(true)
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

	transport, mid, err := TransportFromOffer(pc.LocalDescription().SDP)
	if err != nil {
		pc.Close()
		return nil, err
	}
	if err := pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: params.RemoteAnswer(mid)}); err != nil {
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
	return transport, nil
}

// receive forwards one incoming RTP stream to every other participant.
func (p *Participant) receive(track *webrtc.TrackRemote) {
	ssrc := uint32(track.SSRC())
	p.mu.Lock()
	p.sources[ssrc] = struct{}{}
	p.mu.Unlock()

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
		p.lastRTP.Store(time.Now().UnixMilli())

		p.call.mu.RLock()
		for _, other := range p.call.participants {
			if other == p {
				continue
			}
			if other.out.write(&pkt.Header, pkt.Payload) {
				other.forwarded.Add(1)
			}
		}
		p.call.mu.RUnlock()
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
	Connected bool   `json:"connected"`
	Received  uint64 `json:"received_packets"`
	Forwarded uint64 `json:"forwarded_packets"`
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
		out = append(out, ParticipantStats{p.Key, int32(p.Source), p.connected.Load(), p.received.Load(), p.forwarded.Load()})
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

// forwardTrack is the one outgoing audio track per participant. Unlike pion's
// static tracks it keeps each packet's own SSRC, so every speaker reaches the
// listener as a separate stream, as from Telegram's SFU.
type forwardTrack struct {
	id string

	mu     sync.RWMutex
	writer webrtc.TrackLocalWriter
	pt     webrtc.PayloadType
}

func (t *forwardTrack) Bind(ctx webrtc.TrackLocalContext) (webrtc.RTPCodecParameters, error) {
	for _, codec := range ctx.CodecParameters() {
		if codec.MimeType == webrtc.MimeTypeOpus {
			t.mu.Lock()
			t.writer = ctx.WriteStream()
			t.pt = codec.PayloadType
			t.mu.Unlock()
			return codec, nil
		}
	}
	return webrtc.RTPCodecParameters{}, webrtc.ErrUnsupportedCodec
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
func (t *forwardTrack) Kind() webrtc.RTPCodecType { return webrtc.RTPCodecTypeAudio }

func (t *forwardTrack) write(h *rtp.Header, payload []byte) bool {
	t.mu.RLock()
	w := t.writer
	pt := t.pt
	t.mu.RUnlock()
	if w == nil {
		return false
	}
	header := *h
	header.PayloadType = uint8(pt)
	_, err := w.WriteRTP(&header, payload)
	return err == nil
}
