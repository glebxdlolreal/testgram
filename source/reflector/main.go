// Command reflector is Testgram's UDP relay for 1:1 calls (phoneConnection with a peer_tag).
package main

import (
	"log"
	"net"
	"os"
	"time"
)

func main() {
	addr := os.Getenv("REFLECTOR_UDP_ADDR")
	if addr == "" {
		addr = ":599"
	}
	conn, err := net.ListenPacket("udp", addr)
	if err != nil {
		log.Fatalf("listen udp %s: %v", addr, err)
	}
	log.Printf("reflector on udp %s", addr)
	log.Fatal(NewReflector(conn, 2*time.Minute).Serve())
}
