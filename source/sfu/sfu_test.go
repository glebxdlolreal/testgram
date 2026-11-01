package main

import (
	"bytes"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func newTestSFU(t *testing.T) *SFU {
	t.Helper()
	conn, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	s, err := NewSFU(Config{PublicIPs: []string{"127.0.0.1"}, UDP: conn, IncludeLoopback: true, KeyframeInterval: 300 * time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func join(t *testing.T, s *SFU, callID int64, key string, c *tgClient) *Transport {
	t.Helper()
	params, err := ParseJoinParams(c.params)
	if err != nil {
		t.Fatal(err)
	}
	joined, err := s.Join(callID, key, params)
	if err != nil {
		t.Fatal(err)
	}
	c.connect(joined.Transport)
	return joined.Transport
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func TestTransportHasTelegramsShape(t *testing.T) {
	s := newTestSFU(t)
	c := newTGClient(t)
	params, _ := ParseJoinParams(c.params)
	joined, err := s.Join(1, "1", params)
	if err != nil {
		t.Fatal(err)
	}
	defer s.End(1)
	tr := joined.Transport

	if len(joined.Endpoint) != 24 || joined.Video["endpoint"] != joined.Endpoint {
		t.Fatalf("endpoint %q not in Telegram's form or not in the video section", joined.Endpoint)
	}
	if src := joined.Video["server_sources"].([]int32); len(src) != 1 || src[0] == 0 {
		t.Fatalf("server_sources = %v", src)
	}

	if tr.Xmlns != "urn:xmpp:jingle:transports:ice-udp:1" || !tr.RtcpMux {
		t.Fatalf("not a Jingle ice-udp transport: %+v", tr)
	}
	if tr.Ufrag == "" || tr.Pwd == "" {
		t.Fatal("no ICE credentials")
	}
	fp := tr.Fingerprints[0]
	if fp.Hash != "sha-256" || fp.Setup != "passive" || strings.Count(fp.Fingerprint, ":") != 31 || fp.Fingerprint != strings.ToUpper(fp.Fingerprint) {
		t.Fatalf("fingerprint not in Telegram's form: %+v", fp)
	}
	if len(tr.Candidates) == 0 {
		t.Fatal("no candidates")
	}
	for _, cand := range tr.Candidates {
		if cand.IP != "127.0.0.1" || cand.Type != "host" || cand.Protocol != "udp" || cand.Component != "1" || cand.Generation != "0" || cand.Network != "0" {
			t.Fatalf("candidate not in Telegram's form: %+v", cand)
		}
	}
	raw, _ := json.Marshal(map[string]any{"transport": tr})
	for _, key := range []string{`"generation":"0"`, `"rtcp-mux":true`, `"xmlns"`, `"port":"`} {
		if !strings.Contains(string(raw), key) {
			t.Fatalf("serialized transport lacks %s: %s", key, raw)
		}
	}
}

func TestTwoParticipantsHearEachOther_WithTheirOwnSsrcs(t *testing.T) {
	s := newTestSFU(t)
	alice, bob := newTGClient(t), newTGClient(t)
	join(t, s, 7, "alice", alice)
	join(t, s, 7, "bob", bob)
	defer s.End(7)

	stop := make(chan struct{})
	defer close(stop)
	go alice.speak(stop)
	go bob.speak(stop)

	waitFor(t, "bob to hear alice", func() bool { return bob.count(alice.ssrc) >= 20 })
	waitFor(t, "alice to hear bob", func() bool { return alice.count(bob.ssrc) >= 20 })

	if alice.count(alice.ssrc) != 0 || bob.count(bob.ssrc) != 0 {
		t.Fatal("a speaker got their own audio back")
	}
	if !bob.hadLevel(alice.ssrc) {
		t.Fatal("the audio-level extension (id 1) did not survive forwarding")
	}

	sources := s.Sources(7)
	want := map[int32]bool{int32(alice.ssrc): true, int32(bob.ssrc): true}
	if len(sources) != 2 || !want[sources[0]] || !want[sources[1]] {
		t.Fatalf("sources = %v, want alice's and bob's", sources)
	}
}

func TestThirdParticipant_IsForwardedToEveryone_AndLeavingStopsIt(t *testing.T) {
	s := newTestSFU(t)
	a, b, c := newTGClient(t), newTGClient(t), newTGClient(t)
	join(t, s, 9, "a", a)
	join(t, s, 9, "b", b)
	join(t, s, 9, "c", c)
	defer s.End(9)

	stop := make(chan struct{})
	go c.speak(stop)
	waitFor(t, "a and b to hear c", func() bool { return a.count(c.ssrc) >= 10 && b.count(c.ssrc) >= 10 })
	close(stop)

	if !s.Leave(9, "c") {
		t.Fatal("leave found no participant")
	}
	for _, src := range s.Sources(9) {
		if src == int32(c.ssrc) {
			t.Fatal("a participant who left is still listed as a source")
		}
	}
	if s.Leave(9, "c") {
		t.Fatal("second leave reported a removal")
	}
}

func TestRejoin_ReplacesTheParticipantsConnection(t *testing.T) {
	s := newTestSFU(t)
	first, second, listener := newTGClient(t), newTGClient(t), newTGClient(t)
	join(t, s, 11, "listener", listener)
	join(t, s, 11, "speaker", first)
	join(t, s, 11, "speaker", second)
	defer s.End(11)

	stop := make(chan struct{})
	defer close(stop)
	go second.speak(stop)
	waitFor(t, "the rejoined connection to be forwarded", func() bool { return listener.count(second.ssrc) >= 10 })
	if len(s.Stats(11)) != 2 {
		t.Fatalf("rejoin left %d participants, want 2", len(s.Stats(11)))
	}
}

func TestAPI_JoinSourcesLeave(t *testing.T) {
	s := newTestSFU(t)
	srv := httptest.NewServer(NewAPIHandler(s))
	defer srv.Close()
	c := newTGClient(t)

	body, _ := json.Marshal(map[string]string{"params": c.params})
	resp, err := http.Post(srv.URL+"/v1/calls/42/participants/2010001", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	var joined struct {
		Transport *Transport     `json:"transport"`
		Video     map[string]any `json:"video"`
		Endpoint  string         `json:"endpoint"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&joined); err != nil || resp.StatusCode != 200 || joined.Transport == nil || joined.Video["endpoint"] != joined.Endpoint {
		t.Fatalf("join: status %d err %v", resp.StatusCode, err)
	}
	c.connect(joined.Transport)
	waitFor(t, "the connection", func() bool {
		st := s.Stats(42)
		return len(st) == 1 && st[0].Connected
	})

	resp, _ = http.Get(srv.URL + "/v1/calls/42/sources")
	var sources struct {
		Sources []int32 `json:"sources"`
	}
	_ = json.NewDecoder(resp.Body).Decode(&sources)
	if len(sources.Sources) != 1 || sources.Sources[0] != int32(c.ssrc) {
		t.Fatalf("sources = %v, want [%d]", sources.Sources, int32(c.ssrc))
	}

	bad, _ := json.Marshal(map[string]string{"params": `{"ssrc":1}`})
	resp, _ = http.Post(srv.URL+"/v1/calls/42/participants/5", "application/json", bytes.NewReader(bad))
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("params without ICE credentials: status %d, want 400", resp.StatusCode)
	}

	req, _ := http.NewRequest(http.MethodDelete, srv.URL+"/v1/calls/42", nil)
	if resp, _ = http.DefaultClient.Do(req); resp.StatusCode != 200 {
		t.Fatalf("end: status %d", resp.StatusCode)
	}
	if len(s.Sources(42)) != 0 {
		t.Fatal("ended call still lists sources")
	}
}

func TestSources_IncludeAParticipantStillConnecting(t *testing.T) {
	s := newTestSFU(t)
	c := newTGClient(t)
	params, _ := ParseJoinParams(c.params)
	if _, err := s.Join(13, "1", params); err != nil {
		t.Fatal(err)
	}
	defer s.End(13)

	// The client has not connected at all, yet its source is known, as on Telegram's SFU.
	if got := s.Sources(13); len(got) != 1 || got[0] != int32(c.ssrc) {
		t.Fatalf("sources = %v, want [%d]", got, int32(c.ssrc))
	}
}

func TestParseJoinParams_RejectsIncompleteParams(t *testing.T) {
	for _, raw := range []string{`{`, `{"ufrag":"a","pwd":"b","ssrc":1}`, `{"ufrag":"a","pwd":"b","fingerprints":[{"fingerprint":"AA"}]}`} {
		if _, err := ParseJoinParams(raw); err == nil {
			t.Fatalf("accepted %s", raw)
		}
	}
	p, err := ParseJoinParams(`{"ufrag":"a","pwd":"b","fingerprints":[{"hash":"sha-256","fingerprint":"AA","setup":"passive"}],"ssrc":-1931371821}`)
	if err != nil {
		t.Fatal(err)
	}
	if p.Ssrc32() != 2363595475 {
		t.Fatalf("a negative (int32) ssrc must map to its unsigned value, got %d", p.Ssrc32())
	}
}

func TestVideo_BaseLayerIsForwarded_UpperLayersAreNot_AndTheWatchedCameraIsAskedForKeyframes(t *testing.T) {
	s := newTestSFU(t)
	camera, viewer := newTGClientWith(t, true), newTGClientWith(t, true)
	join(t, s, 21, "camera", camera)
	join(t, s, 21, "viewer", viewer)
	defer s.End(21)

	stop := make(chan struct{})
	defer close(stop)
	go camera.film(stop, 0)
	go camera.film(stop, 2)

	waitFor(t, "the viewer to receive the camera's base layer", func() bool { return viewer.count(camera.videoSsrc[0]) >= 20 })
	if viewer.count(camera.videoSsrc[2]) != 0 {
		t.Fatal("an upper simulcast layer was forwarded; receivers render only the base SSRC")
	}

	before := camera.keyframes.Load()
	waitFor(t, "periodic keyframe requests to reach the camera", func() bool { return camera.keyframes.Load() >= before+2 })

	sources := map[int32]bool{}
	for _, src := range s.Sources(21) {
		sources[src] = true
	}
	if !sources[int32(camera.videoSsrc[0])] || !sources[int32(camera.ssrc)] {
		t.Fatalf("sources %v lack the camera's audio or base video ssrc", s.Sources(21))
	}
}

func TestVideo_ANewcomerTriggersAKeyframeFromEveryCamera(t *testing.T) {
	s := newTestSFU(t)
	camera := newTGClientWith(t, true)
	join(t, s, 23, "camera", camera)
	defer s.End(23)
	waitFor(t, "the camera to connect", func() bool {
		st := s.Stats(23)
		return len(st) == 1 && st[0].Connected
	})

	before := camera.keyframes.Load()
	join(t, s, 23, "newcomer", newTGClient(t))
	waitFor(t, "a keyframe request for the newcomer", func() bool { return camera.keyframes.Load() > before })
}

func TestVideoSsrcs_TakeTheBaseLayerAndItsRtx(t *testing.T) {
	p, err := ParseJoinParams(`{"ufrag":"a","pwd":"b","fingerprints":[{"fingerprint":"AA"}],"ssrc":1,"ssrc-groups":[{"semantics":"SIM","sources":[10,12,14]},{"semantics":"FID","sources":[12,13]},{"semantics":"FID","sources":[10,11]}]}`)
	if err != nil {
		t.Fatal(err)
	}
	if main, rtx, ok := p.VideoSsrcs(); !ok || main != 10 || rtx != 11 {
		t.Fatalf("got %d/%d/%v, want 10/11", main, rtx, ok)
	}
	screen, _ := ParseJoinParams(`{"ufrag":"a","pwd":"b","fingerprints":[{"fingerprint":"AA"}],"ssrc":1,"ssrc-groups":[{"semantics":"FID","sources":[-5,-4]}]}`)
	if main, rtx, ok := screen.VideoSsrcs(); !ok || int32(main) != -5 || int32(rtx) != -4 {
		t.Fatalf("a non-simulcast stream (screen share) should use its FID pair, got %d/%d", int32(main), int32(rtx))
	}
	audioOnly, _ := ParseJoinParams(`{"ufrag":"a","pwd":"b","fingerprints":[{"fingerprint":"AA"}],"ssrc":1,"ssrc-groups":[]}`)
	if _, _, ok := audioOnly.VideoSsrcs(); ok {
		t.Fatal("audio-only params reported video")
	}
}
