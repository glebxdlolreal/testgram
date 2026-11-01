package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/pion/sdp/v3"
)

// JoinParams is what a Telegram client puts into phone.joinGroupCall params
// (tgcalls GroupJoinInternalPayload::serialize): its ICE credentials, DTLS
// fingerprint and the SSRC it sends audio with.
type JoinParams struct {
	Ufrag        string        `json:"ufrag"`
	Pwd          string        `json:"pwd"`
	Fingerprints []Fingerprint `json:"fingerprints"`
	Ssrc         int64         `json:"ssrc"`
	SsrcGroups   []SsrcGroup   `json:"ssrc-groups"`
}

type SsrcGroup struct {
	Semantics string  `json:"semantics"`
	Sources   []int64 `json:"sources"`
}

type Fingerprint struct {
	Hash        string `json:"hash"`
	Fingerprint string `json:"fingerprint"`
	Setup       string `json:"setup"`
}

// Candidate and Transport mirror the Jingle-style transport Telegram's SFU
// returns in updateGroupCallConnection.params (every candidate field is a string).
type Candidate struct {
	Generation string `json:"generation"`
	Component  string `json:"component"`
	Protocol   string `json:"protocol"`
	Port       string `json:"port"`
	IP         string `json:"ip"`
	Foundation string `json:"foundation"`
	ID         string `json:"id"`
	Priority   string `json:"priority"`
	Type       string `json:"type"`
	Network    string `json:"network"`
}

type Transport struct {
	Candidates   []Candidate   `json:"candidates"`
	Xmlns        string        `json:"xmlns"`
	Ufrag        string        `json:"ufrag"`
	RtcpMux      bool          `json:"rtcp-mux"`
	Pwd          string        `json:"pwd"`
	Fingerprints []Fingerprint `json:"fingerprints"`
}

// Header extension ids are fixed: tgcalls and Telegram's SFU both assume them.
const (
	extAudioLevel   = "urn:ietf:params:rtp-hdrext:ssrc-audio-level"
	extAbsSendTime  = "http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time"
	extTransportCC  = "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01"
	opusPayloadType = 111
)

func ParseJoinParams(raw string) (*JoinParams, error) {
	var p JoinParams
	if err := json.Unmarshal([]byte(raw), &p); err != nil {
		return nil, fmt.Errorf("params are not JSON: %w", err)
	}
	if p.Ufrag == "" || p.Pwd == "" {
		return nil, errors.New("params carry no ICE ufrag/pwd")
	}
	if len(p.Fingerprints) == 0 || p.Fingerprints[0].Fingerprint == "" {
		return nil, errors.New("params carry no DTLS fingerprint")
	}
	if p.Ssrc == 0 {
		return nil, errors.New("params carry no audio ssrc")
	}
	return &p, nil
}

// Ssrc32 is the audio SSRC as the unsigned value on the wire (tgcalls sends it as a signed int32).
func (p *JoinParams) Ssrc32() uint32 { return uint32(int32(p.Ssrc)) }

// videoCodec is one entry of the video section Telegram's SFU answers with (captured 2026-11-01).
type videoCodec struct {
	pt       int
	name     string
	rtxPT    int
	fmtpLine string
}

var videoCodecs = []videoCodec{
	{pt: 100, name: "VP8", rtxPT: 101, fmtpLine: "x-google-start-bitrate=800"},
	{pt: 102, name: "VP9", rtxPT: 103},
	{pt: 104, name: "H264", rtxPT: 105},
	{pt: 106, name: "AV1", rtxPT: 107},
}

var videoFeedback = []string{"goog-remb", "transport-cc", "ccm fir", "nack", "nack pli"}

// VideoLayers lists the sender's video encodings in SIM order (lowest first: the first SSRC
// is what receivers render as the participant's video), each with its FID (RTX) partner. A
// stream without a SIM group (a screen share) is its single FID pair.
func (p *JoinParams) VideoLayers() []videoLayer {
	rtxOf := map[uint32]uint32{}
	var fids [][2]uint32
	for _, g := range p.SsrcGroups {
		if g.Semantics == "FID" && len(g.Sources) >= 2 {
			main, rtx := uint32(int32(g.Sources[0])), uint32(int32(g.Sources[1]))
			rtxOf[main] = rtx
			fids = append(fids, [2]uint32{main, rtx})
		}
	}
	for _, g := range p.SsrcGroups {
		if g.Semantics == "SIM" && len(g.Sources) > 0 {
			layers := make([]videoLayer, 0, len(g.Sources))
			for _, src := range g.Sources {
				ssrc := uint32(int32(src))
				layers = append(layers, videoLayer{ssrc: ssrc, rtx: rtxOf[ssrc]})
			}
			return layers
		}
	}
	if len(fids) > 0 {
		return []videoLayer{{ssrc: fids[0][0], rtx: fids[0][1]}}
	}
	return nil
}

// VideoSsrcs is the base layer: its SSRC and RTX partner.
func (p *JoinParams) VideoSsrcs() (main uint32, rtx uint32, ok bool) {
	layers := p.VideoLayers()
	if len(layers) == 0 {
		return 0, 0, false
	}
	return layers[0].ssrc, layers[0].rtx, true
}

// RemoteAnswer turns the client's join params into the SDP answer the SFU's
// offer needs. setup:passive makes the SFU the DTLS client, which is what the
// client expects: tgcalls' GroupNetworkManager always runs DTLS as the server.
func (p *JoinParams) RemoteAnswer(audioMid, videoMid, dataMid string) string {
	fp := p.Fingerprints[0]
	hash := fp.Hash
	if hash == "" {
		hash = "sha-256"
	}
	ssrc := p.Ssrc32()
	var b strings.Builder
	b.WriteString("v=0\r\no=- 1 2 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n")
	fmt.Fprintf(&b, "a=group:BUNDLE %s %s %s\r\n", audioMid, videoMid, dataMid)
	transport := func() {
		b.WriteString("c=IN IP4 0.0.0.0\r\na=rtcp:9 IN IP4 0.0.0.0\r\n")
		fmt.Fprintf(&b, "a=ice-ufrag:%s\r\na=ice-pwd:%s\r\n", p.Ufrag, p.Pwd)
		fmt.Fprintf(&b, "a=fingerprint:%s %s\r\n", hash, fp.Fingerprint)
		b.WriteString("a=setup:passive\r\n")
	}

	fmt.Fprintf(&b, "m=audio 9 UDP/TLS/RTP/SAVPF %d\r\n", opusPayloadType)
	transport()
	fmt.Fprintf(&b, "a=mid:%s\r\n", audioMid)
	fmt.Fprintf(&b, "a=extmap:1 %s\r\na=extmap:2 %s\r\na=extmap:3 %s\r\n", extAudioLevel, extAbsSendTime, extTransportCC)
	b.WriteString("a=sendrecv\r\na=rtcp-mux\r\n")
	fmt.Fprintf(&b, "a=rtpmap:%d opus/48000/2\r\na=fmtp:%d minptime=10;useinbandfec=1\r\n", opusPayloadType, opusPayloadType)
	fmt.Fprintf(&b, "a=rtcp-fb:%d transport-cc\r\n", opusPayloadType)
	fmt.Fprintf(&b, "a=ssrc:%d cname:%d\r\n", ssrc, ssrc)

	pts := []string{}
	for _, c := range videoCodecs {
		pts = append(pts, strconv.Itoa(c.pt), strconv.Itoa(c.rtxPT))
	}
	fmt.Fprintf(&b, "m=video 9 UDP/TLS/RTP/SAVPF %s\r\n", strings.Join(pts, " "))
	transport()
	fmt.Fprintf(&b, "a=mid:%s\r\n", videoMid)
	fmt.Fprintf(&b, "a=extmap:2 %s\r\na=extmap:3 %s\r\n", extAbsSendTime, extTransportCC)
	b.WriteString("a=sendrecv\r\na=rtcp-mux\r\n")
	for _, c := range videoCodecs {
		fmt.Fprintf(&b, "a=rtpmap:%d %s/90000\r\n", c.pt, c.name)
		if c.fmtpLine != "" {
			fmt.Fprintf(&b, "a=fmtp:%d %s\r\n", c.pt, c.fmtpLine)
		}
		for _, fb := range videoFeedback {
			fmt.Fprintf(&b, "a=rtcp-fb:%d %s\r\n", c.pt, fb)
		}
		fmt.Fprintf(&b, "a=rtpmap:%d rtx/90000\r\na=fmtp:%d apt=%d\r\n", c.rtxPT, c.rtxPT, c.pt)
	}
	// Every layer is its own SSRC line, so pion opens a receiver for each (as Plan B tracks).
	for _, l := range p.VideoLayers() {
		if l.rtx != 0 {
			fmt.Fprintf(&b, "a=ssrc-group:FID %d %d\r\n", l.ssrc, l.rtx)
		}
		fmt.Fprintf(&b, "a=ssrc:%d cname:%d\r\n", l.ssrc, ssrc)
		if l.rtx != 0 {
			fmt.Fprintf(&b, "a=ssrc:%d cname:%d\r\n", l.rtx, ssrc)
		}
	}
	// tgcalls runs SCTP straight over the DTLS transport on port 5000, with no SDP of its own.
	b.WriteString("m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n")
	transport()
	fmt.Fprintf(&b, "a=mid:%s\r\na=sctp-port:5000\r\na=max-message-size:262144\r\n", dataMid)
	return b.String()
}

// VideoDescription is the "video" object of the join answer, as Telegram's SFU sends it: the
// participant's endpoint id (what other clients key its video on), the payload types and header
// extensions, and server_sources (the SSRC tgcalls treats as the server's bandwidth probe).
func VideoDescription(endpoint string, probingSsrc int32) map[string]any {
	feedback := []map[string]string{{"type": "goog-remb"}, {"type": "transport-cc"}, {"type": "ccm", "subtype": "fir"}, {"type": "nack"}, {"type": "nack", "subtype": "pli"}}
	payloadTypes := []map[string]any{}
	for _, c := range videoCodecs {
		var params any = []any{}
		if c.fmtpLine != "" {
			params = map[string]string{"fmtp": c.fmtpLine}
		}
		payloadTypes = append(payloadTypes,
			map[string]any{"id": c.pt, "name": c.name, "clockrate": 90000, "channels": 1, "parameters": params, "rtcp-fbs": feedback},
			map[string]any{"id": c.rtxPT, "clockrate": 90000, "name": "rtx", "parameters": map[string]string{"apt": strconv.Itoa(c.pt)}})
	}
	return map[string]any{
		"endpoint":      endpoint,
		"payload-types": payloadTypes,
		"rtp-hdrexts": []map[string]any{
			{"id": 2, "uri": extAbsSendTime},
			{"id": 3, "uri": extTransportCC},
			{"id": 13, "uri": "urn:3gpp:video-orientation"},
		},
		"server_sources": []int32{probingSsrc},
	}
}

// TransportFromOffer extracts what the client needs to reach the SFU from its
// own SDP offer, in Telegram's Jingle JSON shape.
func TransportFromOffer(offer string) (*Transport, []string, error) {
	var d sdp.SessionDescription
	if err := d.UnmarshalString(offer); err != nil {
		return nil, nil, err
	}
	if len(d.MediaDescriptions) == 0 {
		return nil, nil, errors.New("offer has no media section")
	}
	m := d.MediaDescriptions[0]
	attr := func(key string) string {
		if v, ok := m.Attribute(key); ok {
			return v
		}
		if v, ok := d.Attribute(key); ok {
			return v
		}
		return ""
	}
	t := &Transport{
		Xmlns:   "urn:xmpp:jingle:transports:ice-udp:1",
		Ufrag:   attr("ice-ufrag"),
		Pwd:     attr("ice-pwd"),
		RtcpMux: true,
	}
	if fp := attr("fingerprint"); fp != "" {
		parts := strings.SplitN(fp, " ", 2)
		if len(parts) == 2 {
			t.Fingerprints = []Fingerprint{{Fingerprint: strings.ToUpper(parts[1]), Setup: "passive", Hash: strings.ToLower(parts[0])}}
		}
	}
	n := 0
	for _, a := range m.Attributes {
		if a.Key != "candidate" {
			continue
		}
		f := strings.Fields(a.Value)
		// foundation component transport priority ip port typ type ...
		// rtcp-mux carries RTCP on component 1, which is all Telegram's SFU lists.
		if len(f) < 8 || f[1] != "1" || !strings.EqualFold(f[2], "udp") {
			continue
		}
		n++
		t.Candidates = append(t.Candidates, Candidate{
			Generation: "0",
			Component:  f[1],
			Protocol:   "udp",
			Port:       f[5],
			IP:         f[4],
			Foundation: strconv.Itoa(n),
			ID:         fmt.Sprintf("%x", n),
			Priority:   f[3],
			Type:       f[7],
			Network:    "0",
		})
	}
	mids := []string{}
	for _, md := range d.MediaDescriptions {
		if v, ok := md.Attribute("mid"); ok {
			mids = append(mids, v)
		}
	}
	if t.Ufrag == "" || t.Pwd == "" || len(t.Fingerprints) == 0 || len(t.Candidates) == 0 {
		return nil, nil, errors.New("offer lacks ICE credentials, fingerprint or candidates")
	}
	return t, mids, nil
}
