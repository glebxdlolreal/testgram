package main

import (
	"bytes"
	"crypto/rand"
	"encoding/binary"
	"net"
	"testing"
	"time"
)

// client speaks the tgcalls ReflectorPort side of the protocol.
type client struct {
	t      *testing.T
	conn   *net.UDPConn
	tag    [16]byte
	random uint32
}

func newClient(t *testing.T, peerTag []byte) *client {
	t.Helper()
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	c := &client{t: t, conn: conn}
	copy(c.tag[:12], peerTag[:12])
	var r [4]byte
	_, _ = rand.Read(r[:])
	copy(c.tag[12:], r[:])
	// tgcalls copies its random tag in native (little-endian) byte order.
	c.random = binary.LittleEndian.Uint32(r[:])
	return c
}

func pad(b []byte) []byte {
	for len(b)%4 != 0 {
		b = append(b, 0)
	}
	return b
}

func (c *client) hello(server net.Addr) {
	p := append([]byte{}, c.tag[:]...)
	p = append(p, bytes.Repeat([]byte{0xff}, 12)...)
	p = append(p, 0xfe, 0xff, 0xff, 0xff)
	p = binary.BigEndian.AppendUint64(p, 123)
	if _, err := c.conn.WriteTo(pad(p), server); err != nil {
		c.t.Fatal(err)
	}
}

func (c *client) send(server net.Addr, to *client, payload []byte) {
	p := append([]byte{}, c.tag[:12]...)
	p = binary.LittleEndian.AppendUint32(p, to.random)
	p = binary.LittleEndian.AppendUint32(p, c.random)
	p = binary.BigEndian.AppendUint32(p, uint32(len(payload)))
	p = append(p, payload...)
	if _, err := c.conn.WriteTo(pad(p), server); err != nil {
		c.t.Fatal(err)
	}
}

func (c *client) read() []byte {
	c.t.Helper()
	buf := make([]byte, 2048)
	_ = c.conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	n, _, err := c.conn.ReadFrom(buf)
	if err != nil {
		c.t.Fatalf("no packet: %v", err)
	}
	return buf[:n]
}

// expectData parses a packet the way ReflectorPort::HandleIncomingPacket does.
func (c *client) expectData(from *client, payload []byte) {
	c.t.Helper()
	p := c.read()
	if !bytes.Equal(p[:12], c.tag[:12]) {
		c.t.Fatal("packet does not carry the call's peer tag")
	}
	if bytes.Equal(p[16:28], bytes.Repeat([]byte{0xff}, 12)) {
		c.t.Fatal("got a special packet, want data")
	}
	if sender := binary.LittleEndian.Uint32(p[16:20]); sender != from.random {
		c.t.Fatalf("sender tag %d, want %d", sender, from.random)
	}
	size := binary.BigEndian.Uint32(p[20:24])
	if !bytes.Equal(p[24:24+size], payload) {
		c.t.Fatalf("payload %x, want %x", p[24:24+size], payload)
	}
}

func startReflector(t *testing.T) (*Reflector, net.Addr) {
	t.Helper()
	conn, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	r := NewReflector(conn, time.Minute)
	go r.Serve()
	t.Cleanup(func() { conn.Close() })
	return r, conn.LocalAddr()
}

func TestHello_IsAnsweredWithAPacketCarryingThePeerTag(t *testing.T) {
	_, server := startReflector(t)
	peerTag := make([]byte, 16)
	_, _ = rand.Read(peerTag)
	a := newClient(t, peerTag)

	a.hello(server)
	p := a.read()
	if len(p) < 28 || !bytes.Equal(p[:12], peerTag[:12]) || !bytes.Equal(p[16:28], bytes.Repeat([]byte{0xff}, 12)) {
		t.Fatalf("reply %x is not a special packet with the peer tag", p)
	}
}

func TestData_IsRelayedBothWays_BetweenTheTwoCallParticipants(t *testing.T) {
	r, server := startReflector(t)
	peerTag := make([]byte, 16)
	_, _ = rand.Read(peerTag)
	a, b := newClient(t, peerTag), newClient(t, peerTag)
	a.hello(server)
	a.read()
	b.hello(server)
	b.read()

	a.send(server, b, []byte("stun binding request"))
	b.expectData(a, []byte("stun binding request"))
	b.send(server, a, []byte("dtls client hello!"))
	a.expectData(b, []byte("dtls client hello!"))

	if r.Forwarded.Load() != 2 {
		t.Fatalf("forwarded %d, want 2", r.Forwarded.Load())
	}
}

func TestData_FromAnotherCall_IsNotDelivered(t *testing.T) {
	r, server := startReflector(t)
	tagOne := make([]byte, 16)
	tagTwo := make([]byte, 16)
	_, _ = rand.Read(tagOne)
	_, _ = rand.Read(tagTwo)
	a := newClient(t, tagOne)
	intruder := newClient(t, tagTwo)
	a.hello(server)
	a.read()

	// The intruder knows a's random tag but not the call's peer tag.
	intruder.send(server, &client{random: a.random}, []byte("hi"))
	_ = a.conn.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
	if n, _, err := a.conn.ReadFrom(make([]byte, 64)); err == nil {
		t.Fatalf("a received %d bytes from another call", n)
	}
	if r.Forwarded.Load() != 0 {
		t.Fatal("a packet without the call's peer tag was delivered")
	}
}

func TestShortPackets_AreDropped(t *testing.T) {
	r, server := startReflector(t)
	conn, _ := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	defer conn.Close()
	conn.WriteTo([]byte{1, 2, 3}, server)
	conn.WriteTo(make([]byte, 20), server)
	time.Sleep(100 * time.Millisecond)
	if r.Dropped.Load() != 2 {
		t.Fatalf("dropped %d, want 2", r.Dropped.Load())
	}
}
