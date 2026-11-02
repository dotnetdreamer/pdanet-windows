package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"strings"
	"sync/atomic"
	"time"
)

// ntfyRelay meets the other PC on ntfy, plain HTTPS on port 443, which also gets out of
// networks that block everything else. Messages are sent with Cache: no, so ntfy keeps none.
type ntfyRelay struct {
	rv     *rendezvous
	base   string
	topic  string
	client *http.Client
	out    chan []byte
	isUp   atomic.Bool
	ctx    context.Context
	cancel context.CancelFunc
}

// ntfy turns longer messages into attachments, the sealed offer stays well below this.
const ntfyMax = 4000

func newNtfyRelay(rv *rendezvous, base string, d dialer) relay {
	r := &ntfyRelay{
		rv:     rv,
		base:   strings.TrimSuffix(base, "/"),
		topic:  "pdanet_" + rv.topic,
		client: d.httpClient(),
		out:    make(chan []byte, 16),
	}
	r.ctx, r.cancel = context.WithCancel(context.Background())
	go r.listen()
	go r.send()
	return r
}

func (r *ntfyRelay) name() string { return r.base }
func (r *ntfyRelay) up() bool     { return r.isUp.Load() }
func (r *ntfyRelay) close()       { r.cancel() }

func (r *ntfyRelay) publish(msg []byte) {
	if !r.isUp.Load() || len(msg) > ntfyMax {
		return
	}
	select {
	case r.out <- msg:
	default:
	}
}

func (r *ntfyRelay) send() {
	for {
		select {
		case <-r.ctx.Done():
			return
		case msg := <-r.out:
			ctx, cancel := context.WithTimeout(r.ctx, 10*time.Second)
			req, _ := http.NewRequestWithContext(ctx, http.MethodPost, r.base+"/"+r.topic, bytes.NewReader(msg))
			req.Header.Set("Cache", "no")
			req.Header.Set("Firebase", "no")
			req.Header.Set("Content-Type", "text/plain")
			if resp, err := r.client.Do(req); err != nil {
				log.Printf("relay %s: %v", r.base, err)
			} else {
				_ = resp.Body.Close()
			}
			cancel()
		}
	}
}

func (r *ntfyRelay) listen() {
	for try := 0; r.ctx.Err() == nil; try++ {
		err := r.subscribe()
		r.isUp.Store(false)
		if r.ctx.Err() != nil {
			return
		}
		log.Printf("relay %s: %v", r.base, err)
		if !backoff(r.ctx, try) {
			return
		}
	}
}

func (r *ntfyRelay) subscribe() error {
	ctx, cancel := context.WithCancel(r.ctx)
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, r.base+"/"+r.topic+"/json", nil)
	resp, err := r.client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("status %s", resp.Status)
	}

	// ntfy sends a keepalive every 45 seconds, silence for longer means the stream is dead.
	watchdog := time.AfterFunc(2*time.Minute, cancel)
	defer watchdog.Stop()
	sc := bufio.NewScanner(resp.Body)
	sc.Buffer(make([]byte, 8<<10), 64<<10)
	for sc.Scan() {
		watchdog.Reset(2 * time.Minute)
		var ev struct {
			Event   string `json:"event"`
			Message string `json:"message"`
		}
		if json.Unmarshal(sc.Bytes(), &ev) != nil {
			continue
		}
		switch ev.Event {
		case "open":
			r.isUp.Store(true)
			r.rv.relayUp()
		case "message":
			r.rv.deliver([]byte(ev.Message))
		}
	}
	if err := sc.Err(); err != nil {
		return err
	}
	return fmt.Errorf("stream ended")
}
