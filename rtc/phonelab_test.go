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
	"slices"
	"strconv"
	"strings"
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
//	PDANET_LAB_AGENT=127.0.0.1:<phone helper port> PDANET_LAB_FROM=<file with the relay's web address>
//	PDANET_LAB_PROXY=127.0.0.1:12345 PDANET_LAB_SECONDS=45 go test -run TestPhoneLab -v
func TestPhoneLab(t *testing.T) {
	agent := os.Getenv("PDANET_LAB_AGENT")
	if agent == "" {
		t.Skip("live test, set PDANET_LAB_AGENT")
	}
	runLiveLab(t, route{Mode: "phone", Tunnel: agent, Proxy: os.Getenv("PDANET_LAB_PROXY")}, false, 20)
}

// TestRelayLab measures the real TURN transport without needing a phone. Both
// endpoints run here and must use the configured relay, so timestamps share a
// monotonic clock. H.264-shaped payloads exercise packetization, encryption,
// pacing, loss repair, assembly and IPC; capture, codecs and display are excluded.
// Opt in with PDANET_LAB_FROM pointing to a file containing the credential URL.
// PDANET_LAB_TRANSPORT optionally selects udp, tcp or tls; PDANET_LAB_FPS defaults
// to 60; PDANET_LAB_BPS optionally holds a fixed offered bitrate instead of GCC.
func TestRelayLab(t *testing.T) {
	if os.Getenv("PDANET_LAB_FROM") == "" {
		t.Skip("live test, set PDANET_LAB_FROM to a credential URL file")
	}
	runLiveLab(t, route{Mode: "direct"}, true, 60)
}

func runLiveLab(t *testing.T, path route, forceBoth bool, defaultFPS int) {
	t.Helper()
	proxy := os.Getenv("PDANET_LAB_PROXY")
	from, err := labRelayURL(os.Getenv("PDANET_LAB_FROM"))
	if err != nil {
		t.Fatal(err)
	}
	seconds := labInt(t, "PDANET_LAB_SECONDS", 45, 1, 3600)
	fps := labInt(t, "PDANET_LAB_FPS", defaultFPS, 1, 120)
	fixedBPS := labInt(t, "PDANET_LAB_BPS", 0, 0, maxBitrate)
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
	transport := os.Getenv("PDANET_LAB_TRANSPORT")
	if transport != "" {
		if transport != "udp" && transport != "tcp" && transport != "tls" {
			t.Fatal("PDANET_LAB_TRANSPORT must be udp, tcp or tls")
		}
		for i := range servers {
			servers[i].URLs = slices.DeleteFunc(servers[i].URLs, func(u string) bool {
				tls := strings.HasPrefix(u, "turns:")
				tcp := strings.Contains(u, "transport=tcp")
				return (transport == "tls" && !tls) || (transport == "tcp" && (tls || !tcp)) || (transport == "udp" && (tls || tcp))
			})
		}
		servers = slices.DeleteFunc(servers, func(s iceServer) bool { return len(s.URLs) == 0 })
		if len(servers) == 0 {
			t.Fatal("no relay URL supports the selected transport")
		}
	}

	hub := "mem+lab-" + randHex(4)
	base := options{Relays: []string{hub}, NoLAN: true, Turn: servers,
		Route: path, RelayOnly: forceBoth}
	hostOpt := base
	hostOpt.Op, hostOpt.Name, hostOpt.RelayOnly = "host", "Sharing PC", true
	host := startLabEnd(hostOpt)
	t.Cleanup(func() { host.close(t) })
	e, err := host.waitFor("code", 10*time.Second)
	if err != nil {
		t.Fatal("host: ", err)
	}
	viewOpt := base
	viewOpt.Op, viewOpt.Name, viewOpt.Code = "join", "Viewing PC", e.Code
	view := startLabEnd(viewOpt)
	t.Cleanup(func() { view.close(t) })
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
	if forceBoth && (hostConn.Path != "relay" || viewConn.Path != "relay") {
		t.Fatalf("forced TURN test escaped the relay: host %s viewer %s", hostConn.Path, viewConn.Path)
	}
	t.Logf("connected: sharing end %s %s to %s (%s), viewing end %s %s to %s (%s)",
		hostConn.Path, hostConn.Local, hostConn.Remote, hostConn.Detail, viewConn.Path, viewConn.Local, viewConn.Remote, viewConn.Detail)

	origin := time.Now()
	enc := &labEncoder{bps: startBitrate, fixedBPS: fixedBPS, fps: fps, end: host, origin: origin}
	if fixedBPS > 0 {
		enc.bps = fixedBPS
	}
	vw := &labViewer{end: view, start: origin}
	t.Logf("workload: %d fps maximum, fixed bitrate %d bit/s (0 follows GCC), transport %q; synthetic payloads, no codecs or display", fps, fixedBPS, transport)
	var hostStats, viewStats atomic.Pointer[event]
	stop := make(chan struct{})
	defer close(stop)
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
	deadline := begin.Add(time.Duration(seconds) * time.Second)
	lastLog := begin
	var lastShown, lastSent, lastKeys, lastAsks int
	for time.Now().Before(deadline) {
		time.Sleep(min(window, time.Until(deadline)))
		now := time.Now()
		interval := now.Sub(lastLog)
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
			time.Since(begin).Seconds(), float64(shown-lastShown)/interval.Seconds(), longest.Milliseconds(), asks-lastAsks,
			float64(sent-lastSent)/interval.Seconds(), keys-lastKeys,
			hs.Target, hs.Gcc, hs.Capacity, hs.Queue, hs.Loss, hs.Send, hs.RTT, vs.Recv, vs.Loss)
		lastShown, lastSent, lastKeys, lastAsks = shown, sent, keys, asks
		lastLog = now
	}
	shown, _, asks := vw.window()
	sent, keys := enc.counts()
	frozen := vw.frozen()
	t.Logf("in all: %d of %d frames shown, %d keyframes sent, %d keyframe asks, frozen (no new picture for 500 ms or more) %.0f %% of the time, longest freeze %d ms",
		shown, sent, keys, asks, 100*frozen.Seconds()/time.Since(vw.start).Seconds(), vw.longestEver().Milliseconds())
	latency := vw.latencySummary(0)
	t.Logf("send-to-receive: samples=%d median=%d ms p95=%d ms max=%d ms; delivered=%.1f fps; lab drops host=%d viewer=%d",
		latency.count, latency.median.Milliseconds(), latency.p95.Milliseconds(), latency.max.Milliseconds(),
		float64(shown)/time.Since(begin).Seconds(), host.dropped.Load(), view.dropped.Load())
	steady := vw.latencySummary(5 * time.Second)
	if seconds > 5 {
		t.Logf("first picture=%d ms; after 5s warmup: samples=%d median=%d ms p95=%d ms max=%d ms delivered=%.1f fps",
			vw.firstPicture().Milliseconds(), steady.count, steady.median.Milliseconds(), steady.p95.Milliseconds(), steady.max.Milliseconds(),
			float64(steady.count)/(time.Since(begin).Seconds()-5))
	}
	shownBPS, receivedBPS := vw.averageBitrates(time.Since(begin))
	hs, vs := hostStats.Load(), viewStats.Load()
	if hs == nil {
		hs = &event{}
	}
	if vs == nil {
		vs = &event{}
	}
	t.Logf("payload averages: accepted=%d bit/s reassembled=%d bit/s; final target=%d kbit/s GCC=%d burst=%d queue=%d ms TWCC loss=%.1f%% sender=%d kbit/s receiver=%d kbit/s RTP loss=%.1f%%",
		shownBPS, receivedBPS, hs.Target, hs.Gcc, hs.Capacity, hs.Queue, hs.Loss, hs.Send, vs.Recv, vs.Loss)
	if shown == 0 || latency.count == 0 {
		t.Error("no timestamped frame delivered")
	}
	if host.dropped.Load()+view.dropped.Load() > 0 {
		t.Error("lab queues dropped data; measurement is incomplete")
	}
}

func labInt(t *testing.T, name string, fallback, low, high int) int {
	t.Helper()
	if s := os.Getenv(name); s != "" {
		n, err := strconv.Atoi(s)
		if err != nil || n < low || n > high {
			t.Fatalf("%s must be an integer from %d to %d", name, low, high)
		}
		return n
	}
	return fallback
}

// labRelayURL reads the relay's web address from a file, never printed: the one Settings takes
// under Relay from a web address.
func labRelayURL(file string) (string, error) {
	raw, err := os.ReadFile(file)
	if err != nil {
		return "", err
	}
	from := string(bytes.TrimSpace(raw))
	if !strings.HasPrefix(from, "https://") {
		return "", fmt.Errorf("%s holds no https address", file)
	}
	return from, nil
}

// labEnd is PdaNet as the helper sees it, with queues that never hold the helper back.
type labEnd struct {
	link    *ipc
	events  chan event
	ctl     chan []byte
	video   chan []byte
	dropped atomic.Int64
	input   *io.PipeWriter
	output  *io.PipeReader
	done    chan struct{}
	once    sync.Once
}

func startLabEnd(opt options) *labEnd {
	toHelperR, toHelperW := io.Pipe()
	fromHelperR, fromHelperW := io.Pipe()
	f := &labEnd{
		link:   newIPC(fromHelperR, toHelperW),
		events: make(chan event, 4096),
		ctl:    make(chan []byte, 256),
		video:  make(chan []byte, 4096),
		input:  toHelperW,
		output: fromHelperR,
		done:   make(chan struct{}),
	}
	go func() {
		defer close(f.done)
		runSession(toHelperR, fromHelperW)
		_ = fromHelperW.Close()
		_ = toHelperR.Close()
	}()
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

func (f *labEnd) close(t *testing.T) {
	t.Helper()
	f.once.Do(func() {
		_ = f.input.Close()
		_ = f.output.Close()
	})
	select {
	case <-f.done:
	case <-time.After(5 * time.Second):
		t.Error("lab helper did not stop after its pipes closed")
	}
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

// labEncoder is a synthetic sender workload: credit of 0.9 times the bitrate,
// frames up to the requested FPS, and large keyframes with the planner's retry
// backoff. Fixed-rate mode deliberately ignores bitrate adaptation to stress the
// transport. These are not measurements of the real encoder or FramePlanner.
type labEncoder struct {
	end           *labEnd
	origin        time.Time
	fixedBPS, fps int

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
	if e.fixedBPS == 0 {
		e.bps = bps
	}
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
	t := time.NewTicker(time.Millisecond)
	defer t.Stop()
	last := time.Now()
	lastFrame := time.Now()
	period := time.Second / time.Duration(e.fps)
	nextFrame := lastFrame.Add(period)
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
		case credit > 0 && !now.Before(nextFrame):
			size = int(min(max(0.9*bps/float64(e.fps)/8, 600), 200000))
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
		nextFrame = nextFrame.Add(period)
		if isKey || !nextFrame.After(now) {
			nextFrame = now.Add(period)
		}
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
		frame = append(frame, fmt.Sprintf("pdanet-lab:%016x", now.Sub(e.origin).Nanoseconds())...)
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

	mu                        sync.Mutex
	shown                     int
	asks                      int
	lastShown                 time.Time
	longest                   time.Duration // in the current window
	ever                      time.Duration
	frozenFor                 time.Duration
	first                     time.Time
	latencies                 []labLatencySample
	shownBytes, receivedBytes int64
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
		v.mu.Lock()
		v.receivedBytes += int64(len(b) - 5)
		v.mu.Unlock()
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
		v.shownBytes += int64(len(b) - 5)
		if v.first.IsZero() {
			v.first = now
		}
		if stamp := bytes.Index(b[5:], []byte("pdanet-lab:")); stamp >= 0 {
			at := 5 + stamp + len("pdanet-lab:")
			if at+16 <= len(b) {
				if sent, err := strconv.ParseInt(string(b[at:at+16]), 16, 64); err == nil {
					v.latencies = append(v.latencies, labLatencySample{time.Duration(sent), now.Sub(v.start) - time.Duration(sent)})
				}
			}
		}
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

type labLatency struct {
	count            int
	median, p95, max time.Duration
}

type labLatencySample struct {
	sent, delay time.Duration
}

func (v *labViewer) averageBitrates(elapsed time.Duration) (shown, received int64) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if elapsed <= 0 {
		return 0, 0
	}
	return int64(float64(v.shownBytes*8) / elapsed.Seconds()), int64(float64(v.receivedBytes*8) / elapsed.Seconds())
}

func (v *labViewer) firstPicture() time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	if v.first.IsZero() {
		return time.Since(v.start)
	}
	return v.first.Sub(v.start)
}

func (v *labViewer) latencySummary(after time.Duration) labLatency {
	v.mu.Lock()
	defer v.mu.Unlock()
	a := make([]time.Duration, 0, len(v.latencies))
	for _, sample := range v.latencies {
		if sample.sent >= after {
			a = append(a, sample.delay)
		}
	}
	slices.Sort(a)
	if len(a) == 0 {
		return labLatency{}
	}
	return labLatency{len(a), a[(len(a)-1)/2], a[(95*len(a)+99)/100-1], a[len(a)-1]}
}

// window gives the frames shown and asks so far, and the longest freeze since the last call.
func (v *labViewer) window() (int, time.Duration, int) {
	v.mu.Lock()
	defer v.mu.Unlock()
	longest := max(v.longest, v.gapLocked(time.Now()))
	v.longest = 0
	return v.shown, longest, v.asks
}

func (v *labViewer) frozen() time.Duration {
	return v.frozenAt(time.Now())
}

func (v *labViewer) gapLocked(now time.Time) time.Duration {
	from := v.lastShown
	if from.IsZero() {
		from = v.start
	}
	return max(0, now.Sub(from))
}

func (v *labViewer) frozenAt(now time.Time) time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	f := v.frozenFor
	if gap := v.gapLocked(now); gap >= 500*time.Millisecond {
		f += gap
	}
	return f
}

func (v *labViewer) longestEver() time.Duration {
	return v.longestAt(time.Now())
}

func (v *labViewer) longestAt(now time.Time) time.Duration {
	v.mu.Lock()
	defer v.mu.Unlock()
	return max(v.ever, v.gapLocked(now))
}

func TestLabLatencySummary(t *testing.T) {
	v := &labViewer{}
	if got := v.latencySummary(0); got != (labLatency{}) {
		t.Fatalf("empty summary: %+v", got)
	}
	for i := 1; i <= 100; i++ {
		v.latencies = append(v.latencies, labLatencySample{time.Duration(i) * time.Second, time.Duration(101-i) * time.Millisecond})
	}
	if got, want := v.latencySummary(0), (labLatency{100, 50 * time.Millisecond, 95 * time.Millisecond, 100 * time.Millisecond}); got != want {
		t.Fatalf("whole run: %+v, want %+v", got, want)
	}
	if got, want := v.latencySummary(51*time.Second), (labLatency{50, 25 * time.Millisecond, 48 * time.Millisecond, 50 * time.Millisecond}); got != want {
		t.Fatalf("after warmup: %+v, want %+v", got, want)
	}
	if got := v.latencySummary(101 * time.Second); got != (labLatency{}) {
		t.Fatalf("empty tail: %+v", got)
	}
	v.shownBytes, v.receivedBytes = 125_000, 250_000
	if shown, received := v.averageBitrates(2 * time.Second); shown != 500_000 || received != 1_000_000 {
		t.Fatalf("payload bitrates %d/%d, want 500000/1000000", shown, received)
	}
}

func TestLabFinalGap(t *testing.T) {
	start := time.Unix(0, 0)
	v := &labViewer{start: start}
	now := start.Add(3 * time.Second)
	if got := v.longestAt(now); got != 3*time.Second {
		t.Fatalf("no first picture: longest %v", got)
	}
	if got := v.frozenAt(now); got != 3*time.Second {
		t.Fatalf("no first picture: frozen %v", got)
	}
	v.lastShown = start.Add(time.Second)
	v.ever, v.frozenFor = 750*time.Millisecond, 750*time.Millisecond
	if got := v.longestAt(now); got != 2*time.Second {
		t.Fatalf("final gap missing from longest freeze: %v", got)
	}
	if got := v.frozenAt(now); got != 2750*time.Millisecond {
		t.Fatalf("final gap missing from frozen time: %v", got)
	}
	if got := v.frozenAt(v.lastShown.Add(100 * time.Millisecond)); got != 750*time.Millisecond {
		t.Fatalf("ordinary frame interval counted as frozen: %v", got)
	}
}
