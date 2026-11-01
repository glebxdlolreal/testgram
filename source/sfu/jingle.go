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

// RemoteAnswer turns the client's join params into the SDP answer the SFU's
// offer needs. setup:passive makes the SFU the DTLS client, which is what the
// client expects: tgcalls' GroupNetworkManager always runs DTLS as the server.
func (p *JoinParams) RemoteAnswer(mid string) string {
	fp := p.Fingerprints[0]
	hash := fp.Hash
	if hash == "" {
		hash = "sha-256"
	}
	ssrc := p.Ssrc32()
	var b strings.Builder
	b.WriteString("v=0\r\no=- 1 2 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n")
	fmt.Fprintf(&b, "a=group:BUNDLE %s\r\n", mid)
	fmt.Fprintf(&b, "m=audio 9 UDP/TLS/RTP/SAVPF %d\r\n", opusPayloadType)
	b.WriteString("c=IN IP4 0.0.0.0\r\na=rtcp:9 IN IP4 0.0.0.0\r\n")
	fmt.Fprintf(&b, "a=ice-ufrag:%s\r\na=ice-pwd:%s\r\n", p.Ufrag, p.Pwd)
	fmt.Fprintf(&b, "a=fingerprint:%s %s\r\n", hash, fp.Fingerprint)
	b.WriteString("a=setup:passive\r\n")
	fmt.Fprintf(&b, "a=mid:%s\r\n", mid)
	fmt.Fprintf(&b, "a=extmap:1 %s\r\na=extmap:2 %s\r\na=extmap:3 %s\r\n", extAudioLevel, extAbsSendTime, extTransportCC)
	b.WriteString("a=sendrecv\r\na=rtcp-mux\r\n")
	fmt.Fprintf(&b, "a=rtpmap:%d opus/48000/2\r\na=fmtp:%d minptime=10;useinbandfec=1\r\n", opusPayloadType, opusPayloadType)
	fmt.Fprintf(&b, "a=rtcp-fb:%d transport-cc\r\n", opusPayloadType)
	fmt.Fprintf(&b, "a=ssrc:%d cname:%d\r\n", ssrc, ssrc)
	return b.String()
}

// TransportFromOffer extracts what the client needs to reach the SFU from its
// own SDP offer, in Telegram's Jingle JSON shape.
func TransportFromOffer(offer string) (*Transport, string, error) {
	var d sdp.SessionDescription
	if err := d.UnmarshalString(offer); err != nil {
		return nil, "", err
	}
	if len(d.MediaDescriptions) == 0 {
		return nil, "", errors.New("offer has no media section")
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
	mid := attr("mid")
	if t.Ufrag == "" || t.Pwd == "" || len(t.Fingerprints) == 0 || len(t.Candidates) == 0 {
		return nil, "", errors.New("offer lacks ICE credentials, fingerprint or candidates")
	}
	return t, mid, nil
}
