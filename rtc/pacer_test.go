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
	p, log := newTestPacer(200_000) // 62.5 KB/s: the first 16 packets go at once, the rest wait
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
