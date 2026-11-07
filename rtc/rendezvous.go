package main

import (
	"context"
	"encoding/json"
	"log"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

// The two PCs meet through public relays they only use to swap a handful of small messages:
// Nostr relays, MQTT brokers, ntfy and a broadcast on the local network. The helper uses all
// of them at once and takes each message from whichever brings it first, so one relay that is
// down or blocked changes nothing. Every message after the SPAKE2 shares is sealed (box.go),
// and the screen itself never touches a relay.

// wireMsg is one message on the relays.
type wireMsg struct {
	V   int    `json:"v"`
	ID  string `json:"id"` // drops the copies that arrive through other relays
	R   string `json:"r"`  // the meeting place
	T   string `json:"t"`  // hello, reply, offer, answer, restart, rans or bye
	F   string `json:"f"`  // who sent it, a random id per session
	To  string `json:"to,omitempty"`
	PA  string `json:"pa,omitempty"` // SPAKE2 share of the PC that views
	PB  string `json:"pb,omitempty"` // SPAKE2 share of the PC that shares
	CA  string `json:"ca,omitempty"` // key confirmations
	CB  string `json:"cb,omitempty"`
	Box string `json:"box,omitempty"` // sealed payload
}

// relay is one way to the meeting place.
type relay interface {
	name() string
	up() bool
	publish(msg []byte) // best effort: callers repeat what matters
	close()
}

// defaultRelays are tried at once. Each kind runs on separate infrastructure, run by
// separate people.
var defaultRelays = []string{
	"nostr+wss://relay.damus.io",
	"nostr+wss://nos.lol",
	"nostr+wss://relay.primal.net",
	"nostr+wss://nostr.mom",
	"mqtt+wss://broker.emqx.io:8084/mqtt",
	"mqtt+wss://broker.hivemq.com:8884/mqtt",
	"ntfy+https://ntfy.sh",
}

var defaultStun = []string{
	"stun:stun.l.google.com:19302",
	"stun:stun.cloudflare.com:3478",
}

type rendezvous struct {
	topic string
	self  string
	in    chan wireMsg
	done  chan struct{}

	mu      sync.Mutex
	relays  []relay
	lan     bool   // the local network relay runs
	waiting []byte // what waits for an answer, for a relay that connects later
	closed  bool
	seen    map[string]time.Time
}

func newRendezvous(room, self string, urls []string, lan bool, d dialer) *rendezvous {
	rv := &rendezvous{
		topic: topicFor(room),
		self:  self,
		in:    make(chan wireMsg, 64),
		done:  make(chan struct{}),
		seen:  map[string]time.Time{},
	}
	for _, u := range urls {
		if r := rv.open(u, d); r != nil {
			rv.relays = append(rv.relays, r)
		}
	}
	if !lan {
		return rv
	}
	// The local network relay waits for other PCs to speak first, which makes Windows ask whether
	// the helper may use the network. While the internet relays work it is not needed, so it only
	// starts when there are none, or none answers within a few seconds.
	if len(rv.relays) == 0 {
		rv.startLAN()
		return rv
	}
	go func() {
		select {
		case <-time.After(3 * time.Second):
			if up, _ := rv.status(); up == 0 {
				rv.startLAN()
			}
		case <-rv.done:
		}
	}()
	return rv
}

// startLAN starts the local network relay, once. A session also starts it when the other PC does
// not show up through the internet relays, since the two may only see each other locally.
func (rv *rendezvous) startLAN() {
	rv.mu.Lock()
	if rv.lan || rv.closed {
		rv.mu.Unlock()
		return
	}
	rv.lan = true
	rv.mu.Unlock()
	r := newLANRelay(rv)
	if r == nil {
		return
	}
	rv.mu.Lock()
	if rv.closed {
		rv.mu.Unlock()
		r.close()
		return
	}
	rv.relays = append(rv.relays, r)
	rv.mu.Unlock()
	log.Printf("relay: the local network joins in")
	rv.relayUp(r)
}

func (rv *rendezvous) open(u string, d dialer) relay {
	scheme, rest, ok := strings.Cut(u, "+")
	if !ok {
		scheme, rest = u, ""
	}
	switch scheme {
	case "nostr":
		return newNostrRelay(rv, rest, d)
	case "mqtt":
		return newMQTTRelay(rv, rest, d)
	case "ntfy":
		return newNtfyRelay(rv, rest, d)
	case "mem":
		return newMemRelay(rv, rest)
	case "none":
		return nil // the local network only
	}
	log.Printf("relay %s: unknown kind", u)
	return nil
}

// deliver takes a raw message from any relay.
func (rv *rendezvous) deliver(raw []byte) {
	var m wireMsg
	if json.Unmarshal(raw, &m) != nil || m.V != 1 || m.R != rv.topic || m.F == rv.self || m.ID == "" {
		return
	}
	now := time.Now()
	rv.mu.Lock()
	if _, dup := rv.seen[m.ID]; dup {
		rv.mu.Unlock()
		return
	}
	rv.seen[m.ID] = now
	if len(rv.seen) > 512 {
		for id, at := range rv.seen {
			if now.Sub(at) > time.Minute {
				delete(rv.seen, id)
			}
		}
	}
	rv.mu.Unlock()
	select {
	case rv.in <- m:
	default: // a flood, the session repeats what matters
	}
}

func (rv *rendezvous) publish(m wireMsg) {
	rv.send(m, false)
}

// ask publishes a message that waits for an answer: a relay that connects later gets it too.
func (rv *rendezvous) ask(m wireMsg) {
	rv.send(m, true)
}

// answered stops giving the last asked message to relays that connect later.
func (rv *rendezvous) answered() {
	rv.mu.Lock()
	rv.waiting = nil
	rv.mu.Unlock()
}

func (rv *rendezvous) send(m wireMsg, wait bool) {
	m.V, m.R, m.F, m.ID = 1, rv.topic, rv.self, randHex(8)
	b, err := json.Marshal(m)
	if err != nil {
		return
	}
	if wait {
		rv.mu.Lock()
		rv.waiting = b
		rv.mu.Unlock()
	}
	for _, r := range rv.list() {
		r.publish(b)
	}
}

func (rv *rendezvous) list() []relay {
	rv.mu.Lock()
	defer rv.mu.Unlock()
	return append([]relay(nil), rv.relays...)
}

// relayUp is called by a relay that just connected: whatever waits for an answer goes to it
// right away, and only to it, since the relays that were up already have it.
func (rv *rendezvous) relayUp(r relay) {
	rv.mu.Lock()
	b := rv.waiting
	rv.mu.Unlock()
	if b != nil {
		r.publish(b)
	}
}

// status is how many relays are connected right now, and how many there are.
func (rv *rendezvous) status() (up, total int) {
	relays := rv.list()
	for _, r := range relays {
		if r.up() {
			up++
		}
	}
	return up, len(relays)
}

func (rv *rendezvous) close() {
	rv.mu.Lock()
	if rv.closed {
		rv.mu.Unlock()
		return
	}
	rv.closed = true
	relays := rv.relays
	rv.mu.Unlock()
	close(rv.done)
	for _, r := range relays {
		r.close()
	}
}

// dialer opens the relay connections: through a proxy, such as PdaNet's own, or from an
// address of this PC, such as the phone's network, so the relays follow the chosen route.
type dialer struct {
	proxy *url.URL
	bind  net.IP
}

func (d dialer) netDialer() *net.Dialer {
	nd := &net.Dialer{Timeout: 8 * time.Second, KeepAlive: 20 * time.Second}
	if d.bind != nil {
		nd.LocalAddr = &net.TCPAddr{IP: d.bind}
	}
	return nd
}

func (d dialer) websocket(ctx context.Context, u string, protocols ...string) (*websocket.Conn, error) {
	wd := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
		Subprotocols:     protocols,
		NetDialContext:   d.netDialer().DialContext,
		ReadBufferSize:   8 << 10,
		WriteBufferSize:  8 << 10,
	}
	if d.proxy != nil {
		wd.Proxy = d.proxyFor
	}
	c, _, err := wd.DialContext(ctx, u, http.Header{"User-Agent": {"PdaNet/" + version}})
	return c, err
}

func (d dialer) httpClient() *http.Client {
	t := &http.Transport{
		DialContext:         d.netDialer().DialContext,
		TLSHandshakeTimeout: 10 * time.Second,
		MaxIdleConns:        2,
		IdleConnTimeout:     30 * time.Second,
	}
	if d.proxy != nil {
		t.Proxy = d.proxyFor
	}
	return &http.Client{Transport: t}
}

// proxyFor is the proxy for one connection: none when the proxy is one on this PC that does not
// answer. PdaNet's own proxy (127.0.0.1:12345) runs only while PdaNet is connected to a phone,
// and a share outlives that: it takes the proxy Windows has when it starts. A share that started
// while PdaNet was connected failed on every relay with "connection refused" once PdaNet
// disconnected, and no PC could reach it (2026-11-07). Each new connection asks again, so the
// relays go through the proxy again when it comes back.
func (d dialer) proxyFor(*http.Request) (*url.URL, error) {
	if d.proxy == nil {
		return nil, nil
	}
	if ip := net.ParseIP(d.proxy.Hostname()); (ip != nil && ip.IsLoopback()) || d.proxy.Hostname() == "localhost" {
		port := d.proxy.Port()
		if port == "" {
			port = "80"
		}
		c, err := net.DialTimeout("tcp", net.JoinHostPort(d.proxy.Hostname(), port), time.Second)
		if err != nil {
			return nil, nil
		}
		_ = c.Close()
	}
	return d.proxy, nil
}

// backoff waits a little longer after each failed connect, up to a limit.
func backoff(ctx context.Context, try int) bool {
	wait := time.Duration(1<<min(try, 4)) * time.Second
	select {
	case <-ctx.Done():
		return false
	case <-time.After(wait):
		return true
	}
}
