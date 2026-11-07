package main

import (
	"errors"
	"maps"
	"sync"
	"sync/atomic"
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
// few packets through at once. When the estimate lags behind and packets pile up, it drains the
// pile within drainTime instead of the estimate: never all at once, which a slow link would drop.
//
// Packets the viewer asked for again go before the screen's new ones: the viewer holds every
// frame after a lost packet until the packet comes, and gives up after frameWait. Packets
// leave numbered in the order they leave (the transport wide sequence number): moving a resend
// ahead would otherwise look like loss and reordering in the feedback.
//
// The rate is GCC's estimate, or the floor the link meter allows when GCC lags behind what the
// link showed it carries, see peer.target. Each packet that leaves is noted in the meter, which
// times its arrival from the feedback.
type burstPacer struct {
	mu       sync.Mutex
	writers  map[uint32]interceptor.RTPWriter
	resend   []pacedPacket // asked for again, these go first
	queue    []pacedPacket
	queued   int  // bytes waiting in both
	flushing bool // a batch is on its way out, new packets wait behind it
	newest   map[uint32]uint16
	gcc      int     // bits per second GCC asks for
	floor    int     // bits per second the link meter allows, 0 for none
	linkBps  int     // bits per second the link meter measured the link at, 0 while unknown
	rate     float64 // bytes per second
	most     float64 // bytes per second no burst goes faster than, 0 for no limit
	tokens   float64 // bytes that may go now, below zero means in debt
	last     time.Time
	meter    *linkMeter

	wmu  sync.Mutex    // held while a packet leaves, so the numbers go out in order
	ext  atomic.Uint32 // the header extension of the transport wide sequence number, 0 until known
	twcc uint16        // the next transport wide sequence number

	wake chan struct{}
	done chan struct{}
	once sync.Once
}

type pacedPacket struct {
	header  rtp.Header
	payload []byte
	attrs   interceptor.Attributes
	size    int
	at      time.Time
	resend  resendTracker
}

// resendTracker is the handle Pion's NACK responder passes with a packet it sends again: it
// lets the next resend of that packet wait until this one left.
type resendTracker interface {
	Retain()
	Release(sent bool)
}

const (
	pacingFactor = 2.5
	capacityGain = 1.5                   // of the measured link, the fastest a burst goes once the meter measured it
	netQueue     = 30 * time.Millisecond // of the link, the most a burst should add to the queue in front of it
	burstTime    = 25 * time.Millisecond
	// Idle credit scales with the rate; a fixed 16-packet minimum consumed
	// almost a whole floor-rate keyframe without any measurable pacing and
	// injected hundreds of milliseconds of serialization into slow links.
	minBurstBytes = 1200 // one MTU minimum, plus at most one packet of token debt
	drainTime     = 250 * time.Millisecond
)

var errUnknownSSRC = errors.New("pacer: unknown ssrc")

func newBurstPacer(bps int, meter *linkMeter) *burstPacer {
	p := &burstPacer{
		writers: map[uint32]interceptor.RTPWriter{},
		newest:  map[uint32]uint16{},
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

// setExt names the header extension of the transport wide sequence number, once negotiated.
func (p *burstPacer) setExt(id uint8) {
	p.ext.Store(uint32(id))
}

// SetTargetBitrate takes GCC's estimate.
func (p *burstPacer) SetTargetBitrate(bps int) {
	p.mu.Lock()
	p.gcc = bps
	p.setRate()
	p.mu.Unlock()
}

// SetFloor takes the rate the link meter allows, 0 for none, and the rate it measured the link
// at, 0 while it knows none.
func (p *burstPacer) SetFloor(bps, capacity int) {
	p.mu.Lock()
	p.floor, p.linkBps = bps, capacity
	p.setRate()
	p.mu.Unlock()
}

// setRate paces at the higher of the two, 2.5 times over. Once the link meter measured the link,
// no burst goes faster than capacityGain times what it carries (or GCC's estimate, when that is
// higher): with the floor at 0.8 of the link, 2.5 times over sent bursts at twice what the link
// takes, and a keyframe or a large refine then overflowed the buffer in front of a slow hop. The
// lost packets were asked for again behind the rest of the burst, the frame expired, and the
// keyframe asked for next did the same (the network lab, 2026-11-07). A little faster than the
// link still lets the meter see the link get faster. Callers hold mu.
func (p *burstPacer) setRate() {
	p.rate = float64(max(p.gcc, p.floor)) * pacingFactor / 8
	p.most = 0
	if p.linkBps > 0 {
		p.most = float64(max(p.linkBps, p.gcc)) * capacityGain / 8
		p.rate = min(p.rate, p.most)
	}
}

// limit is how fast what waits may leave once the meter measured the link: capacityGain times
// the link while little waits, and closer to the link the more waits, so that a burst adds no
// more than netQueue of the link's time to the queue in front of the slowest hop. A burst of S
// bytes sent at g times the link leaves S(1-1/g) there: a 900 KB keyframe at 1.5 times a
// 20 Mbit/s link left 300 KB, more than its router kept, and the frame's last packets were
// lost; paced near the link it waits here instead, and arrives no later. Callers hold mu.
func (p *burstPacer) limit() float64 {
	link := p.most / capacityGain
	gain := capacityGain
	if room := link * netQueue.Seconds(); float64(p.queued) > room {
		gain = min(gain, 1/(1-room/float64(p.queued)))
	}
	return link * gain // never below the link itself: gain stays above 1
}

func (p *burstPacer) AddStream(ssrc uint32, w interceptor.RTPWriter) {
	p.mu.Lock()
	p.writers[ssrc] = w
	p.mu.Unlock()
}

// refill adds the tokens earned since the last call, faster while a pile waits: fast enough that
// the oldest packet leaves within drainTime of when it came. Callers hold mu.
func (p *burstPacer) refill(now time.Time) {
	rate := p.rate
	if p.queued > 0 {
		oldest := now
		if len(p.resend) > 0 {
			oldest = p.resend[0].at
		}
		if len(p.queue) > 0 && p.queue[0].at.Before(oldest) {
			oldest = p.queue[0].at
		}
		left := max(drainTime-now.Sub(oldest), 50*time.Millisecond)
		rate = max(rate, float64(p.queued)/left.Seconds())
		if p.most > 0 {
			rate = min(rate, p.limit())
		}
	}
	p.tokens = min(p.tokens+now.Sub(p.last).Seconds()*rate, max(p.capacity(), rate*burstTime.Seconds()))
	p.last = now
}

// isResend tells a packet sent again: Pion's responder hands it with a resendTracker, and its
// sequence number is not newer than the newest of its stream. Callers hold mu.
func (p *burstPacer) isResend(h *rtp.Header, tr resendTracker) bool {
	newest, ok := p.newest[h.SSRC]
	if tr != nil || (ok && int16(h.SequenceNumber-newest) <= 0) {
		return true
	}
	p.newest[h.SSRC] = h.SequenceNumber
	return false
}

// trackerOf finds the responder's handle among the attributes, whose key type Pion keeps to itself.
func trackerOf(attrs interceptor.Attributes) resendTracker {
	for _, v := range attrs {
		if tr, ok := v.(resendTracker); ok {
			return tr
		}
	}
	return nil
}

func (p *burstPacer) Write(h *rtp.Header, payload []byte, attrs interceptor.Attributes) (int, error) {
	size := h.MarshalSize() + len(payload)
	tr := trackerOf(attrs)
	now := time.Now()
	p.mu.Lock()
	again := p.isResend(h, tr)
	p.refill(now)
	if !p.flushing && len(p.queue) == 0 && len(p.resend) == 0 && p.tokens >= 0 {
		p.tokens -= float64(size)
		w := p.writers[h.SSRC]
		p.mu.Unlock()
		if w == nil {
			return 0, errUnknownSSRC
		}
		return p.send(w, h, payload, attrs)
	}
	pk := pacedPacket{
		header:  h.Clone(),
		payload: append([]byte(nil), payload...),
		attrs:   maps.Clone(attrs),
		size:    size,
		at:      now,
	}
	if tr != nil {
		tr.Retain() // the responder lets go when Write returns, the packet has not left yet
		pk.resend = tr
	}
	if again {
		p.resend = append(p.resend, pk)
	} else {
		p.queue = append(p.queue, pk)
	}
	p.queued += size
	p.mu.Unlock()
	select {
	case p.wake <- struct{}{}:
	default:
	}
	return size, nil
}

// send writes one packet with the next transport wide sequence number, and notes it in the meter.
func (p *burstPacer) send(w interceptor.RTPWriter, h *rtp.Header, payload []byte, attrs interceptor.Attributes) (int, error) {
	p.wmu.Lock()
	defer p.wmu.Unlock()
	if ext := uint8(p.ext.Load()); ext != 0 && len(h.GetExtension(ext)) >= 2 {
		_ = h.SetExtension(ext, []byte{byte(p.twcc >> 8), byte(p.twcc)})
		p.twcc++
	}
	if p.meter != nil {
		p.meter.onSent(h, len(payload))
	}
	return w.Write(h, payload, attrs)
}

func (p *burstPacer) run() {
	t := time.NewTicker(2 * time.Millisecond)
	defer t.Stop()
	for {
		p.mu.Lock()
		empty := len(p.queue) == 0 && len(p.resend) == 0
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
	var batch []pacedPacket
	nr, nq := 0, 0
	for p.tokens >= 0 {
		var pk pacedPacket
		switch {
		case nr < len(p.resend):
			pk = p.resend[nr]
			nr++
		case nq < len(p.queue):
			pk = p.queue[nq]
			nq++
		}
		if pk.size == 0 {
			break
		}
		p.tokens -= float64(pk.size)
		p.queued -= pk.size
		batch = append(batch, pk)
	}
	p.resend = append(p.resend[:0], p.resend[nr:]...)
	p.queue = append(p.queue[:0], p.queue[nq:]...)
	if len(batch) == 0 {
		p.mu.Unlock()
		return
	}
	to := make([]interceptor.RTPWriter, len(batch))
	for i := range batch {
		to[i] = p.writers[batch[i].header.SSRC]
	}
	p.flushing = true
	p.mu.Unlock()
	for i := range batch {
		sent := false
		if to[i] != nil {
			_, err := p.send(to[i], &batch[i].header, batch[i].payload, batch[i].attrs)
			sent = err == nil
		}
		if batch[i].resend != nil {
			batch[i].resend.Release(sent)
		}
	}
	p.mu.Lock()
	p.flushing = false
	p.mu.Unlock()
}

func (p *burstPacer) Close() error {
	p.once.Do(func() { close(p.done) })
	return nil
}
