package main

import (
	"encoding/binary"
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
	for time.Since(m.start) <= m.lastSend+2*burstGap {
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

// A small burst says nothing of the link, nor does one whose arrivals are too close to time, nor
// one that left all at once: that one can arrive all at once behind a tunnel that batches. Nor
// does one that arrives closer together than it left.
func TestBurstsThatSayNothing(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 3, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 3, 10, 0, time.Millisecond, nil))
	sendBurst(m, 4, 20, 20*time.Millisecond, paced)
	settle(m)
	m.onFeedback(feedback(4, 20, 10, 20*time.Millisecond, 50*time.Microsecond, nil))
	sendBurst(m, 24, 20, 40*time.Millisecond, 0)
	settle(m)
	m.onFeedback(feedback(24, 20, 10, 40*time.Millisecond, time.Millisecond, nil))
	// Spread over 19 ms by the pacer, in over 9.5 ms: held up on the way and let go at once.
	sendBurst(m, 44, 20, 60*time.Millisecond, time.Millisecond)
	settle(m)
	m.onFeedback(feedback(44, 20, 10, 80*time.Millisecond, paced, nil))
	if capacity, _, _ := m.snapshot(); capacity != 0 {
		t.Fatalf("capacity %.0f, want none", capacity)
	}
}

// A burst the pacer spread out that arrives in batches, several packets in one tick, came out of
// a tunnel that held them, not off the link: it says nothing, however its batches are spaced.
func TestBatchedBurstSaysNothing(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 20, 0, time.Millisecond)
	settle(m)
	var arrivals []time.Duration
	for i := 0; i < 20; i++ {
		arrivals = append(arrivals, time.Duration(i/5)*6*time.Millisecond) // 4 batches of 5, 6 ms apart
	}
	fb := &rtcp.TransportLayerCC{BaseSequenceNumber: 1, PacketStatusCount: 20, ReferenceTime: 10}
	var symbols []uint16
	prev := time.Duration(0)
	for _, at := range arrivals {
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
	m.onFeedback(fb)
	if capacity, _, _ := m.snapshot(); capacity != 0 {
		t.Fatalf("capacity %.0f, want none", capacity)
	}
}

// One burst moves the estimate at most to twice what the encoder has, even with nothing measured before.
func TestBurstIsBounded(t *testing.T) {
	m := newTestMeter()
	m.setTarget(1_000_000)
	sendBurst(m, 1, 20, 0, 110*time.Microsecond) // sent at 87 Mbit/s, over just more than 2 ms
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 0, 110*time.Microsecond, nil)) // and arrived as fast
	if capacity, _, _ := m.snapshot(); capacity != 2e6 {
		t.Fatalf("capacity %.0f, want 2 Mbit/s: twice the target, and the 2 Mbit/s least", capacity)
	}
}

// A burst that starts above the lowest delay of the last seconds met a queue that did not drain.
func TestStandingQueue(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 10, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 10, 10, 0, time.Millisecond, nil))
	if _, standing, _ := m.snapshot(); standing > 2*time.Millisecond {
		t.Fatalf("standing %v after the first burst, want none", standing)
	}
	// Sent 30 ms later, arriving 30 ms later plus 80 ms of queue.
	sendBurst(m, 11, 10, 30*time.Millisecond, paced)
	settle(m)
	m.onFeedback(feedback(11, 10, 10, 110*time.Millisecond, time.Millisecond, nil))
	if _, standing, _ := m.snapshot(); standing < 79*time.Millisecond || standing > 81*time.Millisecond {
		t.Fatalf("standing %v, want 80 ms", standing)
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
	if final, floor := p.target(); final != 400_000 || floor != 0 {
		t.Fatalf("before any burst: final %d floor %d, want GCC's 400000 and no floor", final, floor)
	}
	sendBurst(m, 1, 20, 0, paced)
	settle(m)
	m.onFeedback(feedback(1, 20, 10, 0, time.Millisecond, nil))
	final, floor := p.target()
	if floor < 7_500_000 || floor > 7_800_000 || final != floor {
		t.Fatalf("after a 9.6 Mbit/s burst: final %d floor %d, want both about 7.68 Mbit/s", final, floor)
	}
	p.gccBps.Store(12_000_000)
	if final, _ := p.target(); final != 12_000_000 {
		t.Fatalf("GCC above the floor: final %d, want 12000000", final)
	}
	p.gccBps.Store(400_000)

	// The next burst starts 80 ms above the lowest delay: the floor goes, and stays gone.
	sendBurst(m, 21, 20, 40*time.Millisecond, paced)
	settle(m)
	m.onFeedback(feedback(21, 20, 10, 120*time.Millisecond, time.Millisecond, nil))
	if final, floor := p.target(); final != 400_000 || floor != 0 {
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
