package main

import (
	"slices"
	"testing"
	"time"

	"github.com/pion/rtcp"
)

func newTestNackGen(rtt time.Duration) (*nackGen, *nackStream) {
	g := &nackGen{rtt: func() time.Duration { return rtt }, streams: map[uint32]*nackStream{}, close: make(chan struct{})}
	st := &nackStream{ssrc: 7, missing: map[uint16]*nackMiss{}}
	g.streams[7] = st
	return g, st
}

// asked lists the sequence numbers a round of due asks for.
func asked(t *testing.T, pkts []rtcp.Packet) []uint16 {
	t.Helper()
	var out []uint16
	for _, p := range pkts {
		n, ok := p.(*rtcp.TransportLayerNack)
		if !ok || n.MediaSSRC != 7 {
			t.Fatalf("unexpected packet %v", p)
		}
		for _, pair := range n.Nacks {
			out = append(out, pair.PacketList()...)
		}
	}
	return out
}

// A lost packet is asked for at once, then again only after one and a half round trips, not
// every tick: asking sooner made the PC that shares send copies that counted as lost.
func TestNackAsksAgainAfterTheRoundTrip(t *testing.T) {
	g, st := newTestNackGen(200 * time.Millisecond)
	t0 := time.Now()
	g.arrived(st, 10, t0)
	g.arrived(st, 13, t0) // 11 and 12 missing
	if got := asked(t, g.due(t0.Add(time.Millisecond), 1)); len(got) != 0 {
		t.Fatalf("asked %v within the grace, want nothing", got)
	}
	if got := asked(t, g.due(t0.Add(10*time.Millisecond), 1)); !slices.Equal(got, []uint16{11, 12}) {
		t.Fatalf("asked %v, want 11 and 12", got)
	}
	for _, at := range []time.Duration{50, 150, 300} {
		if got := asked(t, g.due(t0.Add(at*time.Millisecond), 1)); len(got) != 0 {
			t.Fatalf("asked %v again at %d ms, want nothing before 310 ms", got, at)
		}
	}
	g.arrived(st, 12, t0.Add(305*time.Millisecond)) // its resend came
	if got := asked(t, g.due(t0.Add(311*time.Millisecond), 1)); !slices.Equal(got, []uint16{11}) {
		t.Fatalf("asked %v, want 11 again, and not 12", got)
	}
}

// After nackTries asks, or once the viewer no longer waits for the frame, a packet is let go.
func TestNackGivesUp(t *testing.T) {
	g, st := newTestNackGen(50 * time.Millisecond) // asks again every nackMinWait
	t0 := time.Now()
	g.arrived(st, 1, t0)
	g.arrived(st, 3, t0)
	n := 0
	for at := 10 * time.Millisecond; at < 2*time.Second; at += 10 * time.Millisecond {
		n += len(asked(t, g.due(t0.Add(at), 1)))
	}
	if n != nackTries {
		t.Fatalf("asked %d times, want %d", n, nackTries)
	}
	g.arrived(st, 5, t0.Add(2*time.Second))
	g.due(t0.Add(2*time.Second+10*time.Millisecond), 1)
	if got := asked(t, g.due(t0.Add(2*time.Second+frameWait+20*time.Millisecond), 1)); len(got) != 0 {
		t.Fatalf("asked %v past frameWait, want nothing", got)
	}
	if len(st.missing) != 0 {
		t.Fatalf("%d still missing, want none", len(st.missing))
	}
}

// Sequence numbers wrap; a jump far ahead is a new start, not a thousand losses.
func TestNackWrapAndJump(t *testing.T) {
	g, st := newTestNackGen(100 * time.Millisecond)
	t0 := time.Now()
	g.arrived(st, 65534, t0)
	g.arrived(st, 1, t0) // 65535 and 0 missing
	if got := asked(t, g.due(t0.Add(10*time.Millisecond), 1)); !slices.Equal(got, []uint16{65535, 0}) {
		t.Fatalf("asked %v, want 65535 then 0", got)
	}
	g.arrived(st, 30000, t0)
	if len(st.missing) != 0 {
		t.Fatalf("%d missing after a jump, want none", len(st.missing))
	}
}
