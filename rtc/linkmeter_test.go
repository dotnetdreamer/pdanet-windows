package main

import (
	"encoding/binary"
	"testing"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/rtp"
)

const testExt = 5

// sendBurst notes n packets of 1200 bytes each as they leave, sequence numbers from seq on.
func sendBurst(m *linkMeter, seq uint16, n int) {
	for i := 0; i < n; i++ {
		h := &rtp.Header{Version: 2, SSRC: 1, SequenceNumber: seq + uint16(i)}
		var b [2]byte
		binary.BigEndian.PutUint16(b[:], seq+uint16(i))
		if err := h.SetExtension(testExt, b[:]); err != nil {
			panic(err)
		}
		m.onSent(h, 1200-h.MarshalSize())
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
	return m
}

// A burst sent at once that arrives 1 ms per packet of 1200 bytes shows a link of 9.6 Mbit/s.
func TestBurstShowsTheLinkRate(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 100, 20)
	time.Sleep(2 * burstGap) // nothing follows, so the feedback finishes the burst
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
	sendBurst(m, base, 20)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(base, 10, 10, 0, time.Millisecond, nil))
	m.onFeedback(feedback(base+10, 10, 10, 10*time.Millisecond, time.Millisecond, nil))
	if capacity, _, _ := m.snapshot(); capacity < 9.4e6 || capacity > 9.8e6 {
		t.Fatalf("capacity %.0f, want about 9.6 Mbit/s", capacity)
	}
}

// A small burst says nothing of the link, nor does one whose arrivals are too close to time.
func TestSmallBurstsAreNotTimed(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 3)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(1, 3, 10, 0, time.Millisecond, nil))
	sendBurst(m, 4, 20)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(4, 20, 10, 0, 50*time.Microsecond, nil))
	if capacity, _, _ := m.snapshot(); capacity != 0 {
		t.Fatalf("capacity %.0f, want none", capacity)
	}
}

// A burst that starts above the lowest delay of the last seconds met a queue that did not drain.
func TestStandingQueue(t *testing.T) {
	m := newTestMeter()
	start := time.Now()
	sendBurst(m, 1, 10)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(1, 10, 10, 0, time.Millisecond, nil))
	if _, standing, _ := m.snapshot(); standing > 2*time.Millisecond {
		t.Fatalf("standing %v after the first burst, want none", standing)
	}
	time.Sleep(20 * time.Millisecond)
	sentAfter := time.Since(start)
	sendBurst(m, 11, 10)
	time.Sleep(2 * burstGap)
	// The same one way delay as before, plus 80 ms of queue.
	m.onFeedback(feedback(11, 10, 10, sentAfter+80*time.Millisecond, time.Millisecond, nil))
	if _, standing, _ := m.snapshot(); standing < 75*time.Millisecond || standing > 90*time.Millisecond {
		t.Fatalf("standing %v, want about 80 ms", standing)
	}
}

func TestLoss(t *testing.T) {
	m := newTestMeter()
	sendBurst(m, 1, 10)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(1, 10, 10, 0, time.Millisecond, map[int]bool{2: true, 5: true, 7: true, 8: true, 9: true}))
	if _, _, loss := m.snapshot(); loss < 0.09 || loss > 0.11 {
		t.Fatalf("loss %v, want 0.1 (half the packets lost, averaged in at 0.2)", loss)
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
	start := time.Now()
	sendBurst(m, 1, 20)
	time.Sleep(2 * burstGap)
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
	time.Sleep(20 * time.Millisecond)
	sentAfter := time.Since(start)
	sendBurst(m, 21, 20)
	time.Sleep(2 * burstGap)
	m.onFeedback(feedback(21, 20, 10, sentAfter+80*time.Millisecond, time.Millisecond, nil))
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
	sendBurst(m, 1, 16)
	time.Sleep(2 * burstGap)
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
