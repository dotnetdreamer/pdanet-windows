package main

import (
	"encoding/binary"
	"sync"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
)

// linkMeter measures what the link between the two PCs really carries, on the PC that shares,
// from the feedback the PC that views sends about every 100 ms: when each packet of the screen
// arrived there (transport wide congestion control).
//
// Google Congestion Control sets the bitrate, and a screen trips it up. Frames come in bursts
// with pauses between, and when the delay rises GCC cuts to 85 % of the rate that arrived over
// the last half second, a span that then holds mostly a pause. The estimate falls far below what
// the link carries and climbs back by 8 % a second, while the picture turns to mush. The meter
// times the bursts instead: the pacer lets a burst out faster than a slower link takes it, so the
// spacing of its packets on arrival is the link's own rate. The best burst of the last ten
// seconds sets a floor under the bitrate while the link keeps no standing queue and loses little,
// see peer.target. A link that really got slower builds a queue that does not drain between
// bursts, which takes the floor away at once.
type linkMeter struct {
	mu    sync.Mutex
	start time.Time
	ext   uint8 // the header extension with the transport wide sequence number, 0 until known

	sent      [sentRing]sentPacket
	lastSend  time.Duration
	lastSeq   uint16
	burst     uint32
	burstFrom time.Duration
	cur       burstAcc

	samples  []rateSample // bursts of the last sampleWindow
	last     rateSample   // the newest burst, kept after the window while nothing newer came
	capacity float64      // bits per second the link carried, 0 while unknown
	target   int          // the bitrate the encoder has now, bounds what one burst may show

	base     [baseBuckets]delayBucket // the lowest one way delay of each of the last seconds
	standing time.Duration            // how far above that lowest delay the latest burst started
	standAt  time.Time
	loss     float64 // the share of packets lost lately, see lossLocked
}

type sentPacket struct {
	seq   uint16
	ok    bool
	at    time.Duration // since the meter started
	size  int
	burst uint32
	fate  byte // what the feedback said of it: 0 nothing yet, fateReceived or fateLost
}

const (
	fateReceived byte = 1
	fateLost     byte = 2
)

// burstAcc adds up the feedback of one burst.
type burstAcc struct {
	id                  uint32
	on                  bool
	n, lost, bytes      int
	firstSize           int
	sendFirst, sendLast time.Duration
	arrFirst, arrLast   time.Duration
	minDelay            time.Duration
	prevSend, prevArr   time.Duration // the packet before, by sequence number
	bunched             int           // packets in the same arrival tick as the one before, though sent apart
}

type rateSample struct {
	at  time.Time
	bps float64
}

type delayBucket struct {
	second int64
	set    bool
	min    time.Duration
}

const (
	sentRing       = 8192                   // packets remembered until their feedback comes
	burstGap       = 4 * time.Millisecond   // packets that leave closer together belong to one burst
	burstMax       = 250 * time.Millisecond // a longer run is cut, so a busy link still gets samples
	sampleWindow   = 10 * time.Second       // the floor follows the best burst of this long
	sampleKeep     = 60 * time.Second       // a resting screen keeps its last burst this long
	minSamplePkts  = 6
	minSampleBytes = 8000
	arrivalTick    = 250 * time.Microsecond // the feedback tells arrivals in these steps
	minArrSpan     = 2 * time.Millisecond   // shorter spans are too coarse for those steps
	minSendSpan    = 2 * time.Millisecond   // a burst must have been spread out by the pacer, see finish
	baseBuckets    = 10                     // the lowest delay over ten one second buckets
	standingKeep   = 2 * time.Second        // a queue no burst measured again for this long counts as drained
	lossWindow     = 2 * time.Second        // the loss counts the packets sent this lately
	lossMinPkts    = 10                     // fewer with a known fate say nothing
)

func newLinkMeter() *linkMeter {
	return &linkMeter{start: time.Now()}
}

// setExt names the header extension of the transport wide sequence number, once negotiated.
func (m *linkMeter) setExt(id uint8) {
	m.mu.Lock()
	m.ext = id
	m.mu.Unlock()
}

// setTarget tells the meter the bitrate the encoder has now.
func (m *linkMeter) setTarget(bps int) {
	m.mu.Lock()
	m.target = bps
	m.mu.Unlock()
}

// onSent notes a packet as it leaves the pacer for the network.
func (m *linkMeter) onSent(h *rtp.Header, payload int) {
	m.onSentAt(h, payload, time.Since(m.start))
}

// onSentAt notes a packet that left at now, counted from when the meter started.
func (m *linkMeter) onSentAt(h *rtp.Header, payload int, now time.Duration) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.ext == 0 {
		return
	}
	b := h.GetExtension(m.ext)
	if len(b) < 2 {
		return
	}
	seq := binary.BigEndian.Uint16(b)
	if now-m.lastSend > burstGap || now-m.burstFrom > burstMax {
		m.burst++
		m.burstFrom = now
	}
	m.lastSend, m.lastSeq = now, seq
	m.sent[seq%sentRing] = sentPacket{seq: seq, ok: true, at: now, size: h.MarshalSize() + payload, burst: m.burst}
}

// onFeedback reads one feedback packet: which packets arrived, and when.
func (m *linkMeter) onFeedback(fb *rtcp.TransportLayerCC) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.ext == 0 {
		return
	}
	// The first arrival counts from the reference time, in steps of 64 ms, each later one from
	// the arrival before it.
	arrival := time.Duration(fb.ReferenceTime) * 64 * time.Millisecond
	deltas := fb.RecvDeltas
	seq := fb.BaseSequenceNumber
	left := int(fb.PacketStatusCount)
	// A packet can be in two feedbacks: Pion's recorder says again what it said before when a
	// late packet comes in. Each counts once, and a late arrival turns a loss into an arrival.
	visit := func(symbol uint16) {
		slot := &m.sent[seq%sentRing]
		p := *slot
		known := p.ok && p.seq == seq
		switch symbol {
		case rtcp.TypeTCCPacketReceivedSmallDelta, rtcp.TypeTCCPacketReceivedLargeDelta:
			if len(deltas) > 0 {
				arrival += time.Duration(deltas[0].Delta) * time.Microsecond
				deltas = deltas[1:]
			}
			if known && p.fate != fateReceived {
				if p.fate == 0 {
					m.add(p, arrival, true)
				}
				slot.fate = fateReceived
			}
		case rtcp.TypeTCCPacketNotReceived:
			if known && p.fate == 0 {
				slot.fate = fateLost
				m.add(p, 0, false)
			}
		}
		seq++
		left--
	}
	for _, chunk := range fb.PacketChunks {
		switch c := chunk.(type) {
		case *rtcp.RunLengthChunk:
			for i := 0; i < int(c.RunLength) && left > 0; i++ {
				visit(c.PacketStatusSymbol)
			}
		case *rtcp.StatusVectorChunk:
			for _, s := range c.SymbolList {
				if left <= 0 {
					break
				}
				visit(s)
			}
		}
	}
	// The newest burst is whole once the last packet sent is in and nothing followed it.
	if m.cur.on && m.cur.id == m.burst && seq-1 == m.lastSeq && time.Since(m.start)-m.lastSend > burstGap {
		m.finish()
	}
}

// add counts a packet into the burst it left with. A packet of a newer burst finishes the one before.
func (m *linkMeter) add(p sentPacket, arrival time.Duration, received bool) {
	if !m.cur.on || p.burst != m.cur.id {
		if m.cur.on && p.burst < m.cur.id {
			return // reported late, its burst is done
		}
		m.finish()
		m.cur = burstAcc{id: p.burst, on: true}
	}
	c := &m.cur
	if !received {
		c.lost++
		return
	}
	delay := arrival - p.at // one way, with an unknown offset between the two clocks
	if c.n == 0 {
		c.firstSize = p.size
		c.sendFirst, c.sendLast = p.at, p.at
		c.arrFirst, c.arrLast = arrival, arrival
		c.minDelay = delay
	} else {
		c.sendFirst, c.sendLast = min(c.sendFirst, p.at), max(c.sendLast, p.at)
		c.arrFirst, c.arrLast = min(c.arrFirst, arrival), max(c.arrLast, arrival)
		c.minDelay = min(c.minDelay, delay)
		if arrival-c.prevArr < arrivalTick && p.at-c.prevSend >= 2*arrivalTick {
			c.bunched++
		}
	}
	c.prevSend, c.prevArr = p.at, arrival
	c.n++
	c.bytes += p.size
	m.noteDelay(p.at, delay)
}

// finish turns the burst that is done into a sample of what the link carries, and of the queue
// it met.
func (m *linkMeter) finish() {
	c := m.cur
	m.cur.on = false
	if !c.on || c.n == 0 {
		return
	}
	now := time.Now()
	// The first packets of a burst find the queue the bursts before left: none on a link that
	// keeps up, a standing one on a link that gets more than it carries.
	if base, ok := m.baseDelay(); ok {
		m.standing, m.standAt = c.minDelay-base, now
	}
	if c.n < minSamplePkts || c.bytes < minSampleBytes || c.lost*5 > c.n+c.lost {
		return
	}
	// Packets the pacer sent apart that arrive in the same tick came out of a batch, not off the
	// link: the phone helper's tunnel on the PC that views hands them over that way, and so does
	// the one on the PC that shares in the phone lab. Their spacing says nothing.
	if c.bunched*4 > c.n {
		return
	}
	// Only a burst the pacer spread out tells what the link carries, and never more than it was
	// sent at. A burst that left at once can also arrive at once: the phone helper's tunnel hands
	// datagrams over in batches, which made bursts look like 20 to 75 Mbit/s on a link that
	// carried half a megabit (2026-11-06). A link spreads a burst out or keeps its spacing; one
	// that arrives closer together than it left was held up and let go at once, by the phone's
	// tunnel or a queue, and says nothing of the link either: those made the floor double the
	// bitrate again and again until a queue stopped it (2026-11-06, the phone lab).
	span, sendSpan := c.arrLast-c.arrFirst, c.sendLast-c.sendFirst
	if span < minArrSpan || sendSpan < minSendSpan || span < sendSpan*4/5 {
		return
	}
	bits := float64(c.bytes-c.firstSize) * 8
	bps := min(bits/span.Seconds(), bits/sendSpan.Seconds())
	// One odd burst moves the estimate a step, not a mile: at most twice what the meter or the
	// encoder has now, also right after a queue made the meter forget its bursts.
	bps = min(bps, max(2*m.capacity, 2*float64(m.target), 2e6))
	m.last = rateSample{at: now, bps: bps}
	m.samples = append(m.samples, m.last)
	m.refresh(now)
}

// refresh drops samples past the window and takes the best of the rest.
func (m *linkMeter) refresh(now time.Time) {
	keep := m.samples[:0]
	best := 0.0
	for _, s := range m.samples {
		if now.Sub(s.at) <= sampleWindow {
			keep = append(keep, s)
			best = max(best, s.bps)
		}
	}
	m.samples = keep
	if best == 0 && m.last.bps > 0 && now.Sub(m.last.at) <= sampleKeep {
		best = m.last.bps // a resting screen sends nothing to measure, the link likely still carries it
	}
	m.capacity = best
}

func (m *linkMeter) noteDelay(at, delay time.Duration) {
	sec := int64(at / time.Second)
	b := &m.base[sec%baseBuckets]
	if !b.set || b.second != sec {
		*b = delayBucket{second: sec, set: true, min: delay}
	} else if delay < b.min {
		b.min = delay
	}
}

// baseDelay is the lowest one way delay of the last ten seconds: the link with nothing queued.
func (m *linkMeter) baseDelay() (time.Duration, bool) {
	now := int64(time.Since(m.start) / time.Second)
	var low time.Duration
	ok := false
	for _, b := range m.base {
		if b.set && now-b.second < baseBuckets && (!ok || b.min < low) {
			low, ok = b.min, true
		}
	}
	return low, ok
}

// snapshot is what the meter knows now: the bits per second the link carried, the queue that did
// not drain, and the share of packets lost lately.
func (m *linkMeter) snapshot() (capacity float64, standing time.Duration, loss float64) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.refresh(time.Now())
	if time.Since(m.standAt) <= standingKeep {
		standing = max(standing, m.standing)
	}
	return m.capacity, standing, m.lossLocked()
}

// lossLocked is the share lost of the packets sent in the last lossWindow whose fate the
// feedback told, or the last share while too few are known. Callers hold mu.
func (m *linkMeter) lossLocked() float64 {
	now := time.Since(m.start)
	got, lost := 0, 0
	seq := m.lastSeq
	for range sentRing {
		p := m.sent[seq%sentRing]
		if !p.ok || p.seq != seq || now-p.at > lossWindow {
			break
		}
		switch p.fate {
		case fateReceived:
			got++
		case fateLost:
			lost++
		}
		seq--
	}
	if got+lost >= lossMinPkts {
		m.loss = float64(lost) / float64(got+lost)
	}
	return m.loss
}

// congested forgets the bursts measured so far: the link got slower than they showed.
func (m *linkMeter) congested() {
	m.mu.Lock()
	m.samples = m.samples[:0]
	m.last = rateSample{}
	m.capacity = 0
	m.mu.Unlock()
}
