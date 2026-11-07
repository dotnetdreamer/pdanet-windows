package main

import (
	"encoding/binary"
	"math"
	"testing"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
)

const testExt = 5

// sendBurst notes n packets of 1200 bytes each as they leave, sequence numbers from seq on, the
// first at the meter's time at and each next one gap later.
func sendBurst(m *linkMeter, seq uint16, n int, at, gap time.Duration) {
	for i := 0; i < n; i++ {
		h := &rtp.Header{Version: 2, SSRC: 1, SequenceNumber: seq + uint16(i)}
		var b [2]byte
		binary.BigEndian.PutUint16(b[:], seq+uint16(i))
		if err := h.SetExtension(testExt, b[:]); err != nil {
			panic(err)
		}
		m.onSentAt(h, 1200-h.MarshalSize(), at+time.Duration(i)*gap)
	}
}

// settle waits until the meter's clock is past the last packet sent, so a feedback that brings
// the last packet in finishes the burst.
func settle(m *linkMeter) {
	for time.Since(m.start) <= m.lastSend+2*burstGapFor(m.target) {
		time.Sleep(time.Millisecond)
	}
}

// feedback says packets seq..seq+n-1 arrived, the first at ref*64 ms plus first, each next
// one gap later, except those in lost.
func feedback(seq uint16, n int, ref uint32, first, gap time.Duration, lost map[int]bool) *rtcp.TransportLayerCC {
	fb := &rtcp.TransportLayerCC{BaseSequenceNumber: seq, PacketStatusCount: uint16(n), ReferenceTime: ref}
	var symbols []uint16
	prev := time.Duration(0)
	for i := 0; i < n; i++ {
		if lost[i] {
			symbols = append(symbols, rtcp.TypeTCCPacketNotReceived)
			continue
		}
		at := first + time.Duration(i)*gap
		symbols = append(symbols, rtcp.TypeTCCPacketReceivedLargeDelta)
		fb.RecvDeltas = append(fb.RecvDeltas, &rtcp.RecvDelta{Type: rtcp.TypeTCCPacketReceivedLargeDelta, Delta: int64((at - prev) / time.Microsecond)})
		prev = at
	}
	for len(symbols) > 0 {
		k := min(7, len(symbols))
		list := append([]uint16(nil), symbols[:k]...)
		for len(list) < 7 {
			list = append(list, rtcp.TypeTCCPacketNotReceived)
		}
		fb.PacketChunks = append(fb.PacketChunks, &rtcp.StatusVectorChunk{SymbolSize: rtcp.TypeTCCSymbolSizeTwoBit, SymbolList: list})
		symbols = symbols[k:]
	}
	return fb
}

func newTestMeter() *linkMeter {
	m := newLinkMeter()
	m.setExt(testExt)
	m.setTarget(8_000_000)
	return m
}

const paced = 500 * time.Microsecond // the pacer's spacing in the tests: 19.2 Mbit/s

// A burst the pacer spread out that arrives 1 ms per packet of 1200 bytes shows a link of 9.6 Mbit/s.
func TestBurstShowsTheLinkRate(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 100, 20, 0, paced)
	settle(m)
	m.onFeedback(feedback(100, 20, 10, 0, time.Millisecond, nil))
	capacity, _, loss := m.snapshot()
	if capacity < 9.4e6 || capacity > 9.8e6 {
		t.Fatalf("capacity %.0f, want about 9.6 Mbit/s", capacity)
	}
	if loss != 0 {
		t.Fatalf("loss %v, want 0", loss)
	}
}

// The feedback of one burst may come in two packets: it is timed as one.
func TestBurstInTwoFeedbacks(t *testing.T) {
	m := newTestMeter()
	base := uint16(65530) // the sequence numbers wrap on the way
	sendBurst(m, base, 20, 0, paced)
	settle(m)
	m.onFeedback(feedback(base, 10, 10, 0, time.Millisecond, nil))
	m.onFeedback(feedback(base+10, 10, 10, 10*time.Millisecond, time.Millisecond, nil))
	if capacity, _, _ := m.snapshot(); capacity < 9.4e6 || capacity > 9.8e6 {
		t.Fatalf("capacity %.0f, want about 9.6 Mbit/s", capacity)
	}
}

// A small burst says nothing of the link, nor does one that came and went too quickly to time
// in the feedback's steps, nor one that lost more than a fifth of its packets.
func TestBurstsThatSayNothing(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 3, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 3, 10, 0, time.Millisecond, nil))
	sendBurst(m, 4, 20, 20*time.Millisecond, 0)
	settle(m)
	m.onFeedback(feedback(4, 20, 10, 20*time.Millisecond, 100*time.Microsecond, nil)) // all in within 2 ms
	sendBurst(m, 24, 20, 60*time.Millisecond, paced)
	settle(m)
	lost := map[int]bool{}
	for i := 0; i < 20; i += 4 {
		lost[i], lost[i+1] = true, true
	}
	m.onFeedback(feedback(24, 20, 10, 60*time.Millisecond, time.Millisecond, lost))
	if capacity, _, _ := m.snapshot(); capacity != 0 {
		t.Fatalf("capacity %.0f, want none", capacity)
	}
}

// arrive tells the meter that packets seq on arrived at these times, all in one feedback.
func arrive(m *linkMeter, seq uint16, arrivals []time.Duration) {
	fb := feedback(seq, len(arrivals), 10, 0, 0, nil)
	prev := time.Duration(0)
	for i, at := range arrivals {
		fb.RecvDeltas[i] = &rtcp.RecvDelta{Type: rtcp.TypeTCCPacketReceivedLargeDelta, Delta: int64((at - prev) / time.Microsecond)}
		prev = at
	}
	m.onFeedback(fb)
}

// Arrivals only ever come late: Wi-Fi, a relay or a tunnel holds packets and hands them over in
// batches, a busy PC reads them late. A burst of 16 packets sent at once crosses a 3 Mbit/s link
// in 51 ms. Read in batches every 30 ms it looked like 4.8 Mbit/s timed from its first arrival.
// Timed from when its first packet could have arrived, the link's lowest delay after it left, it
// is never faster than the link. Handed over all at once, its second half cannot be timed, and it
// says nothing.
func TestBatchedArrivalsNeverLookFaster(t *testing.T) {
	for _, tc := range []struct {
		name  string
		batch func(time.Duration) time.Duration // when a packet that came off the link at d is read
		timed bool
	}{
		{"read every 30 ms", func(d time.Duration) time.Duration { return (d/(30*time.Millisecond) + 1) * 30 * time.Millisecond }, true},
		{"handed over at once", func(time.Duration) time.Duration { return 50 * time.Millisecond }, false},
		{"read when it came", func(d time.Duration) time.Duration { return d }, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			m := newTestMeter()
			// A packet before that the link brought straight in: the link's lowest delay.
			sendBurst(m, 1, 1, 0, 0)
			arrive(m, 1, []time.Duration{0})
			at := 100 * time.Millisecond
			sendBurst(m, 2, 16, at, 0)
			settle(m)
			arrivals := make([]time.Duration, 16)
			for i := range arrivals {
				arrivals[i] = at + tc.batch(time.Duration(i)*3200*time.Microsecond) // 1200 bytes at 3 Mbit/s each
			}
			arrive(m, 2, arrivals)
			capacity, _, _ := m.snapshot()
			if (capacity > 0) != tc.timed || capacity > 3.01e6 {
				t.Fatalf("capacity %.0f, want a reading %v, and never above the 3 Mbit/s link", capacity, tc.timed)
			}
		})
	}
}

// Some links let the first bytes of a burst by at once, a token bucket: 3 KB of a frame of 8
// packets sent at once came in together, the rest at the link's 3 Mbit/s. Timed from its first
// packet that frame read 3.9 Mbit/s and set the floor at the link's own rate. Its second half
// shows the link.
func TestBucketBurstShowsTheLink(t *testing.T) {
	m := newTestMeter()
	m.setTarget(3_000_000)
	sendBurst(m, 1, 8, 0, 0)
	settle(m)
	arrivals := make([]time.Duration, 8)
	for i := range arrivals {
		arrivals[i] = time.Duration(max(i-2, 0)) * 3200 * time.Microsecond // 2.5 packets in the bucket
	}
	arrive(m, 1, arrivals)
	if capacity, _, _ := m.snapshot(); capacity < 2.9e6 || capacity > 3.05e6 {
		t.Fatalf("capacity %.0f, want the 3 Mbit/s link", capacity)
	}
}

// A burst that left at once is the clearest picture of the link, which spreads it out at its own
// rate: the pacer lets an idle stream's first packets go together.
func TestBurstSentAtOnceShowsTheLink(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 16, 0, 0)
	settle(m)
	m.onFeedback(feedback(1, 16, 10, 0, time.Millisecond, nil))
	if capacity, _, _ := m.snapshot(); capacity < 9.4e6 || capacity > 9.8e6 {
		t.Fatalf("capacity %.0f, want about 9.6 Mbit/s", capacity)
	}
}

// A burst that arrived closer together than it left was held on the way and let go at once. Its
// last packet came in with the link's lowest delay, so the link kept up with the pacer: it shows
// the rate it was sent at, no more.
func TestHeldBurstShowsNoMoreThanItsSendRate(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 20, 0, time.Millisecond) // 9.6 Mbit/s over 19 ms
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 18*time.Millisecond, paced, nil)) // in over 9.5 ms
	if capacity, _, _ := m.snapshot(); capacity < 9.4e6 || capacity > 9.8e6 {
		t.Fatalf("capacity %.0f, want the 9.6 Mbit/s it was sent at", capacity)
	}
}

// One burst moves the estimate at most to twice what the encoder has, even with nothing measured before.
func TestBurstIsBounded(t *testing.T) {
	m := newTestMeter()
	m.setTarget(1_000_000)
	sendBurst(m, 1, 20, 0, paced) // genuinely paced at 19.2 Mbit/s, well above the encoder's target
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 0, paced, nil)) // and arrived as fast
	if capacity, _, _ := m.snapshot(); capacity != 2e6 {
		t.Fatalf("capacity %.0f, want 2 Mbit/s: twice the target, and the 2 Mbit/s least", capacity)
	}
}

// Tiny scheduling gaps are not pacing evidence. These arrivals bunch in groups
// of four, but the old 500 us send-gap guard did not count any of them as bunched.
// Repeated samples then doubled their own ceiling from 2 to 88 Mbit/s in 120 ms.
func TestUnpacedBatchesCannotRatchetCapacity(t *testing.T) {
	m := newTestMeter()
	m.setTarget(1_000_000)
	seq := uint16(1)
	for round := 0; round < 8; round++ {
		at := time.Duration(round) * 15 * time.Millisecond
		sent, arrived := make([]time.Duration, 24), make([]time.Duration, 24)
		for i := range sent {
			sent[i] = at + time.Duration(i)*100*time.Microsecond
			arrived[i] = at + time.Duration(i/4)*500*time.Microsecond
		}
		timedBurst(m, seq, sent, arrived)
		capacity, _, _ := m.snapshot()
		if capacity != 0 {
			t.Fatalf("unpaced batch %d taught capacity %.0f; scheduling gaps must not raise the bitrate", round+1, capacity)
		}
		m.setTarget(max(1_000_000, min(int(floorShare*capacity), maxBitrate)))
		seq += uint16(len(sent))
	}
}

// The real pacer wakes every 2 ms and sends several packets together. Those
// boundaries are enough evidence to learn a fast link without a bandwidth cap.
func TestPacedClustersStillMeasureFastLink(t *testing.T) {
	m := newTestMeter()
	m.setTarget(30_000_000)
	sent := clusteredSendTimes()
	arrived := make([]time.Duration, len(sent))
	for i := range arrived {
		arrived[i] = time.Duration(i) * 500 * time.Microsecond // 19.2 Mbit/s bottleneck
	}
	timedBurst(m, 1, sent, arrived)
	if capacity, _, _ := m.snapshot(); capacity < 19e6 || capacity > 19.4e6 {
		t.Fatalf("paced clusters measured %.0f; want the approximately 20 Mbit/s link", capacity)
	}
}

// However fast bursts look, the estimate at most doubles each growTime: a run of bursts that
// some batching still made look fast cannot carry it off within a few frames, as one did from
// 2 to 88 Mbit/s in 120 ms. Each of these crossed 24 KB in 11 ms, 16 Mbit/s.
func TestEstimateGrowsAtMostTwiceEachGrowTime(t *testing.T) {
	m := newTestMeter()
	m.setTarget(1_000_000)
	start := time.Now()
	seq := uint16(1)
	for round := 0; round < 8; round++ {
		at := time.Since(m.start)
		sendBurst(m, seq, 20, at, 0)
		settle(m)
		arrivals := make([]time.Duration, 20)
		for i := range arrivals {
			arrivals[i] = at + time.Duration(i)*600*time.Microsecond
		}
		arrive(m, seq, arrivals)
		capacity, _, _ := m.snapshot()
		m.setTarget(max(1_000_000, min(int(floorShare*capacity), maxBitrate)))
		seq += 20
	}
	capacity, _, _ := m.snapshot()
	if most := 2e6 * math.Exp2(float64(time.Since(start))/float64(growTime)); capacity < 2e6 || capacity > most*1.01 {
		t.Fatalf("capacity %.0f after %v, want 2 Mbit/s at first, doubled at most each %v since: %.0f", capacity, time.Since(start), growTime, most)
	}
}

// On a slow link a normally paced packet can leave more than four milliseconds
// after its predecessor. Treating every packet as a new burst prevents any
// capacity measurement, even though the full train has ample paced evidence.
func TestSlowPacedTrainsRemainOneSample(t *testing.T) {
	for _, tc := range []struct {
		name   string
		target int
		slow   bool
	}{
		{"one megabit with scheduler jitter", 1_000_000, false},
		{"floor rate with split feedback", 200_000, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			m := newTestMeter()
			m.setTarget(tc.target)
			sent := make([]time.Duration, 24)
			for i := range sent {
				if tc.slow {
					sent[i] = time.Duration(i) * 20 * time.Millisecond
				} else {
					// A nominal 3.84 ms interval rounded to 4/6 ms timer wakes.
					sent[i] = time.Duration(i/2)*10*time.Millisecond + time.Duration(i%2)*4*time.Millisecond
				}
				sendBurst(m, uint16(i+1), 1, sent[i], 0)
			}
			settle(m)
			for begin := 0; begin < len(sent); begin += 12 {
				fb := feedback(uint16(begin+1), 12, 10, 0, 0, nil)
				previous := time.Duration(0)
				for i := range fb.RecvDeltas {
					at := sent[begin+i]
					fb.RecvDeltas[i] = &rtcp.RecvDelta{Type: rtcp.TypeTCCPacketReceivedLargeDelta, Delta: int64((at - previous) / time.Microsecond)}
					previous = at
				}
				m.onFeedback(fb)
			}
			want := 1.936842e6
			if tc.slow {
				want = 480_000
			}
			if capacity, _, _ := m.snapshot(); capacity < want*.99 || capacity > want*1.01 {
				t.Fatalf("slow paced train measured %.0f; want about %.0f without a bitrate floor trap", capacity, want)
			}
		})
	}
}

func clusteredSendTimes() []time.Duration {
	var sent []time.Duration
	for i := 0; i < 16; i++ {
		sent = append(sent, time.Duration(i)*time.Microsecond)
	}
	for group := 1; group <= 6; group++ {
		for i := 0; i < 5; i++ {
			sent = append(sent, time.Duration(group)*2*time.Millisecond+time.Duration(i)*time.Microsecond)
		}
	}
	return sent
}

func timedBurst(m *linkMeter, seq uint16, sent, arrived []time.Duration) {
	for i, at := range sent {
		sendBurst(m, seq+uint16(i), 1, at, 0)
	}
	settle(m)
	fb := feedback(seq, len(sent), 10, 0, 0, nil)
	prev := time.Duration(0)
	for i, at := range arrived {
		fb.RecvDeltas[i] = &rtcp.RecvDelta{Type: rtcp.TypeTCCPacketReceivedLargeDelta, Delta: int64((at - prev) / time.Microsecond)}
		prev = at
	}
	m.onFeedback(fb)
}

// queuedBursts sends n bursts of 10 packets gap apart from seq and at on, each arriving queue
// later than the link alone takes, and feeds their feedback to the meter.
func queuedBursts(m *linkMeter, seq uint16, n int, at, gap time.Duration, queue ...time.Duration) {
	for i := 0; i < n; i++ {
		s, sent := seq+uint16(10*i), at+time.Duration(i)*gap
		sendBurst(m, s, 10, sent, paced)
		settle(m)
		m.onFeedback(feedback(s, 10, 10, sent+queue[i%len(queue)], time.Millisecond, nil))
	}
}

// Bursts that all start above the lowest delay of the last seconds, for long enough, met a
// queue that did not drain.
func TestStandingQueue(t *testing.T) {
	m := newTestMeter()
	queuedBursts(m, 1, 1, 0, 0, 0)
	if _, standing, _ := m.snapshot(); standing > 2*time.Millisecond {
		t.Fatalf("standing %v after the first burst, want none", standing)
	}
	// Each sent 100 ms after the one before and arriving 80 ms later than the link alone takes.
	queuedBursts(m, 11, 2, 500*time.Millisecond, 100*time.Millisecond, 80*time.Millisecond)
	if _, standing, _ := m.snapshot(); standing != 0 {
		t.Fatalf("standing %v after two queued bursts, want none yet", standing)
	}
	queuedBursts(m, 31, 2, 700*time.Millisecond, 100*time.Millisecond, 80*time.Millisecond)
	if _, standing, _ := m.snapshot(); standing < 79*time.Millisecond || standing > 81*time.Millisecond {
		t.Fatalf("standing %v after four over 300 ms, want 80 ms", standing)
	}
}

// Single packets that leave apart are bursts of their own: a few within one jitter spike are
// not yet a queue.
func TestShortRunIsNoStandingQueue(t *testing.T) {
	m := newTestMeter()
	queuedBursts(m, 1, 1, 0, 0, 0)
	queuedBursts(m, 11, 6, 500*time.Millisecond, 20*time.Millisecond, 80*time.Millisecond)
	if _, standing, _ := m.snapshot(); standing != 0 {
		t.Fatalf("standing %v after 100 ms of late bursts, want none yet", standing)
	}
}

// A deep queue in bursts that follow closely is a link that got slower: it counts at once.
func TestDeepQueueCountsAtOnce(t *testing.T) {
	m := newTestMeter()
	queuedBursts(m, 1, 1, 0, 0, 0)
	queuedBursts(m, 11, 3, 500*time.Millisecond, 20*time.Millisecond, 150*time.Millisecond)
	if _, standing, _ := m.snapshot(); standing < 149*time.Millisecond || standing > 151*time.Millisecond {
		t.Fatalf("standing %v after three bursts 150 ms late, want 150 ms", standing)
	}
}

// Jitter delays a burst now and then, a queue that does not drain delays them all: the bursts
// between the late ones show the link has no standing queue.
func TestJitterIsNoStandingQueue(t *testing.T) {
	m := newTestMeter()
	queuedBursts(m, 1, 9, 0, 60*time.Millisecond, 0, 80*time.Millisecond, 5*time.Millisecond)
	if _, standing, _ := m.snapshot(); standing > 2*time.Millisecond {
		t.Fatalf("standing %v with every second burst 80 ms late, want none", standing)
	}
}

func TestLoss(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 10, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 10, 10, 0, time.Millisecond, map[int]bool{2: true, 5: true, 7: true, 8: true, 9: true}))
	if _, _, loss := m.snapshot(); loss != 0.5 {
		t.Fatalf("loss %v, want 0.5: half the packets lost", loss)
	}
}

// A packet in two feedbacks counts once, and one said lost that came in late counts as come:
// Pion's recorder says a stretch again when a late packet arrives.
func TestLossCountsEachPacketOnce(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 20, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 0, time.Millisecond, map[int]bool{4: true}))
	m.onFeedback(feedback(1, 20, 10, 0, time.Millisecond, map[int]bool{4: true})) // the same again
	if _, _, loss := m.snapshot(); loss != 0.05 {
		t.Fatalf("loss %v, want 0.05: 1 of 20, counted once", loss)
	}
	m.onFeedback(feedback(1, 20, 10, 0, time.Millisecond, nil)) // packet 5 came in late after all
	if _, _, loss := m.snapshot(); loss != 0 {
		t.Fatalf("loss %v after the late arrival, want 0", loss)
	}
}

// The floor lifts a GCC estimate that sank below what bursts showed, and goes away with a
// standing queue, taking the bursts measured so far with it.
func TestTargetFloor(t *testing.T) {
	m := newTestMeter()
	p := &peer{meter: m, floorOn: true}
	p.gccBps.Store(400_000)
	if final, floor, _ := p.target(); final != 400_000 || floor != 0 {
		t.Fatalf("before any burst: final %d floor %d, want GCC's 400000 and no floor", final, floor)
	}
	sendBurst(m, 1, 20, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 0, time.Millisecond, nil))
	final, floor, _ := p.target()
	if floor < 7_500_000 || floor > 7_800_000 || final != floor {
		t.Fatalf("after a 9.6 Mbit/s burst: final %d floor %d, want both about 7.68 Mbit/s", final, floor)
	}
	p.gccBps.Store(12_000_000)
	if final, _, _ := p.target(); final != 12_000_000 {
		t.Fatalf("GCC above the floor: final %d, want 12000000", final)
	}
	p.gccBps.Store(400_000)

	// The next bursts start 80 ms above the lowest delay: the floor goes, and stays gone.
	queuedBursts(m, 21, 4, 500*time.Millisecond, 100*time.Millisecond, 80*time.Millisecond)
	if final, floor, _ := p.target(); final != 400_000 || floor != 0 {
		t.Fatalf("with a standing queue: final %d floor %d, want GCC's 400000 and no floor", final, floor)
	}
	if capacity, _, _ := m.snapshot(); capacity != 0 {
		t.Fatalf("capacity %.0f after the queue, want the old bursts forgotten", capacity)
	}
}

// The status of each packet is read the way the feedback lays it out: run lengths, and one bit
// symbols.
func TestFeedbackLayouts(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 16, 0, paced)
	settle(m)
	fb := &rtcp.TransportLayerCC{
		BaseSequenceNumber: 1, PacketStatusCount: 16, ReferenceTime: 10,
		PacketChunks: []rtcp.PacketStatusChunk{
			&rtcp.RunLengthChunk{PacketStatusSymbol: rtcp.TypeTCCPacketReceivedSmallDelta, RunLength: 2},
			&rtcp.StatusVectorChunk{SymbolSize: rtcp.TypeTCCSymbolSizeOneBit, SymbolList: []uint16{1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}},
		},
	}
	for i := 0; i < 16; i++ {
		fb.RecvDeltas = append(fb.RecvDeltas, &rtcp.RecvDelta{Type: rtcp.TypeTCCPacketReceivedSmallDelta, Delta: 1000})
	}
	m.onFeedback(fb)
	if capacity, _, loss := m.snapshot(); capacity < 9.4e6 || capacity > 9.8e6 || loss != 0 {
		t.Fatalf("capacity %.0f loss %v, want about 9.6 Mbit/s and none lost", capacity, loss)
	}
}
