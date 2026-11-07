package main

import (
	"encoding/binary"
	"math"
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
// times the bursts instead: the pacer lets a burst out faster than a slower link takes it, so how
// long its packets take to arrive is the link's own rate. The best burst of the last ten seconds
// sets a floor under the bitrate while the link keeps no standing queue and loses little, see
// peer.target. A link that really got slower builds a queue that does not drain between bursts,
// which takes the floor away at once.
//
// A burst is timed from when its first packet could have arrived, the moment it left plus the
// lowest delay the link showed, to when its last packet did arrive, see finish. Arrival times
// only ever come late: Wi-Fi hands packets over in batches, a relay or the phone's tunnel holds
// them and lets them go together, a busy PC reads them late. Timed from the first arrival, such
// a batch made a burst look up to 30 times faster than the link (16 to 64 Mbit/s on a relay
// path to a laptop that carried 2, on 2026-11-06, and 90 to 140 Mbit/s on the relay loop of
// one PC, 2026-11-07). Timed so, lateness can only make a burst look slower, never faster. A
// link that lets a burst's first bytes by at once still makes it look faster, so a burst counts
// at no more than its second half shows either, timed by its own arrivals. And however fast the
// bursts look, the estimate at most doubles each growTime.
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
	arrivals  []burstArrival // of the packets of cur that arrived, as the feedback told them

	samples  []rateSample // bursts of the last sampleWindow
	last     rateSample   // the newest burst, kept after the window while nothing newer came
	capacity float64      // bits per second the link carried, 0 while unknown
	grownAt  time.Time    // when capacity was last worked out, see refresh
	target   int          // the bitrate the encoder has now, bounds what one burst may show

	base   [baseBuckets]delayBucket    // the lowest one way delay of each of the last seconds
	queued [queueReadings]queueReading // how far above that lowest delay the latest bursts started
	next   int                         // where the next of those goes
	loss   float64                     // the share of packets lost lately, see lossLocked
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

// burstAcc adds up the feedback of one burst: the packets that arrived, the first of them to
// leave, and the last of them to arrive.
type burstAcc struct {
	id             uint32
	on             bool
	n, lost, bytes int
	firstSize      int
	sendFirst      time.Duration
	arrLast        time.Duration
	minDelay       time.Duration
}

// burstArrival is one packet of a burst that arrived: when, and how large.
type burstArrival struct {
	at   time.Duration
	size int
}

// queueReading is the queue one burst met, and when.
type queueReading struct {
	queue time.Duration
	at    time.Time
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
	minSpan        = 5 * time.Millisecond   // shorter spans are too coarse for those steps, see finish
	growTime       = 500 * time.Millisecond // the estimate at most doubles this often, see refresh
	baseBuckets    = 10                     // the lowest delay over ten one second buckets
	queueReadings  = 64                     // the bursts kept for that
	queueSpan      = 400 * time.Millisecond // a queue counts as standing once every burst this long met it
	queueBursts    = 3                      // and at least this many, over at least half of queueSpan
	lossWindow     = 2 * time.Second        // the loss counts the packets sent this lately
	lossMinPkts    = 10                     // fewer with a known fate say nothing
)

// meterTrace, when set, is told each burst the meter times: the network lab sets it.
var meterTrace func(format string, args ...any)

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

func burstGapFor(target int) time.Duration {
	return max(burstGap, time.Duration(1200*8)*time.Second/time.Duration(max(target, minBitrate)))
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
	// At a low encoder target, a full packet's normal pacing interval exceeds
	// four milliseconds. Leave room for that interval and timer rounding instead
	// of splitting an otherwise measurable train into one-packet bursts.
	gap := burstGapFor(m.target)
	if now-m.lastSend > gap || now-m.burstFrom > burstMax {
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
	if m.cur.on && m.cur.id == m.burst && seq-1 == m.lastSeq && time.Since(m.start)-m.lastSend > burstGapFor(m.target) {
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
		m.arrivals = m.arrivals[:0]
	}
	c := &m.cur
	if !received {
		c.lost++
		return
	}
	m.arrivals = append(m.arrivals, burstArrival{at: arrival, size: p.size})
	delay := arrival - p.at // one way, with an unknown offset between the two clocks
	if c.n == 0 || p.at < c.sendFirst {
		c.sendFirst, c.firstSize = p.at, p.size
	}
	if c.n == 0 {
		c.arrLast, c.minDelay = arrival, delay
	}
	c.arrLast, c.minDelay = max(c.arrLast, arrival), min(c.minDelay, delay)
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
	base, ok := m.baseDelay()
	if ok {
		m.queued[m.next%queueReadings] = queueReading{queue: c.minDelay - base, at: now}
		m.next++
	}
	if !ok || c.n < minSamplePkts || c.bytes < minSampleBytes || c.lost*5 > c.n+c.lost {
		return
	}
	// The bytes after the first packet arrived by the last arrival, and none of them before the
	// first packet could have arrived: it left at sendFirst and nothing crosses the link faster
	// than its lowest delay. So the link carried them at least this fast, and any lateness, a
	// batch, a hold, a queue the burst found, only stretches the span. A burst sent at once
	// counts too: it is the clearest picture of the link, which spreads it out at its own rate.
	// Timed from the first arrival instead, as before, a burst whose first packets a batch held
	// back looked many times faster than the link. That the pacer spread a burst out was then
	// the only defence, and a burst spread out still arrives in batches over Wi-Fi: the relay
	// loop's bursts were then all refused, and the floor never came (2026-11-07).
	span := c.arrLast - c.sendFirst - base
	if span < minSpan {
		return
	}
	bits := float64(c.bytes-c.firstSize) * 8
	bps := bits / span.Seconds()
	// Some links let the first bytes of a burst by at once and the rest at their rate: a token
	// bucket, a router's burst allowance. Timed from the first packet, a frame of 8 or 10 packets
	// sent at once read 3.1 to 3.9 Mbit/s on a 3 Mbit/s link with a 3 KB bucket (the network
	// lab), the floor came to the link's own rate, and each time the link slowed down the queue
	// overflowed. The burst's second half, timed by its own arrivals, comes after any such
	// allowance is spent; late arrivals would make it look faster, which the first rate does not
	// allow for. Each covers what fools the other, the burst counts at the lower of the two,
	// and not at all when the second half came in too quickly to time.
	half, sum := c.bytes/2, 0
	var halfIn time.Duration
	for i, a := range m.arrivals {
		if i == 0 || a.at > halfIn {
			halfIn = a.at
		}
		if sum += a.size; sum >= half {
			break
		}
	}
	tail := c.arrLast - halfIn
	if tail < minSpan || sum >= c.bytes {
		return
	}
	tailBps := float64(c.bytes-sum) * 8 / tail.Seconds()
	if meterTrace != nil {
		meterTrace("burst %d: %d packets %d bytes (%d lost), sent from %v, in by %v, lowest delay %v, span %v: %.0f kbit/s, second half %v: %.0f kbit/s",
			c.id, c.n, c.bytes, c.lost, c.sendFirst, c.arrLast, base, span, bps/1000, tail, tailBps/1000)
	}
	bps = min(bps, tailBps)
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
	// However fast its bursts look, the estimate at most doubles each growTime: a link that
	// really is faster is learned within seconds, while an estimate that something still fools
	// climbs slowly enough for a queue or losses to stop it first. Batches of arrivals once
	// doubled it with each burst, from 2 to 88 Mbit/s in 120 ms. Falling takes no time.
	if m.capacity > 0 && best > m.capacity {
		best = min(best, m.capacity*math.Exp2(float64(now.Sub(m.grownAt))/float64(growTime)))
	}
	m.capacity, m.grownAt = best, now
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
	now := time.Now()
	m.refresh(now)
	return m.capacity, m.standingLocked(now), m.lossLocked()
}

// standingLocked is the queue every burst of the last queueSpan met. A queue that does not
// drain delays every burst, jitter such as Wi-Fi's or a busy relay's delays some bursts for a
// while and not the next ones, and the queue a keyframe leaves drains within it. Read from one
// burst, jitter of up to 80 ms took the floor away every second or two and left the bitrate to
// GCC, which jitter sinks as well: 0.25 Mbit/s on a 3 Mbit/s link that lost nothing (the network
// lab, and the relay sessions of 2026-11-07). Packets that leave alone are bursts of their own,
// so a few in a row say nothing yet: it takes queueBursts over at least half of queueSpan. A
// deep queue counts at once: queueBursts in a row that each met more than twice queueOff, more
// than such jitter adds, are a link that got slower, and waiting would let it lose packets.
// Callers hold mu.
func (m *linkMeter) standingLocked(now time.Time) time.Duration {
	standing, recent, n := time.Duration(math.MaxInt64), time.Duration(math.MaxInt64), 0
	var oldest time.Time
	for i := 1; i <= queueReadings; i++ {
		r := m.queued[(m.next-i+queueReadings*2)%queueReadings]
		if r.at.IsZero() || now.Sub(r.at) > queueSpan {
			break
		}
		standing, oldest, n = min(standing, r.queue), r.at, n+1
		if n == queueBursts {
			recent = standing
		}
	}
	switch {
	case n >= queueBursts && recent > 2*queueOff:
		return recent
	case n < queueBursts || now.Sub(oldest) < queueSpan/2:
		return 0
	}
	return max(standing, 0)
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
