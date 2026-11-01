package main

import (
	"sync"
	"time"

	"github.com/pion/rtp"
)

// videoLayer is one simulcast encoding of a camera: the SSRC it is sent on and its RTX partner.
type videoLayer struct {
	ssrc uint32
	rtx  uint32
}

// codecOf maps the SFU's video payload types (shared by every client) to a codec name.
func codecOf(pt uint8) string {
	for _, c := range videoCodecs {
		if int(pt) == c.pt {
			return c.name
		}
	}
	return ""
}

// isKeyframeStart reports whether the packet opens an independently decodable frame: the only
// place a receiver can be moved from one simulcast layer to another.
func isKeyframeStart(codec string, payload []byte) bool {
	switch codec {
	case "VP8":
		d, ok := parseVP8Descriptor(payload)
		if !ok || !d.start || d.partition != 0 || d.headerLen >= len(payload) {
			return false
		}
		return payload[d.headerLen]&0x01 == 0
	case "VP9":
		d, ok := parseVP9Descriptor(payload)
		return ok && d.start && !d.interPredicted
	case "H264":
		return h264HasIDR(payload)
	case "AV1":
		// Aggregation header: Z (continues an OBU from the previous packet) and N (first packet
		// of a coded video sequence, i.e. a keyframe).
		return len(payload) > 0 && payload[0]&0x80 == 0 && payload[0]&0x08 != 0
	}
	return false
}

func h264HasIDR(p []byte) bool {
	if len(p) == 0 {
		return false
	}
	switch t := p[0] & 0x1f; {
	case t == 5 || t == 7:
		return true
	case t == 24: // STAP-A
		for i := 1; i+2 < len(p); {
			size := int(p[i])<<8 | int(p[i+1])
			i += 2
			if i >= len(p) {
				return false
			}
			if nt := p[i] & 0x1f; nt == 5 || nt == 7 {
				return true
			}
			i += size
		}
	case t == 28: // FU-A
		return len(p) > 1 && p[1]&0x80 != 0 && (p[1]&0x1f == 5 || p[1]&0x1f == 7)
	}
	return false
}

// pictureIDs is where a VP8 or VP9 payload descriptor keeps the picture id and TL0PICIDX,
// which a receiver uses to chain frames and which differ between simulcast layers.
type pictureIDs struct {
	start          bool
	partition      int
	interPredicted bool
	headerLen      int
	picOffset      int // -1 when absent
	picWide        bool
	tl0Offset      int // -1 when absent
}

func parseVP8Descriptor(p []byte) (pictureIDs, bool) {
	d := pictureIDs{picOffset: -1, tl0Offset: -1}
	if len(p) < 1 {
		return d, false
	}
	d.start = p[0]&0x10 != 0
	d.partition = int(p[0] & 0x07)
	i := 1
	if p[0]&0x80 != 0 {
		if len(p) < 2 {
			return d, false
		}
		ext := p[1]
		i = 2
		if ext&0x80 != 0 {
			if i >= len(p) {
				return d, false
			}
			d.picOffset, d.picWide = i, p[i]&0x80 != 0
			i++
			if d.picWide {
				i++
			}
		}
		if ext&0x40 != 0 {
			d.tl0Offset = i
			i++
		}
		if ext&0x30 != 0 {
			i++
		}
	}
	d.headerLen = i
	return d, i <= len(p)
}

func parseVP9Descriptor(p []byte) (pictureIDs, bool) {
	d := pictureIDs{picOffset: -1, tl0Offset: -1}
	if len(p) < 1 {
		return d, false
	}
	b := p[0]
	d.interPredicted = b&0x40 != 0
	d.start = b&0x08 != 0
	i := 1
	if b&0x80 != 0 {
		if i >= len(p) {
			return d, false
		}
		d.picOffset, d.picWide = i, p[i]&0x80 != 0
		i++
		if d.picWide {
			i++
		}
	}
	if b&0x20 != 0 { // layer indices
		i++
		if b&0x10 == 0 { // non-flexible mode carries TL0PICIDX
			d.tl0Offset = i
			i++
		}
	}
	d.headerLen = i
	return d, i <= len(p)
}

func readPictureID(p []byte, d pictureIDs) uint16 {
	if d.picWide {
		return uint16(p[d.picOffset]&0x7f)<<8 | uint16(p[d.picOffset+1])
	}
	return uint16(p[d.picOffset] & 0x7f)
}

func writePictureID(p []byte, d pictureIDs, v uint16) {
	if d.picWide {
		p[d.picOffset] = 0x80 | byte(v>>8)&0x7f
		p[d.picOffset+1] = byte(v)
		return
	}
	p[d.picOffset] = byte(v) & 0x7f
}

// layerSelector maps one camera's simulcast layers onto the single stream one viewer gets:
// the camera's base SSRC, with sequence numbers, timestamps and picture ids that continue
// across a layer change, as tgcalls renders only that SSRC.
type layerSelector struct {
	mu      sync.Mutex
	current int // -1 until the first keyframe has been forwarded
	started bool

	seqOff uint16
	tsOff  uint32
	picOff uint16
	tl0Off uint8

	lastSeq  uint16
	lastTs   uint32
	lastPic  uint16
	lastTl0  uint8
	lastSent time.Time

	lastAsk time.Time
}

func newLayerSelector() *layerSelector { return &layerSelector{current: -1} }

// route decides what to do with a packet of the given layer when the viewer should be on
// target. It returns the rewritten header and payload to send (nil when the packet is
// dropped) and whether a keyframe of target has to be asked for.
func (s *layerSelector) route(layer, target int, baseSsrc uint32, h *rtp.Header, payload []byte, now time.Time) (*rtp.Header, []byte, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()

	codec := codecOf(h.PayloadType)
	ask := false
	if layer != s.current {
		// Move only at a keyframe; until one comes, keep the viewer on the layer it has. A
		// viewer with nothing yet takes any keyframe at or below its target.
		switchable := layer == target || (s.current < 0 && layer < target)
		if !switchable || !isKeyframeStart(codec, payload) {
			if s.current != target && now.Sub(s.lastAsk) > 300*time.Millisecond {
				s.lastAsk, ask = now, true
			}
			return nil, nil, ask
		}
		s.switchTo(layer, h, payload, codec, now)
	} else if s.current != target && now.Sub(s.lastAsk) > 300*time.Millisecond {
		s.lastAsk, ask = now, true
	}

	out := *h
	out.SSRC = baseSsrc
	out.SequenceNumber = h.SequenceNumber + s.seqOff
	out.Timestamp = h.Timestamp + s.tsOff

	if s.picOff != 0 || s.tl0Off != 0 {
		if d, ok := descriptorOf(codec, payload); ok && (d.picOffset >= 0 || d.tl0Offset >= 0) {
			payload = append([]byte(nil), payload...)
			if d.picOffset >= 0 {
				mask := uint16(0x7f)
				if d.picWide {
					mask = 0x7fff
				}
				writePictureID(payload, d, (readPictureID(payload, d)+s.picOff)&mask)
			}
			if d.tl0Offset >= 0 {
				payload[d.tl0Offset] += s.tl0Off
			}
		}
	}
	s.remember(&out, codec, payload, now)
	return &out, payload, ask
}

// switchTo sets the offsets so the new layer's packets continue right after the last packet
// the viewer received.
func (s *layerSelector) switchTo(layer int, h *rtp.Header, payload []byte, codec string, now time.Time) {
	if s.started {
		s.seqOff = s.lastSeq + 1 - h.SequenceNumber
		elapsed := uint32(now.Sub(s.lastSent).Microseconds() * 90 / 1000)
		if elapsed == 0 {
			elapsed = 1
		}
		s.tsOff = s.lastTs + elapsed - h.Timestamp
		if d, ok := descriptorOf(codec, payload); ok {
			if d.picOffset >= 0 {
				s.picOff = s.lastPic + 1 - readPictureID(payload, d)
			}
			if d.tl0Offset >= 0 {
				s.tl0Off = s.lastTl0 + 1 - payload[d.tl0Offset]
			}
		}
	}
	s.current = layer
}

func (s *layerSelector) remember(out *rtp.Header, codec string, payload []byte, now time.Time) {
	if !s.started || int16(out.SequenceNumber-s.lastSeq) > 0 {
		s.lastSeq = out.SequenceNumber
	}
	if !s.started || int32(out.Timestamp-s.lastTs) > 0 {
		s.lastTs = out.Timestamp
		s.lastSent = now
	}
	if d, ok := descriptorOf(codec, payload); ok {
		if d.picOffset >= 0 {
			s.lastPic = readPictureID(payload, d)
		}
		if d.tl0Offset >= 0 {
			s.lastTl0 = payload[d.tl0Offset]
		}
	}
	s.started = true
}

func (s *layerSelector) layer() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.current
}

func descriptorOf(codec string, payload []byte) (pictureIDs, bool) {
	switch codec {
	case "VP8":
		return parseVP8Descriptor(payload)
	case "VP9":
		return parseVP9Descriptor(payload)
	}
	return pictureIDs{}, false
}
