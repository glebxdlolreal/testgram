package main

import (
	"bytes"
	"log"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

// Reflector is the UDP relay Telegram lists as phoneConnection (with a peer_tag) next to
// its TURN servers, speaking tgcalls' v2 ReflectorPort protocol:
//
//	a client's tag  = peer_tag[0:12] + its own random 4 bytes
//	hello / ping    = [own tag 16][0xFF x12][0xFE][0xFF x3][u64 123], padded to 4 bytes
//	data            = [target tag 16][sender random 4][size u32 BE][payload], padded to 4 bytes
//
// The reflector learns where every (peer_tag[0:12], random) lives from what it sends, echoes
// hellos (any packet carrying the client's peer_tag makes its port ready), and forwards data
// packets unchanged to the client whose tag they name: the receiving client checks only the
// first 12 bytes and reads the sender's random tag at offset 16.
type Reflector struct {
	conn    net.PacketConn
	idleTTL time.Duration

	mu    sync.Mutex
	peers map[peerKey]*peerEntry

	Forwarded atomic.Uint64
	Hellos    atomic.Uint64
	Dropped   atomic.Uint64
}

type peerKey struct {
	tag    [12]byte
	random [4]byte
}

type peerEntry struct {
	addr     net.Addr
	lastSeen time.Time
}

const (
	tagSize    = 16
	headerSize = tagSize + 4 + 4
	maxPacket  = 1 << 16
)

var specialMarker = bytes.Repeat([]byte{0xff}, 12)

func NewReflector(conn net.PacketConn, idleTTL time.Duration) *Reflector {
	return &Reflector{conn: conn, idleTTL: idleTTL, peers: map[peerKey]*peerEntry{}}
}

// Serve reads packets until the socket is closed.
func (r *Reflector) Serve() error {
	go r.expireLoop()
	buf := make([]byte, maxPacket)
	for {
		n, addr, err := r.conn.ReadFrom(buf)
		if err != nil {
			return err
		}
		r.handle(buf[:n], addr)
	}
}

func (r *Reflector) handle(p []byte, from net.Addr) {
	if len(p) < tagSize {
		r.Dropped.Add(1)
		return
	}
	var key peerKey
	copy(key.tag[:], p[0:12])

	// A hello (or keepalive ping) carries the sender's own tag first.
	if len(p) >= tagSize+12 && bytes.Equal(p[tagSize:tagSize+12], specialMarker) {
		copy(key.random[:], p[12:16])
		r.remember(key, from)
		r.Hellos.Add(1)
		_, _ = r.conn.WriteTo(p, from)
		return
	}

	if len(p) < headerSize {
		r.Dropped.Add(1)
		return
	}
	sender := key
	copy(sender.random[:], p[16:20])
	r.remember(sender, from)

	target := key
	copy(target.random[:], p[12:16])
	r.mu.Lock()
	dest := r.peers[target]
	r.mu.Unlock()
	if dest == nil {
		r.Dropped.Add(1)
		return
	}
	if _, err := r.conn.WriteTo(p, dest.addr); err == nil {
		r.Forwarded.Add(1)
	}
}

func (r *Reflector) remember(key peerKey, addr net.Addr) {
	r.mu.Lock()
	e := r.peers[key]
	if e == nil {
		e = &peerEntry{}
		r.peers[key] = e
	}
	e.addr = addr
	e.lastSeen = time.Now()
	r.mu.Unlock()
}

func (r *Reflector) expireLoop() {
	ticker := time.NewTicker(r.idleTTL / 2)
	defer ticker.Stop()
	for range ticker.C {
		cutoff := time.Now().Add(-r.idleTTL)
		r.mu.Lock()
		for k, e := range r.peers {
			if e.lastSeen.Before(cutoff) {
				delete(r.peers, k)
			}
		}
		n := len(r.peers)
		r.mu.Unlock()
		if n > 0 {
			log.Printf("reflector: %d peers, %d forwarded, %d hellos, %d dropped", n, r.Forwarded.Load(), r.Hellos.Load(), r.Dropped.Load())
		}
	}
}
