package main

import (
	"encoding/json"
	"log"

	"github.com/pion/webrtc/v4"
)

// tgcalls opens one SCTP data channel over the call's DTLS transport (ports 5000/5000, stream
// 0, label "data", as the DCEP opener) and speaks colibri JSON on it: it sends
// ReceiverVideoConstraints and acts on SenderVideoConstraints.

// receiverConstraints is what a viewer asked for in its last ReceiverVideoConstraints: the
// highest picture height it wants from each endpoint, and from any endpoint it did not list.
type receiverConstraints struct {
	defaultMaxHeight int
	maxHeight        map[string]int
}

type colibriMessage struct {
	ColibriClass       string `json:"colibriClass"`
	DefaultConstraints *struct {
		MaxHeight *int `json:"maxHeight"`
	} `json:"defaultConstraints"`
	Constraints map[string]struct {
		MaxHeight *int `json:"maxHeight"`
	} `json:"constraints"`
}

// parseReceiverVideoConstraints reads a ReceiverVideoConstraints message. An endpoint listed
// without a maxHeight is not capped; one not listed gets the default (tgcalls sends 0: no video).
func parseReceiverVideoConstraints(raw []byte) (*receiverConstraints, bool) {
	var m colibriMessage
	if err := json.Unmarshal(raw, &m); err != nil || m.ColibriClass != "ReceiverVideoConstraints" {
		return nil, false
	}
	c := &receiverConstraints{defaultMaxHeight: unconstrainedHeight, maxHeight: map[string]int{}}
	if m.DefaultConstraints != nil && m.DefaultConstraints.MaxHeight != nil {
		c.defaultMaxHeight = *m.DefaultConstraints.MaxHeight
	}
	for endpoint, ec := range m.Constraints {
		h := unconstrainedHeight
		if ec.MaxHeight != nil {
			h = *ec.MaxHeight
		}
		c.maxHeight[endpoint] = h
	}
	return c, true
}

// unconstrainedHeight is what a viewer without constraints wants: the top layer.
const unconstrainedHeight = 720

func (c *receiverConstraints) heightFor(endpoint string) int {
	if c == nil {
		return unconstrainedHeight
	}
	if h, ok := c.maxHeight[endpoint]; ok {
		return h
	}
	return c.defaultMaxHeight
}

// layerForHeight maps a wanted picture height to the highest simulcast layer it allows, using
// the heights tgcalls encodes its three layers at (180, 360, 720). -1 means no video.
func layerForHeight(height, layers int) int {
	var l int
	switch {
	case height <= 0:
		return -1
	case height >= 720:
		l = 2
	case height >= 360:
		l = 1
	default:
		l = 0
	}
	if l >= layers {
		l = layers - 1
	}
	return l
}

// openDataChannel makes the offer carry an SCTP section. The channel itself is pre-negotiated
// on a stream tgcalls never uses, so nothing is sent on it; the client's own channel arrives
// through OnDataChannel when its DCEP OPEN does.
func (p *Participant) openDataChannel() error {
	negotiated, id := true, uint16(1)
	if _, err := p.pc.CreateDataChannel("sfu", &webrtc.DataChannelInit{Negotiated: &negotiated, ID: &id}); err != nil {
		return err
	}
	p.pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		dc.OnOpen(func() {
			p.dc.Store(dc)
			p.call.updateSenderConstraints()
		})
		dc.OnClose(func() { p.dc.CompareAndSwap(dc, nil) })
		dc.OnMessage(func(msg webrtc.DataChannelMessage) {
			if c, ok := parseReceiverVideoConstraints(msg.Data); ok {
				p.constraints.Store(c)
				p.call.updateSenderConstraints()
			}
		})
	})
	return nil
}

// updateSenderConstraints tells every camera the largest picture any viewer wants from it,
// so its encoder can switch off the layers nobody watches.
func (c *Call) updateSenderConstraints() {
	c.mu.RLock()
	participants := make([]*Participant, 0, len(c.participants))
	for _, p := range c.participants {
		participants = append(participants, p)
	}
	c.mu.RUnlock()
	for _, camera := range participants {
		if camera.videoMain == 0 {
			continue
		}
		ideal, viewers := 0, 0
		for _, viewer := range participants {
			if viewer == camera {
				continue
			}
			viewers++
			if h := viewer.constraints.Load().heightFor(camera.Endpoint); h > ideal {
				ideal = h
			}
		}
		if viewers > 0 {
			camera.sendSenderConstraints(ideal)
		}
	}
}

func (p *Participant) sendSenderConstraints(idealHeight int) {
	dc := p.dc.Load()
	if dc == nil {
		return
	}
	p.mu.Lock()
	if p.sentIdealHeight == idealHeight {
		p.mu.Unlock()
		return
	}
	p.sentIdealHeight = idealHeight
	p.mu.Unlock()
	msg, _ := json.Marshal(map[string]any{
		"colibriClass":     "SenderVideoConstraints",
		"videoConstraints": map[string]int{"idealHeight": idealHeight},
	})
	if err := dc.SendText(string(msg)); err != nil {
		log.Printf("participant %s: sending SenderVideoConstraints: %v", p.Key, err)
	}
}
