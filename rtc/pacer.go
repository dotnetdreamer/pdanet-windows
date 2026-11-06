package main

import (
	"errors"
	"maps"
	"sync"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
)

// burstPacer spreads the screen's packets so they do not hit the link all at once, without
// holding small frames back.
//
// A screen sends in bursts: nothing while it is still, a big frame when a window opens. The
// pacer that comes with GCC sends at exactly the estimate, which makes a keyframe late by
// its size. This one sends at 2.5 times the estimate, like browsers do, and lets a frame of a
// few packets through at once. A packet never waits longer than maxQueueDelay: when the
// estimate lags behind, a late packet is worse than a burst.
//
// The rate is GCC's estimate, or the floor the link meter allows when GCC lags behind what the
// link showed it carries, see peer.target. Each packet that leaves is noted in the meter, which
// times its arrival from the feedback.
type burstPacer struct {
	mu      sync.Mutex
	writers map[uint32]interceptor.RTPWriter
	queue   []pacedPacket
	gcc     int     // bits per second GCC asks for
	floor   int     // bits per second the link meter allows, 0 for none
	rate    float64 // bytes per second
	tokens  float64 // bytes that may go now, below zero means in debt
	last    time.Time
	meter   *linkMeter
	wake    chan struct{}
	done    chan struct{}
	once    sync.Once
}

type pacedPacket struct {
	header  rtp.Header
	payload []byte
	attrs   interceptor.Attributes
	at      time.Time
}

const (
	pacingFactor  = 2.5
	burstTime     = 25 * time.Millisecond
	minBurstBytes = 16 * 1200
	maxQueueDelay = 150 * time.Millisecond
)

var errUnknownSSRC = errors.New("pacer: unknown ssrc")

func newBurstPacer(bps int, meter *linkMeter) *burstPacer {
	p := &burstPacer{
		writers: map[uint32]interceptor.RTPWriter{},
		meter:   meter,
		wake:    make(chan struct{}, 1),
		done:    make(chan struct{}),
		last:    time.Now(),
	}
	p.SetTargetBitrate(bps)
	p.tokens = p.capacity()
	go p.run()
	return p
}

func (p *burstPacer) capacity() float64 {
	return max(p.rate*burstTime.Seconds(), minBurstBytes)
}

// SetTargetBitrate takes GCC's estimate.
func (p *burstPacer) SetTargetBitrate(bps int) {
	p.mu.Lock()
	p.gcc = bps
	p.setRate()
	p.mu.Unlock()
}

// SetFloor takes the rate the link meter allows, 0 for none.
func (p *burstPacer) SetFloor(bps int) {
	p.mu.Lock()
	p.floor = bps
	p.setRate()
	p.mu.Unlock()
}

// setRate paces at the higher of the two. Callers hold mu.
func (p *burstPacer) setRate() {
	p.rate = float64(max(p.gcc, p.floor)) * pacingFactor / 8
}

func (p *burstPacer) AddStream(ssrc uint32, w interceptor.RTPWriter) {
	p.mu.Lock()
	p.writers[ssrc] = w
	p.mu.Unlock()
}

// refill adds the tokens earned since the last call. Callers hold mu.
func (p *burstPacer) refill(now time.Time) {
	p.tokens = min(p.tokens+now.Sub(p.last).Seconds()*p.rate, p.capacity())
	p.last = now
}

func (p *burstPacer) Write(h *rtp.Header, payload []byte, attrs interceptor.Attributes) (int, error) {
	size := h.MarshalSize() + len(payload)
	now := time.Now()
	p.mu.Lock()
	p.refill(now)
	if len(p.queue) == 0 && p.tokens >= 0 {
		p.tokens -= float64(size)
		w := p.writers[h.SSRC]
		p.mu.Unlock()
		if w == nil {
			return 0, errUnknownSSRC
		}
		p.note(h, len(payload))
		return w.Write(h, payload, attrs)
	}
	p.queue = append(p.queue, pacedPacket{
		header:  h.Clone(),
		payload: append([]byte(nil), payload...),
		attrs:   maps.Clone(attrs),
		at:      now,
	})
	p.mu.Unlock()
	select {
	case p.wake <- struct{}{}:
	default:
	}
	return size, nil
}

// note tells the meter a packet leaves now.
func (p *burstPacer) note(h *rtp.Header, payload int) {
	if p.meter != nil {
		p.meter.onSent(h, payload)
	}
}

func (p *burstPacer) run() {
	t := time.NewTicker(2 * time.Millisecond)
	defer t.Stop()
	for {
		p.mu.Lock()
		empty := len(p.queue) == 0
		p.mu.Unlock()
		if empty {
			select {
			case <-p.wake:
			case <-p.done:
				return
			}
		}
		select {
		case <-t.C:
		case <-p.done:
			return
		}
		p.flush()
	}
}

func (p *burstPacer) flush() {
	now := time.Now()
	p.mu.Lock()
	p.refill(now)
	n := 0
	for n < len(p.queue) && (p.tokens >= 0 || now.Sub(p.queue[n].at) > maxQueueDelay) {
		p.tokens -= float64(p.queue[n].header.MarshalSize() + len(p.queue[n].payload))
		n++
	}
	send := make([]pacedPacket, n)
	to := make([]interceptor.RTPWriter, n)
	copy(send, p.queue[:n])
	for i := range send {
		to[i] = p.writers[send[i].header.SSRC]
	}
	p.queue = append(p.queue[:0], p.queue[n:]...)
	p.mu.Unlock()
	for i := range send {
		if to[i] != nil {
			p.note(&send[i].header, len(send[i].payload))
			_, _ = to[i].Write(&send[i].header, send[i].payload, send[i].attrs)
		}
	}
}

func (p *burstPacer) Close() error {
	p.once.Do(func() { close(p.done) })
	return nil
}
