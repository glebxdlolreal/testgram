// Command sfu is Testgram's selective forwarding unit for group calls: it
// terminates ICE and DTLS-SRTP with each participant and forwards RTP by SSRC,
// speaking Telegram's Jingle-style join JSON on the messenger side.
package main

import (
	"encoding/json"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
)

func main() {
	udpAddr := env("SFU_UDP_ADDR", ":32000")
	httpAddr := env("SFU_HTTP_ADDR", ":8090")
	publicIPs := splitNonEmpty(os.Getenv("SFU_PUBLIC_IP"))
	if len(publicIPs) == 0 {
		log.Fatal("SFU_PUBLIC_IP is required: the address clients reach the SFU's UDP port on")
	}

	conn, err := net.ListenPacket("udp4", udpAddr)
	if err != nil {
		log.Fatalf("listen udp %s: %v", udpAddr, err)
	}
	sfu, err := NewSFU(Config{PublicIPs: publicIPs, UDP: conn})
	if err != nil {
		log.Fatal(err)
	}
	log.Printf("SFU media on udp %s (advertised as %v), API on %s", udpAddr, publicIPs, httpAddr)
	log.Fatal(http.ListenAndServe(httpAddr, NewAPIHandler(sfu)))
}

// NewAPIHandler is the messenger-facing API (internal network only):
//
//	POST   /v1/calls/{call}/participants/{key}  {"params": "<join params JSON>"} -> {"transport": {...}}
//	DELETE /v1/calls/{call}/participants/{key}
//	DELETE /v1/calls/{call}
//	GET    /v1/calls/{call}/sources -> {"sources": [...]}
//	GET    /v1/calls/{call}/stats
func NewAPIHandler(s *SFU) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { w.Write([]byte("ok")) })
	mux.HandleFunc("POST /v1/calls/{call}/participants/{key}", func(w http.ResponseWriter, r *http.Request) {
		callID, ok := callIDOf(w, r)
		if !ok {
			return
		}
		var body struct {
			Params string `json:"params"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			httpError(w, http.StatusBadRequest, "body must be {\"params\": \"...\"}")
			return
		}
		params, err := ParseJoinParams(body.Params)
		if err != nil {
			httpError(w, http.StatusBadRequest, err.Error())
			return
		}
		transport, err := s.Join(callID, r.PathValue("key"), params)
		if err != nil {
			httpError(w, http.StatusUnprocessableEntity, err.Error())
			return
		}
		writeJSON(w, map[string]any{"transport": transport})
	})
	mux.HandleFunc("DELETE /v1/calls/{call}/participants/{key}", func(w http.ResponseWriter, r *http.Request) {
		if callID, ok := callIDOf(w, r); ok {
			writeJSON(w, map[string]any{"removed": s.Leave(callID, r.PathValue("key"))})
		}
	})
	mux.HandleFunc("DELETE /v1/calls/{call}", func(w http.ResponseWriter, r *http.Request) {
		if callID, ok := callIDOf(w, r); ok {
			s.End(callID)
			writeJSON(w, map[string]any{"ended": true})
		}
	})
	mux.HandleFunc("GET /v1/calls/{call}/sources", func(w http.ResponseWriter, r *http.Request) {
		if callID, ok := callIDOf(w, r); ok {
			writeJSON(w, map[string]any{"sources": s.Sources(callID)})
		}
	})
	mux.HandleFunc("GET /v1/calls/{call}/stats", func(w http.ResponseWriter, r *http.Request) {
		if callID, ok := callIDOf(w, r); ok {
			writeJSON(w, map[string]any{"participants": s.Stats(callID)})
		}
	})
	return mux
}

func callIDOf(w http.ResponseWriter, r *http.Request) (int64, bool) {
	id, err := strconv.ParseInt(r.PathValue("call"), 10, 64)
	if err != nil {
		httpError(w, http.StatusBadRequest, "call id must be an integer")
		return 0, false
	}
	return id, true
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(v)
}

func httpError(w http.ResponseWriter, status int, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]string{"error": msg})
}

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func splitNonEmpty(s string) []string {
	var out []string
	for _, part := range strings.Split(s, ",") {
		if part = strings.TrimSpace(part); part != "" {
			out = append(out, part)
		}
	}
	return out
}
