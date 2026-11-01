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

	"github.com/pion/rtp"
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
	return joinFull(t, s, callID, key, c).Transport
}

func joinFull(t *testing.T, s *SFU, callID int64, key string, c *tgClient) *JoinResult {
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
	return joined
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

func TestSimulcast_TheViewerGetsTheTopLayer_OnTheBaseSsrc_AndThatLayerIsAskedForKeyframes(t *testing.T) {
	s := newTestSFU(t)
	camera, viewer := newTGClientWith(t, true), newTGClientWith(t, true)
	join(t, s, 21, "camera", camera)
	join(t, s, 21, "viewer", viewer)
	defer s.End(21)

	stop := make(chan struct{})
	defer close(stop)
	for layer := 0; layer < 3; layer++ {
		go camera.film(stop, layer)
	}

	waitFor(t, "the viewer to be moved to the top layer", func() bool { return layerOf(s, 21, "camera", "viewer") == 2 })
	waitFor(t, "the viewer to receive video", func() bool { return viewer.count(camera.videoSsrc[0]) >= 20 })
	if viewer.count(camera.videoSsrc[1]) != 0 || viewer.count(camera.videoSsrc[2]) != 0 {
		t.Fatal("an upper simulcast layer reached the viewer on its own SSRC; tgcalls renders only the base SSRC")
	}

	// The SFU's own count: pion hands a client only the RTCP for its own sender SSRC, while
	// libwebrtc answers a picture-loss request for any simulcast SSRC it declared.
	before := layerKeyframes(s, 21, "camera", 2)
	waitFor(t, "periodic keyframe requests for the watched layer", func() bool { return layerKeyframes(s, 21, "camera", 2) >= before+2 })

	sources := map[int32]bool{}
	for _, src := range s.Sources(21) {
		sources[src] = true
	}
	for i := range camera.videoSsrc {
		if !sources[int32(camera.videoSsrc[i])] {
			t.Fatalf("sources %v lack simulcast layer %d", s.Sources(21), i)
		}
	}
}

func TestSimulcast_WhenTheTopLayerStops_TheViewerFallsBack_AndTheStreamStaysContinuous(t *testing.T) {
	s := newTestSFU(t)
	camera, viewer := newTGClientWith(t, true), newTGClientWith(t, true)
	join(t, s, 22, "camera", camera)
	join(t, s, 22, "viewer", viewer)
	defer s.End(22)

	stopAll := make(chan struct{})
	defer close(stopAll)
	stopTop := make(chan struct{})
	go camera.film(stopAll, 0)
	go camera.film(stopAll, 1)
	go camera.film(stopTop, 2)
	waitFor(t, "the top layer", func() bool { return layerOf(s, 22, "camera", "viewer") == 2 })
	time.Sleep(300 * time.Millisecond)
	close(stopTop) // the camera's encoder turns its top layer off
	waitFor(t, "the fallback to the middle layer", func() bool { return layerOf(s, 22, "camera", "viewer") == 1 })
	time.Sleep(300 * time.Millisecond)

	headers := viewer.videoHeaders(camera.videoSsrc[0])
	if len(headers) < 30 {
		t.Fatalf("only %d packets reached the viewer", len(headers))
	}
	seen := map[uint16]bool{}
	for i := 1; i < len(headers); i++ {
		if seen[headers[i].SequenceNumber] {
			t.Fatalf("sequence number %d repeated across the layer change", headers[i].SequenceNumber)
		}
		seen[headers[i].SequenceNumber] = true
		if gap := headers[i].SequenceNumber - headers[i-1].SequenceNumber; gap != 1 {
			t.Fatalf("sequence jumped by %d at packet %d", gap, i)
		}
		// One frame is 3000; the fallback waits out up to a second of silence of the top layer.
		if step := int32(headers[i].Timestamp - headers[i-1].Timestamp); step <= 0 || step > 2*90000 {
			t.Fatalf("timestamp moved by %d at packet %d", step, i)
		}
	}
}

func TestLayerSelector_WaitsForAKeyframe_AndContinuesPictureIds(t *testing.T) {
	sel := newLayerSelector()
	vp8 := func(pic uint16, key bool) []byte {
		inter := byte(1)
		if key {
			inter = 0
		}
		return []byte{0x90, 0x80, 0x80 | byte(pic>>8), byte(pic), inter}
	}
	now := time.Unix(100, 0)
	h := func(seq uint16, ts uint32) *rtp.Header {
		return &rtp.Header{PayloadType: 100, SequenceNumber: seq, Timestamp: ts, SSRC: 7}
	}

	// Layer 0 starts the stream.
	out, _, _ := sel.route(0, 0, 1, h(10, 1000), vp8(50, true), now)
	if out == nil || out.SSRC != 1 || out.SequenceNumber != 10 {
		t.Fatalf("the first keyframe should pass unchanged onto the base ssrc, got %+v", out)
	}
	now = now.Add(33 * time.Millisecond)
	sel.route(0, 0, 1, h(11, 3970), vp8(51, false), now)

	// Target becomes layer 1; its delta frames are held back and a keyframe is asked for.
	now = now.Add(10 * time.Millisecond)
	out, _, ask := sel.route(1, 1, 1, h(500, 777000), vp8(9000, false), now)
	if out != nil || !ask {
		t.Fatalf("a delta frame of the new layer must not be forwarded, and a keyframe must be requested (out=%v ask=%v)", out, ask)
	}
	out, _, _ = sel.route(0, 1, 1, h(12, 6940), vp8(52, false), now)
	if out == nil || out.SequenceNumber != 12 {
		t.Fatal("the viewer must stay on its current layer until the new one has a keyframe")
	}

	now = now.Add(33 * time.Millisecond)
	out, payload, _ := sel.route(1, 1, 1, h(501, 780000), vp8(9001, true), now)
	if out == nil || out.SequenceNumber != 13 {
		t.Fatalf("the switch must continue the sequence: got %+v", out)
	}
	if step := out.Timestamp - 6940; step == 0 || step > 9000 {
		t.Fatalf("the timestamp should continue near the previous frame, moved by %d", step)
	}
	if pic := uint16(payload[2]&0x7f)<<8 | uint16(payload[3]); pic != 53 {
		t.Fatalf("the picture id should continue at 53, got %d", pic)
	}
	if sel.layer() != 1 {
		t.Fatal("the selector did not move to layer 1")
	}
}

func layerKeyframes(s *SFU, call int64, camera string, layer int) uint64 {
	for _, st := range s.Stats(call) {
		if st.Key == camera && layer < len(st.LayerKeyframes) {
			return st.LayerKeyframes[layer]
		}
	}
	return 0
}

func layerOf(s *SFU, call int64, camera, viewer string) int {
	for _, st := range s.Stats(call) {
		if st.Key == camera {
			if l, ok := st.Layers[viewer]; ok {
				return l
			}
		}
	}
	return -1
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

	before := camera.layerPLIs[0].Load()
	join(t, s, 23, "newcomer", newTGClient(t))
	waitFor(t, "a keyframe request for the newcomer", func() bool { return camera.layerPLIs[0].Load() > before })
}

func TestVideoLayers_FollowTheSimGroupOrder_WithTheirRtx(t *testing.T) {
	p, _ := ParseJoinParams(`{"ufrag":"a","pwd":"b","fingerprints":[{"fingerprint":"AA"}],"ssrc":1,"ssrc-groups":[{"semantics":"SIM","sources":[10,12,14]},{"semantics":"FID","sources":[12,13]},{"semantics":"FID","sources":[10,11]},{"semantics":"FID","sources":[14,15]}]}`)
	got := p.VideoLayers()
	want := []videoLayer{{10, 11}, {12, 13}, {14, 15}}
	if len(got) != 3 || got[0] != want[0] || got[1] != want[1] || got[2] != want[2] {
		t.Fatalf("got %v, want %v", got, want)
	}
	answer := p.RemoteAnswer("0", "1", "2")
	for _, line := range []string{"a=ssrc:12 cname:1", "a=ssrc:14 cname:1", "a=ssrc-group:FID 14 15"} {
		if !strings.Contains(answer, line) {
			t.Fatalf("the answer lacks %q, so pion would not receive that layer", line)
		}
	}
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

func TestDataChannel_ConstraintsCapTheLayer_AndZeroStopsTheVideo(t *testing.T) {
	s := newTestSFU(t)
	camera, viewer := newTGClientWith(t, true), newTGClientWith(t, true)
	cam := joinFull(t, s, 31, "camera", camera)
	join(t, s, 31, "viewer", viewer)
	defer s.End(31)

	stop := make(chan struct{})
	defer close(stop)
	for layer := 0; layer < 3; layer++ {
		go camera.film(stop, layer)
	}
	waitFor(t, "the viewer's data channel to open on the SFU", func() bool {
		for _, st := range s.Stats(31) {
			if st.Key == "viewer" && st.DataChannel {
				return true
			}
		}
		return false
	})
	waitFor(t, "the top layer before any constraint", func() bool { return layerOf(s, 31, "camera", "viewer") == 2 })

	viewer.constrain(map[string]int{cam.Endpoint: 360})
	waitFor(t, "the viewer to be capped at the 360p layer", func() bool { return layerOf(s, 31, "camera", "viewer") == 1 })

	viewer.constrain(map[string]int{}) // the camera is no longer shown: default maxHeight 0
	waitFor(t, "the viewer to stop getting the camera", func() bool { return layerOf(s, 31, "camera", "viewer") == -1 })
	n := viewer.count(camera.videoSsrc[0])
	time.Sleep(400 * time.Millisecond)
	if got := viewer.count(camera.videoSsrc[0]); got != n {
		t.Fatalf("video kept flowing after maxHeight 0: %d -> %d packets", n, got)
	}

	viewer.constrain(map[string]int{cam.Endpoint: 180})
	waitFor(t, "the thumbnail layer", func() bool { return layerOf(s, 31, "camera", "viewer") == 0 })
}

func TestDataChannel_TheCameraIsToldTheLargestPictureAnyViewerWants(t *testing.T) {
	s := newTestSFU(t)
	camera, a, b := newTGClientWith(t, true), newTGClient(t), newTGClient(t)
	cam := joinFull(t, s, 32, "camera", camera)
	join(t, s, 32, "a", a)
	join(t, s, 32, "b", b)
	defer s.End(32)

	waitFor(t, "every data channel", func() bool { return camera.dcOpen.Load() && a.dcOpen.Load() && b.dcOpen.Load() })
	a.constrain(map[string]int{cam.Endpoint: 180})
	b.constrain(map[string]int{cam.Endpoint: 360})
	waitFor(t, "SenderVideoConstraints with idealHeight 360", func() bool {
		return strings.Contains(camera.lastMessage(), `"idealHeight":360`) && strings.Contains(camera.lastMessage(), `"colibriClass":"SenderVideoConstraints"`)
	})
	b.constrain(map[string]int{})
	waitFor(t, "idealHeight to drop to 180", func() bool { return strings.Contains(camera.lastMessage(), `"idealHeight":180`) })
}

func TestParseReceiverVideoConstraints_FollowsTgcallsShape(t *testing.T) {
	c, ok := parseReceiverVideoConstraints([]byte(`{"colibriClass":"ReceiverVideoConstraints","defaultConstraints":{"maxHeight":0},"onStageEndpoints":["e1"],"constraints":{"e1":{"minHeight":720,"maxHeight":720},"e2":{"maxHeight":180},"e3":{}}}`))
	if !ok {
		t.Fatal("not parsed")
	}
	for endpoint, want := range map[string]int{"e1": 720, "e2": 180, "e3": 720, "other": 0} {
		if got := c.heightFor(endpoint); got != want {
			t.Errorf("%s: maxHeight %d, want %d", endpoint, got, want)
		}
	}
	for h, want := range map[int]int{0: -1, 90: 0, 180: 0, 360: 1, 540: 1, 720: 2, 1080: 2} {
		if got := layerForHeight(h, 3); got != want {
			t.Errorf("height %d -> layer %d, want %d", h, got, want)
		}
	}
	if layerForHeight(720, 1) != 0 {
		t.Error("a single-layer stream (screen share) must use its only layer")
	}
	if _, ok := parseReceiverVideoConstraints([]byte(`{"colibriClass":"EndpointMessage"}`)); ok {
		t.Error("another colibri class was taken for constraints")
	}
}
