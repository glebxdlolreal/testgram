package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// tgClient behaves like tgcalls' GroupInstanceCustomImpl on the wire: ICE
// controlled, DTLS server, join params with ufrag/pwd/fingerprint/ssrc and no
// candidates, and it accepts the SFU's Jingle transport as its remote side.
type tgClient struct {
	t      *testing.T
	pc     *webrtc.PeerConnection
	track  *webrtc.TrackLocalStaticRTP
	ssrc   uint32
	params string

	video     *rawTrack
	videoSsrc [3]uint32 // SIM layers; the first is what receivers render
	videoRtx  [3]uint32

	mu        sync.Mutex
	received  map[uint32]int
	levels    map[uint32]bool
	gotAny    atomic.Bool
	keyframes atomic.Int64
}

func newTGClient(t *testing.T) *tgClient { return newTGClientWith(t, false) }

func newTGClientWith(t *testing.T, withVideo bool) *tgClient {
	t.Helper()
	me := &webrtc.MediaEngine{}
	if err := me.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1"},
		PayloadType:        opusPayloadType,
	}, webrtc.RTPCodecTypeAudio); err != nil {
		t.Fatal(err)
	}
	for _, uri := range []string{extAudioLevel, extAbsSendTime, extTransportCC} {
		if err := me.RegisterHeaderExtension(webrtc.RTPHeaderExtensionCapability{URI: uri}, webrtc.RTPCodecTypeAudio); err != nil {
			t.Fatal(err)
		}
	}
	for _, c := range videoCodecs {
		_ = me.RegisterCodec(webrtc.RTPCodecParameters{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: "video/" + c.name, ClockRate: 90000, SDPFmtpLine: c.fmtpLine, RTCPFeedback: videoRTCPFeedback}, PayloadType: webrtc.PayloadType(c.pt)}, webrtc.RTPCodecTypeVideo)
		_ = me.RegisterCodec(webrtc.RTPCodecParameters{RTPCodecCapability: webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeRTX, ClockRate: 90000, SDPFmtpLine: fmt.Sprintf("apt=%d", c.pt)}, PayloadType: webrtc.PayloadType(c.rtxPT)}, webrtc.RTPCodecTypeVideo)
	}
	ufrag := randomToken(8)
	pwd := randomToken(24)
	se := webrtc.SettingEngine{}
	se.SetICECredentials(ufrag, pwd)
	se.SetAnsweringDTLSRole(webrtc.DTLSRoleServer)
	se.SetIncludeLoopbackCandidate(true)
	se.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	api := webrtc.NewAPI(webrtc.WithMediaEngine(me), webrtc.WithSettingEngine(se))

	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	cert, err := webrtc.GenerateCertificate(key)
	if err != nil {
		t.Fatal(err)
	}
	pc, err := api.NewPeerConnection(webrtc.Configuration{Certificates: []webrtc.Certificate{*cert}})
	if err != nil {
		t.Fatal(err)
	}
	track, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2}, "audio", "tg")
	if err != nil {
		t.Fatal(err)
	}
	sender, err := pc.AddTrack(track)
	if err != nil {
		t.Fatal(err)
	}
	go drainRTCP(sender)
	ssrc := uint32(sender.GetParameters().Encodings[0].SSRC)

	c := &tgClient{t: t, pc: pc, track: track, ssrc: ssrc, received: map[uint32]int{}, levels: map[uint32]bool{}}
	groups := []map[string]any{}
	if withVideo {
		c.video = &rawTrack{}
		videoSender, err := pc.AddTrack(c.video)
		if err != nil {
			t.Fatal(err)
		}
		go c.countKeyframeRequests(videoSender)
		// The base layer uses the sender's own SSRC so pion hands this client the PLIs for it,
		// as libwebrtc does for every SSRC it declared.
		base := uint32(videoSender.GetParameters().Encodings[0].SSRC)
		sim := []int32{}
		for i := range c.videoSsrc {
			c.videoSsrc[i] = base + uint32(i*2)
			c.videoRtx[i] = base + uint32(i*2) + 1
			sim = append(sim, int32(c.videoSsrc[i]))
		}
		groups = append(groups, map[string]any{"semantics": "SIM", "sources": sim})
		for i := range c.videoSsrc {
			groups = append(groups, map[string]any{"semantics": "FID", "sources": []int32{int32(c.videoSsrc[i]), int32(c.videoRtx[i])}})
		}
	}

	fps, _ := cert.GetFingerprints()
	params, _ := json.Marshal(map[string]any{
		"ufrag":        ufrag,
		"pwd":          pwd,
		"fingerprints": []map[string]string{{"hash": fps[0].Algorithm, "fingerprint": strings.ToUpper(fps[0].Value), "setup": "passive"}},
		"ssrc":         int32(ssrc),
		"ssrc-groups":  groups,
	})
	c.params = string(params)
	pc.OnTrack(func(remote *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		go func() {
			for {
				pkt, _, err := remote.ReadRTP()
				if err != nil {
					return
				}
				c.gotAny.Store(true)
				c.mu.Lock()
				c.received[pkt.SSRC]++
				if pkt.GetExtension(1) != nil {
					c.levels[pkt.SSRC] = true
				}
				c.mu.Unlock()
			}
		}()
	})
	return c
}

// connect applies the SFU's transport the way tgcalls does: it is the remote
// side's ICE credentials, fingerprint and candidates; no SDP from the server.
func (c *tgClient) connect(tr *Transport) {
	c.t.Helper()
	var b strings.Builder
	b.WriteString("v=0\r\no=- 7 2 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\n")
	common := func(mid string) {
		b.WriteString("c=IN IP4 0.0.0.0\r\n")
		fmt.Fprintf(&b, "a=ice-ufrag:%s\r\na=ice-pwd:%s\r\n", tr.Ufrag, tr.Pwd)
		fmt.Fprintf(&b, "a=fingerprint:%s %s\r\na=setup:actpass\r\na=mid:%s\r\n", tr.Fingerprints[0].Hash, tr.Fingerprints[0].Fingerprint, mid)
		b.WriteString("a=sendrecv\r\na=rtcp-mux\r\n")
		for _, cand := range tr.Candidates {
			fmt.Fprintf(&b, "a=candidate:%s %s %s %s %s %s typ %s\r\n", cand.Foundation, cand.Component, cand.Protocol, cand.Priority, cand.IP, cand.Port, cand.Type)
		}
	}
	fmt.Fprintf(&b, "m=audio 9 UDP/TLS/RTP/SAVPF %d\r\n", opusPayloadType)
	common("0")
	fmt.Fprintf(&b, "a=extmap:1 %s\r\na=extmap:2 %s\r\na=extmap:3 %s\r\n", extAudioLevel, extAbsSendTime, extTransportCC)
	fmt.Fprintf(&b, "a=rtpmap:%d opus/48000/2\r\n", opusPayloadType)
	b.WriteString("m=video 9 UDP/TLS/RTP/SAVPF 100 101\r\n")
	common("1")
	b.WriteString("a=rtpmap:100 VP8/90000\r\na=rtcp-fb:100 nack pli\r\na=rtpmap:101 rtx/90000\r\na=fmtp:101 apt=100\r\n")
	if err := c.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: b.String()}); err != nil {
		c.t.Fatal(err)
	}
	answer, err := c.pc.CreateAnswer(nil)
	if err != nil {
		c.t.Fatal(err)
	}
	if err := c.pc.SetLocalDescription(answer); err != nil {
		c.t.Fatal(err)
	}
}

// film sends VP8-like packets on the given simulcast layer.
func (c *tgClient) film(stop <-chan struct{}, layer int) {
	var seq uint16
	ts := uint32(0)
	ticker := time.NewTicker(20 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-stop:
			return
		case <-ticker.C:
			seq++
			ts += 3000
			c.video.write(&rtp.Packet{Header: rtp.Header{Version: 2, PayloadType: 100, SequenceNumber: seq, Timestamp: ts, SSRC: c.videoSsrc[layer]}, Payload: []byte{0x10, 0x00, 0x9d, 0x01}})
		}
	}
}

func (c *tgClient) countKeyframeRequests(sender *webrtc.RTPSender) {
	buf := make([]byte, 1500)
	for {
		n, _, err := sender.Read(buf)
		if err != nil {
			return
		}
		packets, err := rtcp.Unmarshal(buf[:n])
		if err != nil {
			continue
		}
		for _, p := range packets {
			if pli, ok := p.(*rtcp.PictureLossIndication); ok && pli.MediaSSRC == c.videoSsrc[0] {
				c.keyframes.Add(1)
			}
		}
	}
}

// rawTrack sends packets with whatever SSRC they carry, like tgcalls' simulcast encoder.
type rawTrack struct {
	mu sync.Mutex
	w  webrtc.TrackLocalWriter
}

func (t *rawTrack) Bind(ctx webrtc.TrackLocalContext) (webrtc.RTPCodecParameters, error) {
	t.mu.Lock()
	t.w = ctx.WriteStream()
	t.mu.Unlock()
	return ctx.CodecParameters()[0], nil
}
func (t *rawTrack) Unbind(webrtc.TrackLocalContext) error { return nil }
func (t *rawTrack) ID() string                            { return "video" }
func (t *rawTrack) RID() string                           { return "" }
func (t *rawTrack) StreamID() string                      { return "tg" }
func (t *rawTrack) Kind() webrtc.RTPCodecType             { return webrtc.RTPCodecTypeVideo }
func (t *rawTrack) write(p *rtp.Packet) {
	t.mu.Lock()
	w := t.w
	t.mu.Unlock()
	if w != nil {
		_, _ = w.WriteRTP(&p.Header, p.Payload)
	}
}

// speak sends opus-like packets carrying an audio level, like a talking client.
func (c *tgClient) speak(stop <-chan struct{}) {
	var seq uint16
	ts := uint32(0)
	ticker := time.NewTicker(20 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-stop:
			return
		case <-ticker.C:
			seq++
			ts += 960
			pkt := &rtp.Packet{Header: rtp.Header{Version: 2, PayloadType: opusPayloadType, SequenceNumber: seq, Timestamp: ts}, Payload: []byte{0xf8, 0xff, 0xfe}}
			_ = pkt.Header.SetExtension(1, []byte{0x9e})
			_ = c.track.WriteRTP(pkt)
		}
	}
}

func (c *tgClient) count(ssrc uint32) int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.received[ssrc]
}

func (c *tgClient) hadLevel(ssrc uint32) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.levels[ssrc]
}

func randomToken(n int) string {
	const letters = "abcdefghijklmnopqrstuvwxyz0123456789"
	b := make([]byte, n)
	_, _ = rand.Read(b)
	for i := range b {
		b[i] = letters[int(b[i])%len(letters)]
	}
	return string(b)
}
