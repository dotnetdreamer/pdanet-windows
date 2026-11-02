package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"log"
	"sync/atomic"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/btcsuite/btcd/btcec/v2/schnorr"
	"github.com/gorilla/websocket"
)

// nostrKind is in the ephemeral range of NIP-01: relays pass such events on to whoever is
// subscribed and keep nothing.
const nostrKind = 25050

type nostrEvent struct {
	ID        string     `json:"id"`
	PubKey    string     `json:"pubkey"`
	CreatedAt int64      `json:"created_at"`
	Kind      int        `json:"kind"`
	Tags      [][]string `json:"tags"`
	Content   string     `json:"content"`
	Sig       string     `json:"sig"`
}

// nostrRelay meets the other PC on a Nostr relay. Each session signs with a throwaway key.
type nostrRelay struct {
	rv     *rendezvous
	url    string
	d      dialer
	key    *btcec.PrivateKey
	pub    string
	tag    string
	out    chan []byte
	isUp   atomic.Bool
	ctx    context.Context
	cancel context.CancelFunc
}

func newNostrRelay(rv *rendezvous, url string, d dialer) relay {
	key, err := btcec.NewPrivateKey()
	if err != nil {
		return nil
	}
	r := &nostrRelay{
		rv:  rv,
		url: url,
		d:   d,
		key: key,
		pub: hex.EncodeToString(schnorr.SerializePubKey(key.PubKey())),
		tag: "pdanet-" + rv.topic,
		out: make(chan []byte, 16),
	}
	r.ctx, r.cancel = context.WithCancel(context.Background())
	go r.run()
	return r
}

func (r *nostrRelay) name() string { return r.url }
func (r *nostrRelay) up() bool     { return r.isUp.Load() }
func (r *nostrRelay) close()       { r.cancel() }

func (r *nostrRelay) publish(msg []byte) {
	if !r.isUp.Load() {
		return
	}
	select {
	case r.out <- msg:
	default:
	}
}

func (r *nostrRelay) run() {
	for try := 0; r.ctx.Err() == nil; try++ {
		err := r.session()
		r.isUp.Store(false)
		if r.ctx.Err() != nil {
			return
		}
		log.Printf("relay %s: %v", r.url, err)
		if !backoff(r.ctx, try) {
			return
		}
	}
}

func (r *nostrRelay) session() error {
	c, err := r.d.websocket(r.ctx, r.url)
	if err != nil {
		return err
	}
	defer c.Close()
	stop := context.AfterFunc(r.ctx, func() { _ = c.Close() })
	defer stop()

	req := []any{"REQ", randHex(6), map[string]any{
		"kinds": []int{nostrKind},
		"#t":    []string{r.tag},
		"since": time.Now().Unix() - 30,
	}}
	if err := c.WriteJSON(req); err != nil {
		return err
	}
	r.isUp.Store(true)
	r.rv.relayUp()

	done := make(chan struct{})
	defer close(done)
	go func() {
		ping := time.NewTicker(25 * time.Second)
		defer ping.Stop()
		for {
			select {
			case msg := <-r.out:
				_ = c.SetWriteDeadline(time.Now().Add(10 * time.Second))
				if err := c.WriteJSON([]any{"EVENT", r.sign(msg)}); err != nil {
					_ = c.Close()
					return
				}
			case <-ping.C:
				_ = c.WriteControl(websocket.PingMessage, nil, time.Now().Add(10*time.Second))
			case <-done:
				return
			}
		}
	}()

	for {
		_ = c.SetReadDeadline(time.Now().Add(90 * time.Second))
		_, data, err := c.ReadMessage()
		if err != nil {
			return err
		}
		var frame []json.RawMessage
		if json.Unmarshal(data, &frame) != nil || len(frame) < 2 {
			continue
		}
		var kind string
		_ = json.Unmarshal(frame[0], &kind)
		switch kind {
		case "EVENT":
			var ev nostrEvent
			if len(frame) < 3 || json.Unmarshal(frame[2], &ev) != nil || ev.PubKey == r.pub || ev.Kind != nostrKind {
				continue
			}
			r.rv.deliver([]byte(ev.Content))
		case "OK":
			var ok bool
			if len(frame) >= 4 && json.Unmarshal(frame[2], &ok) == nil && !ok {
				log.Printf("relay %s refused an event: %s", r.url, frame[3])
			}
		case "CLOSED", "NOTICE":
			log.Printf("relay %s: %s", r.url, data)
		}
	}
}

// sign makes a NIP-01 event: its id is the SHA-256 of [0, pubkey, created_at, kind, tags,
// content] as compact JSON, signed with BIP-340 Schnorr.
func (r *nostrRelay) sign(content []byte) nostrEvent {
	ev := nostrEvent{
		PubKey:    r.pub,
		CreatedAt: time.Now().Unix(),
		Kind:      nostrKind,
		Tags:      [][]string{{"t", r.tag}},
		Content:   string(content),
	}
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	_ = enc.Encode([]any{0, ev.PubKey, ev.CreatedAt, ev.Kind, ev.Tags, ev.Content})
	id := sha256.Sum256(bytes.TrimSuffix(buf.Bytes(), []byte("\n")))
	ev.ID = hex.EncodeToString(id[:])
	if sig, err := schnorr.Sign(r.key, id[:]); err == nil {
		ev.Sig = hex.EncodeToString(sig.Serialize())
	}
	return ev
}
