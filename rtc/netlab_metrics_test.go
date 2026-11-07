package main

import (
	"math"
	"testing"
	"time"
)

func TestNetLabMetricsExcludeIdleCrossing(t *testing.T) {
	v := &viewerModel{shownAt: []time.Duration{5 * time.Second, 7900 * time.Millisecond, 10100 * time.Millisecond, 12 * time.Second}}
	r := v.resultAt(nil, &screenModel{}, 0, 5*time.Second, 12*time.Second)
	if r.frozen != 4800*time.Millisecond || r.longest != 2900*time.Millisecond {
		t.Fatalf("idle rest inflated a freeze: frozen %v longest %v, want 4.8s and 2.9s", r.frozen, r.longest)
	}
}

func TestNetLabMetricsIncludesTailBeginningAtRest(t *testing.T) {
	v := &viewerModel{shownAt: []time.Duration{5 * time.Second, 6 * time.Second, 7 * time.Second, 8 * time.Second, 9 * time.Second}}
	r := v.resultAt(nil, &screenModel{}, 0, 5*time.Second, 12*time.Second)
	if r.frozen != 5*time.Second || r.longest != 2*time.Second {
		t.Fatalf("tail starting during rest hid motion freeze: frozen %v longest %v, want 5s and 2s", r.frozen, r.longest)
	}
}

func TestNetLabMetricsExactMotionDenominator(t *testing.T) {
	v := &viewerModel{}
	for at := 5 * time.Second; at < 8*time.Second; at += 100 * time.Millisecond {
		v.shownAt = append(v.shownAt, at)
	}
	r := v.resultAt(nil, &screenModel{}, 0, 5*time.Second, 9*time.Second)
	if math.Abs(r.motionFps-10) > .001 {
		t.Fatalf("30 frames in 3 seconds of actual motion: %.4f fps, want 10", r.motionFps)
	}
}

func TestNetLabMetricsWarmupAndStopWindow(t *testing.T) {
	start := time.Unix(1000, 0)
	v := &viewerModel{start: start, shownAt: []time.Duration{4 * time.Second, 5 * time.Second, 6 * time.Second, 7 * time.Second, 8 * time.Second}}
	v.samples = []viewerSample{
		{4 * time.Second, time.Second, 100_000, true}, // warmup must not affect payload or latency.
		{5 * time.Second, 20 * time.Millisecond, 100, true},
		{6 * time.Second, 40 * time.Millisecond, 200, true},
		{7 * time.Second, 60 * time.Millisecond, 300, true},
		{8 * time.Second, 2 * time.Second, 200_000, true}, // post-stop drain is not measured.
	}
	target := []targetSample{{start, 1_000_000}, {start.Add(6 * time.Second), 2_000_000}, {start.Add(8 * time.Second), 30_000_000}}
	r := v.resultAt(target, &screenModel{}, 0, 5*time.Second, 7*time.Second)
	if math.Abs(r.deliveredKbps-2.4) > 1e-9 || r.p50 != 40*time.Millisecond || r.p95 != 60*time.Millisecond || r.max != 60*time.Millisecond {
		t.Fatalf("windowed payload/latency: %+v", r)
	}
	if r.targetKbps != 1500 || r.minTargetKbps != 1000 || r.motionFps != 1.5 {
		t.Fatalf("target and FPS use a different observation window: %+v", r)
	}
}

func TestNetLabMetricsWarmupClipsMotionFreeze(t *testing.T) {
	v := &viewerModel{shownAt: []time.Duration{0, 3 * time.Second}}
	r := v.resultAt(nil, &screenModel{}, 0, 2*time.Second, 4*time.Second)
	if r.frozen != time.Second || r.longest != time.Second {
		t.Fatalf("warmup contributed to freeze: frozen %v longest %v", r.frozen, r.longest)
	}
	// No-motion windows, empty windows and repeated results must remain finite and unchanged.
	for _, window := range [][2]time.Duration{{3 * time.Second, 5 * time.Second}, {5 * time.Second, 5 * time.Second}} {
		r = v.resultAt(nil, &screenModel{}, 0, window[0], window[1])
		if r.motionFps != 0 || r.frozen != 0 || r.deliveredKbps != 0 {
			t.Fatalf("idle/empty window has motion or payload: %+v", r)
		}
	}
}

func TestNetLabMetricsIdleCannotJoinSeparateFreezes(t *testing.T) {
	v := &viewerModel{}
	r := v.resultAt(nil, &screenModel{}, 0, 0, 10*time.Second)
	if r.frozen != 6*time.Second || r.longest != 3*time.Second {
		t.Fatalf("two idle-separated motion intervals joined: frozen %v longest %v", r.frozen, r.longest)
	}
}

func TestNetLabControlKeyRequestReachesHost(t *testing.T) {
	screen := &screenModel{}
	received := make(chan byte, 1)
	hostLink, _ := netLabLink(t, nil, func(payload []byte) {
		if len(payload) > 0 && payload[0] == 0x07 {
			screen.askKey()
			received <- payload[0]
		}
	})
	viewLink, _ := netLabLink(t, nil, nil)
	host, err := newPeer(peerConfig{host: true, link: hostLink})
	if err != nil {
		t.Fatal(err)
	}
	defer host.close()
	view, err := newPeer(peerConfig{link: viewLink})
	if err != nil {
		t.Fatal(err)
	}
	defer view.close()
	offer, err := view.offer()
	if err != nil {
		t.Fatal(err)
	}
	answer, err := host.answer(offer)
	if err != nil {
		t.Fatal(err)
	}
	if err := view.accept(answer); err != nil {
		t.Fatal(err)
	}
	// This may run before SCTP opens: the real helper must queue and later deliver it,
	// rather than directly invoking the host model after an assumed one-way delay.
	viewer := &viewerModel{askKey: func() { view.sendCtl([]byte{0x07}) }}
	viewer.ask()
	select {
	case <-received:
		screen.mu.Lock()
		defer screen.mu.Unlock()
		if !screen.want {
			t.Fatal("received control request did not ask the synthetic host for a keyframe")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("viewer keyframe request did not cross the real control data channel")
	}
}
