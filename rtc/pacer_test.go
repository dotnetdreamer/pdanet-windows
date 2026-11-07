package main

import (
	"encoding/binary"
	"sync"
	"testing"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
)

const pacerExt = 3

type sentLog struct {
	mu   sync.Mutex
	seqs []uint16 // RTP sequence numbers in the order they left
	twcc []uint16 // and the transport wide numbers they left with
	at   []time.Time
}

func (l *sentLog) Write(h *rtp.Header, _ []byte, _ interceptor.Attributes) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.seqs = append(l.seqs, h.SequenceNumber)
	l.twcc = append(l.twcc, binary.BigEndian.Uint16(h.GetExtension(pacerExt)))
	l.at = append(l.at, time.Now())
	return 0, nil
}

func (l *sentLog) count() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.seqs)
}

// testPacket is a 1200 byte packet as the TWCC interceptor hands it: with a number of its own,
// which the pacer replaces.
func testPacket(seq uint16) (*rtp.Header, []byte) {
	h := &rtp.Header{Version: 2, SSRC: 9, SequenceNumber: seq}
	_ = h.SetExtension(pacerExt, []byte{0xee, 0xee})
	return h, make([]byte, 1200-h.MarshalSize())
}

func newTestPacer(bps int) (*burstPacer, *sentLog) {
	p := newBurstPacer(bps, nil)
	p.setExt(pacerExt)
	log := &sentLog{}
	p.AddStream(9, log)
	return p, log
}

func waitSent(t *testing.T, log *sentLog, n int, within time.Duration) {
	t.Helper()
	deadline := time.Now().Add(within)
	for log.count() < n {
		if time.Now().After(deadline) {
			t.Fatalf("%d packets left within %v, want %d", log.count(), within, n)
		}
		time.Sleep(time.Millisecond)
	}
}

// A packet sent again goes before the screen's new ones waiting, and every packet leaves with
// the next transport wide number, in the order they leave.
func TestPacerResendsFirstNumbersInOrder(t *testing.T) {
	p, log := newTestPacer(200_000) // 62.5 KB/s: only a couple of packets go at once, the rest wait
	defer p.Close()
	for seq := uint16(100); seq < 140; seq++ {
		h, b := testPacket(seq)
		_, _ = p.Write(h, b, nil)
	}
	h, b := testPacket(105) // asked for again
	_, _ = p.Write(h, b, nil)
	waitSent(t, log, 41, 3*time.Second)
	log.mu.Lock()
	defer log.mu.Unlock()
	at := -1
	for i, s := range log.seqs {
		if s == 105 && i > 5 {
			at = i
		}
	}
	if at < 0 || at > 20 {
		t.Fatalf("the resend left as packet %d (order %v), want right behind those already out", at, log.seqs)
	}
	for i, n := range log.twcc {
		if n != uint16(i) {
			t.Fatalf("packet %d left with transport wide number %d, want %d (%v)", i, n, i, log.twcc)
		}
	}
}

// A 22.5 KB keyframe at the floor must retain enough paced tail to measure the
// link and lift the encoder target. A fixed 19.2 KB idle allowance spent nearly
// all of it at once, leaving no usable timing sample and trapping the bitrate.
func TestPacerFloorKeyframeTeachesCapacity(t *testing.T) {
	m := newTestMeter()
	m.setExt(pacerExt)
	m.setTarget(minBitrate)
	p := newBurstPacer(minBitrate, m)
	p.setExt(pacerExt)
	log := &sentLog{}
	p.AddStream(9, log)
	defer p.Close()
	if credit := p.capacity(); credit > 2*1200 {
		t.Fatalf("floor idle credit %.0f bytes exceeds two packets", credit)
	}
	const count = 19
	for i := 0; i < count; i++ {
		h, payload := testPacket(uint16(i + 1))
		if i == count-1 {
			payload = payload[:900-h.MarshalSize()]
		}
		if _, err := p.Write(h, payload, nil); err != nil {
			t.Fatal(err)
		}
	}
	waitSent(t, log, count, 2*time.Second)
	settle(m)
	fb := feedback(0, count, 10, 0, 0, nil)
	previous := time.Duration(0)
	for i := range fb.RecvDeltas {
		m.mu.Lock()
		at := m.sent[i].at / arrivalTick * arrivalTick
		m.mu.Unlock()
		fb.RecvDeltas[i].Delta = int64((at - previous) / time.Microsecond)
		previous = at
	}
	m.onFeedback(fb)
	peer := &peer{meter: m, floorOn: true}
	peer.gccBps.Store(minBitrate)
	if target, _, _ := peer.target(); target <= minBitrate {
		capacity, _, _ := m.snapshot()
		t.Fatalf("floor keyframe left target %d, capacity %.0f; want measured capacity to lift the floor", target, capacity)
	}
}

// A pile far above the rate drains within about drainTime, spread out, never all at once.
func TestPacerDrainsAPile(t *testing.T) {
	p, log := newTestPacer(200_000)
	defer p.Close()
	start := time.Now()
	for seq := uint16(1); seq <= 100; seq++ { // 120 KB, at 2.5 x 200 kbit/s it would take 1.9 s
		h, b := testPacket(seq)
		_, _ = p.Write(h, b, nil)
	}
	waitSent(t, log, 100, 2*time.Second)
	log.mu.Lock()
	defer log.mu.Unlock()
	took := log.at[99].Sub(start)
	if took < drainTime/2 || took > 2*drainTime {
		t.Fatalf("the pile took %v to leave, want about %v", took, drainTime)
	}
	// No more than a burst's worth leaves in any 10 ms.
	for i := range log.at {
		j := i
		for j < len(log.at) && log.at[j].Sub(log.at[i]) < 10*time.Millisecond {
			j++
		}
		if j-i > 40 {
			t.Fatalf("%d packets left within 10 ms from packet %d, want the pile spread out", j-i, i)
		}
	}
}

// Once the meter measured the link, a large burst leaves close to the link's rate: the queue
// it makes waits here, not in front of the slowest hop, which might keep less of it. A small
// burst still goes faster than the link, so the meter can see the link get faster.
func TestPacerKeepsALargeBurstNearTheLink(t *testing.T) {
	p, log := newTestPacer(1_000_000)
	defer p.Close()
	p.SetFloor(8_000_000, 10_000_000) // 1.5 times the 10 Mbit/s link is 15 Mbit/s
	start := time.Now()
	for seq := uint16(1); seq <= 500; seq++ { // 600 KB: 0.32 s at 15 Mbit/s, 0.48 s at the link's 10
		h, b := testPacket(seq)
		_, _ = p.Write(h, b, nil)
	}
	waitSent(t, log, 500, 2*time.Second)
	log.mu.Lock()
	took := log.at[499].Sub(start)
	log.mu.Unlock()
	if took < 360*time.Millisecond || took > 600*time.Millisecond {
		t.Fatalf("600 KB took %v to leave towards a 10 Mbit/s link, want about 0.4 s", took)
	}

	// 30 KB, less than 30 ms of the link adds to its queue at 1.5 times, goes at 1.5 times.
	time.Sleep(100 * time.Millisecond)
	before := log.count()
	start = time.Now()
	for seq := uint16(501); seq <= 525; seq++ {
		h, b := testPacket(seq)
		_, _ = p.Write(h, b, nil)
	}
	waitSent(t, log, before+25, time.Second)
	log.mu.Lock()
	took = log.at[before+24].Sub(start)
	log.mu.Unlock()
	if took > 25*time.Millisecond {
		t.Fatalf("a 30 KB burst took %v, want about 16 ms at 15 Mbit/s", took)
	}
}

// When a queue or losses make the meter forget the link, a pile still leaves near the link the
// meter measured last. Without that bound it drained within about drainTime whatever the link:
// 600 KB in 0.35 s, 14 Mbit/s, towards a link measured at 10.
func TestPacerKeepsTheLastLinkWhenTheMeterForgetsIt(t *testing.T) {
	p, log := newTestPacer(1_000_000)
	defer p.Close()
	p.SetFloor(3_200_000, 4_000_000)
	p.SetFloor(0, 0) // a queue built up: the floor is off and the meter forgot its bursts
	start := time.Now()
	for seq := uint16(1); seq <= 500; seq++ { // 600 KB: 1.2 s at the link's 4 Mbit/s, 1.9 s at 2.5 x GCC
		h, b := testPacket(seq)
		_, _ = p.Write(h, b, nil)
	}
	waitSent(t, log, 500, 3*time.Second)
	log.mu.Lock()
	took := log.at[499].Sub(start)
	log.mu.Unlock()
	if took < 800*time.Millisecond || took > 1600*time.Millisecond {
		t.Fatalf("600 KB took %v to leave after the meter forgot the 4 Mbit/s link, want about 1.1 s", took)
	}
}
