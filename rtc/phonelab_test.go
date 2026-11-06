package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"math/rand/v2"
	"net/url"
	"os"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// TestPhoneLab runs a whole screen share over the real network with both ends on this PC: the
// sharing end and the viewing end each go through the phone helper (Phone VPN), the sharing end
// only by the TURN relay, so every packet crosses the phone's network to the relay and back, the
// way the screen reached this laptop from the home PC on 2026-11-06. A model of FramePlanner sends
// a screen that moves the whole time, at the bitrate the helper asks for, with keyframes when the
// viewer asks and the same backoff; a model of ViewerPipeline shows frames the way it does: after
// a loss nothing until the next keyframe, which it keeps asking for. Every 5 seconds it prints what
// the viewer saw and what the helpers said.
//
// Live only, it needs the phone:
//
//	PDANET_LAB_AGENT=127.0.0.1:<phone helper port> PDANET_LAB_DLL=<debug PdaNetPortable.dll>
//	PDANET_LAB_PROXY=127.0.0.1:12345 PDANET_LAB_SECONDS=45 go test -run TestPhoneLab -v
func TestPhoneLab(t *testing.T) {
	agent := os.Getenv("PDANET_LAB_AGENT")
	if agent == "" {
		t.Skip("live test, set PDANET_LAB_AGENT")
	}
	proxy := os.Getenv("PDANET_LAB_PROXY")
	from, err := labRelayURL(os.Getenv("PDANET_LAB_DLL"))
	if err != nil {
		t.Fatal(err)
	}
	seconds := 45
	if s := os.Getenv("PDANET_LAB_SECONDS"); s != "" {
		fmt.Sscan(s, &seconds)
	}
	var d dialer
	if proxy != "" {
		d.proxy, _ = url.Parse("http://" + proxy)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	servers, _, err := fetchedServers(ctx, d, from)
	cancel()
	if err != nil {
		t.Fatal("credentials: ", err)
	}

	hub := "mem+lab-" + randHex(4)
	base := options{Relays: []string{hub}, NoLAN: true, Turn: servers,
		Route: route{Mode: "phone", Tunnel: agent, Proxy: proxy}}
	hostOpt := base
	hostOpt.Op, hostOpt.Name, hostOpt.RelayOnly = "host", "Sharing PC", true
	host := startLabEnd(hostOpt)
	e, err := host.waitFor("code", 10*time.Second)
	if err != nil {
		t.Fatal("host: ", err)
	}
	viewOpt := base
	viewOpt.Op, viewOpt.Name, viewOpt.Code = "join", "Viewing PC", e.Code
	view := startLabEnd(viewOpt)
	var wg sync.WaitGroup
	var hostConn, viewConn event
	var hostErr, viewErr error
	wg.Add(2)
	go func() { defer wg.Done(); hostConn, hostErr = host.waitFor("connected", 40*time.Second) }()
	go func() { defer wg.Done(); viewConn, viewErr = view.waitFor("connected", 40*time.Second) }()
	wg.Wait()
	if hostErr != nil || viewErr != nil {
		t.Fatalf("connect: host %v, viewer %v", hostErr, viewErr)
	}
	t.Logf("connected: sharing end %s %s to %s (%s), viewing end %s %s to %s (%s)",
		hostConn.Path, hostConn.Local, hostConn.Remote, hostConn.Detail, viewConn.Path, viewConn.Local, viewConn.Remote, viewConn.Detail)

	enc := &labEncoder{bps: startBitrate, end: host}
	vw := &labViewer{end: view, start: time.Now()}
	var hostStats, viewStats atomic.Pointer[event]
	stop := make(chan struct{})
	go func() {
		for {
			select {
			case e, ok := <-host.events:
				if !ok {
					return
				}
				switch e.Ev {
				case "bitrate":
					enc.setBitrate(e.Bps)
				case "keyframe":
					enc.wantKey("pli")
				case "stats":
					hostStats.Store(&e)
				}
			case b := <-host.ctl:
				if len(b) > 0 && b[0] == 0x07 { // Msg.Keyframe, from the viewer's ctl channel
					enc.wantKey("ctl")
				}
			case <-stop:
				return
			}
		}
	}()
	go func() {
		for {
			select {
			case e, ok := <-view.events:
				if !ok {
					return
				}
				if e.Ev == "stats" {
					viewStats.Store(&e)
				}
			case <-stop:
				return
			}
		}
	}()
	go vw.run(stop)
	go enc.run(stop)

	window := 5 * time.Second
	begin := time.Now()
	var lastShown, lastSent, lastKeys, lastAsks int
	for time.Since(begin) < time.Duration(seconds)*time.Second {
		time.Sleep(window)
		shown, longest, asks := vw.window()
		sent, keys := enc.counts()
		hs, vs := hostStats.Load(), viewStats.Load()
		if hs == nil {
			hs = &event{}
		}
		if vs == nil {
			vs = &event{}
		}
		t.Logf("%3.0fs: viewer showed %4.1f fps (longest freeze %4d ms, %d keyframe asks) | sent %4.1f fps, %d keyframes | target %4d kbit/s (gcc %4d, burst %4d), queue %3d ms, twcc loss %4.1f %%, sending %4d, rtt %4.0f | viewer gets %4d kbit/s, rtp loss %4.1f %%",
			time.Since(begin).Seconds(), float64(shown-lastShown)/window.Seconds(), longest.Milliseconds(), asks-lastAsks,
			float64(sent-lastSent)/window.Seconds(), keys-lastKeys,
			hs.Target, hs.Gcc, hs.Capacity, hs.Queue, hs.Loss, hs.Send, hs.RTT, vs.Recv, vs.Loss)
		lastShown, lastSent, lastKeys, lastAsks = shown, sent, keys, asks
	}
	close(stop)
	shown, _, asks := vw.window()
	sent, keys := enc.counts()
	frozen := vw.frozen()
	t.Logf("in all: %d of %d frames shown, %d keyframes sent, %d keyframe asks, frozen (no new picture for 500 ms or more) %.0f %% of the time, longest freeze %d ms",
		shown, sent, keys, asks, 100*frozen.Seconds()/time.Since(vw.start).Seconds(), vw.longestEver().Milliseconds())
	_ = view.link.write(kindJSON, []byte(`{"op":"stop"}`))
	_ = host.link.write(kindJSON, []byte(`{"op":"stop"}`))
	time.Sleep(time.Second)
}

// labRelayURL finds the built-in relay address in a debug build's DLL, never printed.
func labRelayURL(dll string) (string, error) {
	raw, err := os.ReadFile(dll)
	if err != nil {
		return "", err
	}
	for off := 0; ; {
		i := bytes.Index(raw[off:], []byte("ShareRelayUrl"))
		if i < 0 {
			return "", fmt.Errorf("no relay address in %s", dll)
		}
		i += off
		off = i + 1
		j := bytes.Index(raw[i:], []byte("https://"))
		if j < 0 || j > 64 {
			continue
		}
		s := raw[i+j:]
		k := 0
		for k < len(s) && s[k] > 0x20 && s[k] < 0x7f {
			k++
		}
		return string(s[:k]), nil
	}
}

// labEnd is PdaNet as the helper sees it, with queues that never hold the helper back.
type labEnd struct {
	link    *ipc
	events  chan event
	ctl     chan []byte
	video   chan []byte
	dropped atomic.Int64
}

func startLabEnd(opt options) *labEnd {
	toHelperR, toHelperW := io.Pipe()
	fromHelperR, fromHelperW := io.Pipe()
	f := &labEnd{
		link:   newIPC(fromHelperR, toHelperW),
		events: make(chan event, 4096),
		ctl:    make(chan []byte, 256),
		video:  make(chan []byte, 4096),
	}
	go func() { runSession(toHelperR, fromHelperW); _ = fromHelperW.Close(); _ = toHelperR.Close() }()
	start, _ := json.Marshal(opt)
	_ = f.link.write(kindJSON, start)
	go func() {
		for {
			kind, b, err := f.link.read()
			if err != nil {
				close(f.events)
				return
			}
			b = append([]byte(nil), b...)
			switch kind {
			case kindJSON:
				var e event
				_ = json.Unmarshal(b, &e)
				f.offer(f.events, e)
			case kindCtl:
				select {
				case f.ctl <- b:
				default:
					f.dropped.Add(1)
				}
			case kindVideo:
				select {
				case f.video <- b:
				default:
					f.dropped.Add(1)
				}
			}
		}
	}()
	return f
}

func (f *labEnd) offer(ch chan event, e event) {
	select {
	case ch <- e:
	default:
		f.dropped.Add(1)
	}
}

func (f *labEnd) waitFor(ev string, d time.Duration) (event, error) {
	deadline := time.After(d)
	for {
		select {
		case e, ok := <-f.events:
			if !ok {
				return event{}, fmt.Errorf("helper stopped")
			}
			if e.Ev == "failed" {
				return e, fmt.Errorf("failed: %s %s", e.Reason, e.Detail)
			}
			if e.Ev == ev {
				return e, nil
			}
		case <-deadline:
			return event{}, fmt.Errorf("no %q within %v", ev, d)
		}
	}
}

// labEncoder is FramePlanner in short: a credit of 0.9 times the bitrate, a frame whenever the
// credit allows at up to 20 frames a second, keyframes at once when asked and as large as half a
// second of the link, again soon after only with the planner's backoff (KeyHeld).
type labEncoder struct {
	end *labEnd

	mu          sync.Mutex
	bps         int
	want        bool
	lastKeyAt   time.Time
	lastKeyBits float64
	streak      int
	sent, keys  int
}

func (e *labEncoder) setBitrate(bps int) {
	e.mu.Lock()
	e.bps = bps
	e.mu.Unlock()
}

func (e *labEncoder) wantKey(string) {
	e.mu.Lock()
	e.want = true
	e.mu.Unlock()
}

func (e *labEncoder) counts() (int, int) {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.sent, e.keys
}

// keyHeld is FramePlanner.KeyHeld.
func (e *labEncoder) keyHeld(now time.Time) bool {
	if e.lastKeyAt.IsZero() || e.streak == 0 || now.Sub(e.lastKeyAt) >= 5*time.Second {
		return false
	}
	gap := time.Duration(max(min(4000, 250<<min(e.streak, 4)), int(2*e.lastKeyBits/float64(e.bps)*1000))) * time.Millisecond
	return now.Sub(e.lastKeyAt) < gap
}

func (e *labEncoder) run(stop chan struct{}) {
	t := time.NewTicker(5 * time.Millisecond)
	defer t.Stop()
	last := time.Now()
	lastFrame := time.Now()
	credit := 0.0
	key := true // the first frame of a pipeline is a keyframe
	for {
		select {
		case <-stop:
			return
		case <-t.C:
		}
		now := time.Now()
		e.mu.Lock()
		bps := float64(e.bps)
		credit = min(credit+0.9*bps*now.Sub(last).Seconds(), 0.25*bps)
		last = now
		var size int
		isKey := false
		switch {
		case (key || e.want) && !e.keyHeld(now):
			isKey, key, e.want = true, false, false
			size = int(min(max(bps*0.5/8, 9000), 70000))
			if !e.lastKeyAt.IsZero() && now.Sub(e.lastKeyAt) < 5*time.Second {
				e.streak++
			} else {
				e.streak = 0
			}
			e.lastKeyAt, e.lastKeyBits = now, float64(size*8)
			e.keys++
		case credit > 0 && now.Sub(lastFrame) >= 50*time.Millisecond:
			size = int(min(max(0.9*bps/20/8, 600), 200000))
		}
		if size == 0 {
			e.mu.Unlock()
			continue
		}
		credit -= float64(size * 8)
		e.sent++
		e.mu.Unlock()

		dur := now.Sub(lastFrame)
		lastFrame = now
		hdr := make([]byte, 5)
		binary.LittleEndian.PutUint32(hdr, uint32(dur.Microseconds()))
		var frame []byte
		if isKey {
			hdr[4] = 1
			frame = append(frame, 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x34, 0xac, 0xd9, 0x40, 0x78)
			frame = append(frame, 0, 0, 0, 1, 0x68, 0xeb, 0xe3, 0xcb)
			frame = append(frame, 0, 0, 0, 1, 0x65)
		} else {
			frame = append(frame, 0, 0, 0, 1, 0x41)
		}
		for len(frame) < size {
			frame = append(frame, byte(0x80|rand.IntN(128)))
		}
		_ = e.end.link.write(kindVideo, hdr, frame)
	}
}

// labViewer is ViewerPipeline in short.
type labViewer struct {
	end   *labEnd
	start time.Time

	mu        sync.Mutex
	shown     int
	asks      int
	lastShown time.Time
	longest   time.Duration // in the current window
	ever      time.Duration
	frozenFor time.Duration
}

func (v *labViewer) run(stop chan struct{}) {
	waitKey := true
	var lastAsk time.Time
	for {
		var b []byte
		select {
		case <-stop:
			return
		case b = <-v.end.video:
		}
		if len(b) < 5 {
			continue
		}
		flags := b[0]
		if flags&frameAfterLoss != 0 {
			waitKey = true
		}
		now := time.Now()
		if waitKey && flags&frameKey == 0 {
			if now.Sub(lastAsk) >= 300*time.Millisecond {
				lastAsk = now
				_ = v.end.link.write(kindCtl, []byte{0x07})
				v.mu.Lock()
				v.asks++
				v.mu.Unlock()
			}
			continue
		}
		waitKey = false
		v.mu.Lock()
		v.shown++
		from := v.lastShown
		if from.IsZero() {
			from = v.start
		}
		gap := now.Sub(from)
		v.longest, v.ever = max(v.longest, gap), max(v.ever, gap)
		if gap >= 500*time.Millisecond {
			v.frozenFor += gap
		}
		v.lastShown = now
		v.mu.Unlock()
	}
}

// window gives the frames shown and asks so far, and the longest freeze since the last call.
func (v *labViewer) window() (int, time.Duration, int) {
	v.mu.Lock()
	defer v.mu.Unlock()
	longest := v.longest
	if !v.lastShown.IsZero() {
		longest = max(longest, time.Since(v.lastShown))
	} else {
		longest = time.Since(v.start)
	}
	v.longest = 0
	return v.shown, longest, v.asks
}

func (v *labViewer) frozen() time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	f := v.frozenFor
	if gap := time.Since(v.lastShown); !v.lastShown.IsZero() && gap >= 500*time.Millisecond {
		f += gap
	}
	return f
}

func (v *labViewer) longestEver() time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	return v.ever
}
