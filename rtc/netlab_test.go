package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"math/rand/v2"
	"net"
	"os"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/pion/logging"
	"github.com/pion/transport/v5/vnet"
)

// TestNetLab runs a whole screen session through an emulated path in this process: the real
// peers, pacer, link meter, congestion control, loss repair and frame assembly, with the
// network replaced by Pion's virtual one. The PC that views gets the screen through a
// bottleneck with a buffer, both directions have a one-way delay, and the path can add jitter
// and lose packets. The sender is FramePlanner in short (bursty: a keyframe, saved credit,
// motion frames, refines at rest), the receiver ViewerPipeline's queue and decoder in short.
//
// Opt in with PDANET_NETLAB=all or a comma separated list of profile names; the test takes
// PDANET_NETLAB_SECONDS (40) for each. Results go to the log, one line per profile.
func TestNetLab(t *testing.T) {
	want := os.Getenv("PDANET_NETLAB")
	if want == "" {
		t.Skip("emulated network test, set PDANET_NETLAB=all or a list of profiles")
	}
	seconds := 40
	if s := os.Getenv("PDANET_NETLAB_SECONDS"); s != "" {
		if n, err := strconv.Atoi(s); err == nil && n >= 10 {
			seconds = n
		}
	}
	if os.Getenv("PDANET_NETLAB_METER") != "" { // each burst the link meter timed
		meterTrace = log.Printf
		defer func() { meterTrace = nil }()
	}
	for _, p := range netProfiles {
		if want != "all" && !slices.Contains(strings.Split(want, ","), p.name) {
			continue
		}
		t.Run(p.name, func(t *testing.T) {
			r := runNetLab(t, p, time.Duration(seconds)*time.Second)
			t.Logf("%s: %s", p.name, r)
		})
	}
}

// TestRelayScreenLab runs the same screen through the TURN relay the Settings use, both peers
// in this process and forced onto the relay, over the real network. Opt in with PDANET_LAB_FROM
// naming a file that holds the relay's https credential address; PDANET_NETLAB_SECONDS (40).
func TestRelayScreenLab(t *testing.T) {
	file := os.Getenv("PDANET_LAB_FROM")
	if file == "" {
		t.Skip("live relay test, set PDANET_LAB_FROM to a file with the credential address")
	}
	from, err := labRelayURL(file)
	if err != nil {
		t.Fatal(err)
	}
	servers, _, err := fetchedServers(context.Background(), dialer{}, from)
	if err != nil {
		t.Fatal("credentials: ", err)
	}
	if only := os.Getenv("PDANET_LAB_TRANSPORT"); only != "" {
		for i := range servers {
			servers[i].URLs = slices.DeleteFunc(servers[i].URLs, func(u string) bool {
				tls, tcp := strings.HasPrefix(u, "turns:"), strings.Contains(u, "transport=tcp")
				return (only == "tls") != tls || (only == "tcp") != (tcp && !tls)
			})
		}
		servers = slices.DeleteFunc(servers, func(s iceServer) bool { return len(s.URLs) == 0 })
	}
	seconds := 40
	if s := os.Getenv("PDANET_NETLAB_SECONDS"); s != "" {
		if n, err := strconv.Atoi(s); err == nil && n >= 10 {
			seconds = n
		}
	}
	cfg := peerConfig{turn: servers, relayOnly: true}
	hostCfg := cfg
	hostCfg.host = true
	r := runScreenSession(t, hostCfg, cfg, time.Duration(seconds)*time.Second)
	t.Logf("relay: %s", r)
}

// netProfile is a path: the bottleneck towards the PC that views and its buffer, the one-way
// delay each way, jitter on top, and random loss towards the PC that views.
type netProfile struct {
	name    string
	bps     int
	buffer  time.Duration // the bottleneck's queue, in time at bps
	delay   time.Duration // one way, each way
	jitter  time.Duration
	lossPct float64
	halve   bool // the bottleneck drops to half for 4 of every 8 seconds
}

var netProfiles = []netProfile{
	// The relay sessions of 2026-11-07: about 190 ms round trip when idle, a deep buffer.
	{name: "relay", bps: 3_000_000, buffer: 300 * time.Millisecond, delay: 95 * time.Millisecond, jitter: 20 * time.Millisecond},
	{name: "slow", bps: 1_200_000, buffer: 500 * time.Millisecond, delay: 140 * time.Millisecond, jitter: 30 * time.Millisecond},
	{name: "lossy", bps: 4_000_000, buffer: 150 * time.Millisecond, delay: 60 * time.Millisecond, jitter: 8 * time.Millisecond, lossPct: 2},
	{name: "fast", bps: 20_000_000, buffer: 80 * time.Millisecond, delay: 12 * time.Millisecond, jitter: 2 * time.Millisecond},
	// Wi-Fi or a busy relay: the delay jumps by up to 80 ms, with nothing lost.
	{name: "jittery", bps: 3_000_000, buffer: 400 * time.Millisecond, delay: 95 * time.Millisecond, jitter: 80 * time.Millisecond},
	// Someone else on the line: every 8 seconds the link drops to half for 4 seconds.
	{name: "varying", bps: 3_000_000, buffer: 300 * time.Millisecond, delay: 95 * time.Millisecond, jitter: 15 * time.Millisecond, halve: true},
	// The slowest relay sessions of 2026-11-07: a 400 ms round trip, and a little loss.
	{name: "far", bps: 3_000_000, buffer: 300 * time.Millisecond, delay: 200 * time.Millisecond, jitter: 20 * time.Millisecond, lossPct: 1},
}

type netLabResult struct {
	targetKbps, minTargetKbps, deliveredKbps float64
	motionFps                                float64
	p50, p95, max                            time.Duration
	keys, plis, resets                       int
	frozen, longest                          time.Duration
}

func (r netLabResult) String() string {
	return fmt.Sprintf("target %4.0f kbit/s (lowest %4.0f), delivered %4.0f kbit/s, %4.1f fps in motion, latency p50 %3d ms p95 %4d ms max %4d ms, %2d keyframes (%2d asked by PLI, %2d viewer queue resets), frozen %4.1f s, longest freeze %4d ms",
		r.targetKbps, r.minTargetKbps, r.deliveredKbps, r.motionFps, r.p50.Milliseconds(), r.p95.Milliseconds(), r.max.Milliseconds(),
		r.keys, r.plis, r.resets, r.frozen.Seconds(), r.longest.Milliseconds())
}

func runNetLab(t *testing.T, p netProfile, length time.Duration) netLabResult {
	quiet := logging.NewDefaultLoggerFactory()
	quiet.DefaultLogLevel = logging.LogLevelDisabled
	// The router's own jitter sleeps while it holds the lock every sender needs, which throttles
	// the link instead of delaying it; the delay filters vary the delay instead, in order.
	wan, err := vnet.NewRouter(&vnet.RouterConfig{CIDR: "10.9.0.0/24", LoggerFactory: quiet})
	if err != nil {
		t.Fatal(err)
	}
	hostNet, err := vnet.NewNet(&vnet.NetConfig{StaticIPs: []string{"10.9.0.2"}})
	if err != nil {
		t.Fatal(err)
	}
	viewNet, err := vnet.NewNet(&vnet.NetConfig{StaticIPs: []string{"10.9.0.3"}})
	if err != nil {
		t.Fatal(err)
	}
	queueBytes := int64(float64(p.bps) / 8 * p.buffer.Seconds())
	tbf := vnet.NewTBFQueue(p.bps, 3000, queueBytes)
	viewQ, err := vnet.NewQueue(viewNet, tbf)
	if err != nil {
		t.Fatal(err)
	}
	if p.halve {
		done := make(chan struct{})
		defer close(done)
		go func() {
			t := time.NewTicker(4 * time.Second)
			defer t.Stop()
			for half := false; ; half = !half {
				select {
				case <-done:
					return
				case <-t.C:
				}
				if half {
					tbf.SetRate(p.bps / 2)
				} else {
					tbf.SetRate(p.bps)
				}
			}
		}()
	}
	viewD, err := vnet.NewDelayFilter(viewQ, vnet.WithDelay(p.delay))
	if err != nil {
		t.Fatal(err)
	}
	hostD, err := vnet.NewDelayFilter(hostNet, vnet.WithDelay(p.delay))
	if err != nil {
		t.Fatal(err)
	}
	if err := wan.AddNet(viewD); err != nil {
		t.Fatal(err)
	}
	if err := wan.AddNet(hostD); err != nil {
		t.Fatal(err)
	}
	if p.lossPct > 0 {
		wan.AddChunkFilter(func(c vnet.Chunk) bool {
			if a, ok := c.DestinationAddr().(*net.UDPAddr); ok && a.IP.Equal(net.IPv4(10, 9, 0, 3)) {
				return rand.Float64()*100 >= p.lossPct
			}
			return true
		})
	}
	if p.jitter > 0 {
		done := make(chan struct{})
		defer close(done)
		go func() {
			t := time.NewTicker(20 * time.Millisecond)
			defer t.Stop()
			for {
				select {
				case <-done:
					return
				case <-t.C:
				}
				viewD.SetDelay(p.delay + rand.N(p.jitter))
				hostD.SetDelay(p.delay + rand.N(p.jitter))
			}
		}()
	}
	if err := wan.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() {
		_ = wan.Stop()
		_ = viewD.Close()
		_ = hostD.Close()
		_ = viewQ.Close()
	}()

	return runScreenSession(t, peerConfig{host: true, vnet: hostNet}, peerConfig{vnet: viewNet}, length)
}

// runScreenSession links two peers configured so, runs the screen and the viewer models
// between them for length and says how it went. Viewer keyframe requests use the real
// reliable control data channel, including its actual network delay and recovery.
// The screen contents and decoder remain synthetic models, not captured/decoded video.
func runScreenSession(t *testing.T, hostCfg, viewCfg peerConfig, length time.Duration) netLabResult {
	oldPlanner, oldViewer := netLabModel()
	model := &viewerModel{start: time.Now(), maxFrames: 64, maxAge: 250 * time.Millisecond}
	screen := &screenModel{origin: model.start, bps: startBitrate, maxFps: 30, share: !oldPlanner}
	hostLink, hostEvents := netLabLink(t, nil, func(payload []byte) {
		if len(payload) > 0 && payload[0] == 0x07 { // Msg.Keyframe, as the real host handles it.
			screen.askKey()
		}
	})
	if oldViewer {
		model.maxFrames, model.maxAge = 8, 100*time.Millisecond
	}
	viewLink, _ := netLabLink(t, model.receive, nil)
	hostCfg.link, viewCfg.link = hostLink, viewLink
	host, err := newPeer(hostCfg)
	if err != nil {
		t.Fatal(err)
	}
	defer host.close()
	view, err := newPeer(viewCfg)
	if err != nil {
		t.Fatal(err)
	}
	defer view.close()
	screen.host = host
	model.askKey = func() { view.sendCtl([]byte{0x07}) }
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

	connected := time.After(15 * time.Second)
	var target []targetSample
	var plis int
	stats := map[string]float64{}
	// Events from the PC that shares: the bitrate for the encoder, keyframes asked for.
	handle := func(e event) bool {
		switch e.Ev {
		case "connected":
			if e.Path != "" && testing.Verbose() {
				t.Logf("connected %s, %s to %s, %s", e.Path, e.Local, e.Remote, e.Detail)
			}
			return true
		case "bitrate":
			screen.setBitrate(e.Bps)
			target = append(target, targetSample{time.Now(), e.Bps})
		case "keyframe":
			plis++
			screen.askKey()
		case "stats":
			stats["gcc"], stats["capacity"], stats["queue"], stats["rtt"] = float64(e.Gcc), float64(e.Capacity), float64(e.Queue), e.RTT
			if testing.Verbose() && os.Getenv("PDANET_NETLAB_TRACE") != "" {
				t.Logf("%5.1fs target %5d gcc %5d burst %5d queue %4d ms rtt %4.0f loss %4.1f sending %5d",
					time.Since(model.start).Seconds(), e.Target, e.Gcc, e.Capacity, e.Queue, e.RTT, e.Loss, e.Send)
			}
		}
		return false
	}
wait:
	for {
		select {
		case e := <-hostEvents:
			if handle(e) {
				break wait
			}
		case <-connected:
			t.Fatal("the emulated peers did not connect")
		}
	}

	// Establish the clock before either worker can send or display a frame.
	origin := time.Now()
	model.start = origin
	screen.origin, screen.phaseStart = origin, origin
	target = append(target, targetSample{origin, int(screen.currentBps())})
	stop := make(chan struct{})
	go screen.run(stop)
	go model.decode(stop)
	end := time.NewTimer(length)
	defer end.Stop()
run:
	for {
		select {
		case e := <-hostEvents:
			handle(e)
		case <-end.C:
			break run
		}
	}
	close(stop)
	time.Sleep(50 * time.Millisecond)
	return model.resultAt(target, screen, plis, 5*time.Second, length)
}

// netLabModel says which sender and receiver the models stand for: PDANET_NETLAB_MODEL=old is
// the planner that spent saved credit on every moving frame and the 8 frame, 100 ms viewer
// queue; anything else the planner that keeps a motion's frames to their share and the 64
// frame, 250 ms queue.
func netLabModel() (oldPlanner, oldViewer bool) {
	switch os.Getenv("PDANET_NETLAB_MODEL") {
	case "old":
		return true, true
	case "oldviewer":
		return false, true
	}
	return false, false
}

type targetSample struct {
	at  time.Time
	bps int
}

// netLabLink is an IPC link whose output is read here: events go to the channel,
// video to onVideo and received reliable data-channel messages to onControl.
func netLabLink(t *testing.T, onVideo func(flags byte, data []byte), onControl func([]byte)) (*ipc, chan event) {
	pr, pw := io.Pipe()
	events := make(chan event, 4096)
	go func() {
		in := newIPC(pr, io.Discard)
		for {
			kind, payload, err := in.read()
			if err != nil {
				return
			}
			switch kind {
			case kindJSON:
				var e event
				if json.Unmarshal(payload, &e) == nil {
					select {
					case events <- e:
					default:
					}
				}
			case kindVideo:
				if onVideo != nil && len(payload) >= 5 {
					onVideo(payload[0], append([]byte(nil), payload[5:]...))
				}
			case kindCtl:
				if onControl != nil {
					onControl(payload)
				}
			}
		}
	}()
	t.Cleanup(func() { _ = pw.Close() })
	return newIPC(bytes.NewReader(nil), pw), events
}

// screenModel is FramePlanner in short, for the Balanced style: frames take what the link
// carries, and a resting screen saves up some of it for the next change. It cycles through
// three seconds of motion (a page that scrolls, every frame as large as its budget) and two
// of rest (a seal, then refine frames that sharpen what moved).
type screenModel struct {
	host   *peer
	origin time.Time
	maxFps int
	share  bool // a motion's later frames take their share, not the saved credit

	mu                  sync.Mutex
	bps                 float64
	want                bool
	credit, queued      float64
	last, lastFrame     time.Time
	lastKeyAt           time.Time
	lastKeyBits         float64
	streak, refines     int
	phaseStart          time.Time
	sealed, started     bool
	lastMotion          bool
	frames, keys        int
	motionFrames        int
	motionTime, restDur time.Duration
}

const (
	planShare   = 0.9
	planSaveUp  = 0.4
	planKeyDebt = 0.5
	planRefine  = 0.3
	planMotion  = 0.10
	motionFor   = 3 * time.Second
	restFor     = 2 * time.Second
)

func (s *screenModel) setBitrate(bps int) {
	s.mu.Lock()
	s.bps = float64(max(bps, 150_000))
	s.mu.Unlock()
}

func (s *screenModel) currentBps() float64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.bps
}

func (s *screenModel) askKey() {
	s.mu.Lock()
	s.want = true
	s.mu.Unlock()
}

func (s *screenModel) moving(now time.Time) bool {
	return now.Sub(s.phaseStart)%(motionFor+restFor) < motionFor
}

// keyHeld is FramePlanner.KeyHeld.
func (s *screenModel) keyHeld(now time.Time, rate float64) bool {
	if s.lastKeyAt.IsZero() || s.streak == 0 || now.Sub(s.lastKeyAt) >= 5*time.Second {
		return false
	}
	gap := time.Duration(max(min(4000, 250<<min(s.streak, 4)), int(2*s.lastKeyBits/rate*1000))) * time.Millisecond
	return now.Sub(s.lastKeyAt) < gap
}

func (s *screenModel) run(stop chan struct{}) {
	t := time.NewTicker(time.Millisecond)
	defer t.Stop()
	for {
		select {
		case <-stop:
			return
		case <-t.C:
		}
		if size, key := s.next(time.Now()); size > 0 {
			s.send(size, key)
		}
	}
}

// next is the planner's decision at now: a frame of this many bytes, or 0.
func (s *screenModel) next(now time.Time) (int, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	rate := s.bps * planShare
	if !s.last.IsZero() {
		dt := now.Sub(s.last).Seconds()
		s.credit = min(s.credit+rate*dt, rate*planSaveUp)
		s.queued = max(0, s.queued-rate*dt)
	} else {
		s.credit = rate * planSaveUp
	}
	s.last = now
	debt := max(s.queued, -s.credit) > 0
	var bits float64
	key := false
	switch {
	case !s.started || s.want:
		if s.keyHeld(now, rate) || (s.started && -s.credit > rate*planKeyDebt) {
			return 0, false
		}
		key, s.want, s.started, s.lastMotion = true, false, true, false
		bits = max(s.credit, 0) + rate*planKeyDebt
		if !s.lastKeyAt.IsZero() && now.Sub(s.lastKeyAt) < 5*time.Second {
			s.streak++
		} else {
			s.streak = 0
		}
		s.lastKeyAt, s.lastKeyBits = now, bits
		s.keys++
	case s.moving(now):
		if debt || now.Sub(s.lastFrame) < time.Second/time.Duration(s.maxFps) {
			return 0, false
		}
		perFrame := rate / float64(s.maxFps)
		if s.share && s.lastMotion && now.Sub(s.lastFrame) <= 250*time.Millisecond {
			bits = perFrame
		} else {
			bits = min(max(perFrame, s.credit), rate*planMotion)
		}
		s.sealed, s.refines, s.lastMotion = false, 0, true
		s.motionFrames++
	case !s.sealed:
		s.sealed = true
		bits = 600
	default:
		// Refines: 120 ms after the last change, then every 200 ms, each once the one before drained.
		since := now.Sub(s.phaseStart)%(motionFor+restFor) - motionFor
		due := 120*time.Millisecond + time.Duration(s.refines)*200*time.Millisecond
		if s.refines >= 3 || since < due || debt {
			return 0, false
		}
		bits = min(max(s.credit, 0)+rate*planRefine, rate*[]float64{0.25, 0.12, 0.05}[s.refines])
		s.refines++
		s.lastMotion = false
	}
	bits = max(bits, 600)
	s.credit -= bits
	s.queued += bits
	s.lastFrame = now
	s.frames++
	return int(bits / 8), key
}

func (s *screenModel) send(size int, key bool) {
	now := time.Now()
	hdr := make([]byte, 5)
	binary.LittleEndian.PutUint32(hdr, 33_333)
	var frame []byte
	if key {
		hdr[4] = 1
		frame = append(frame, 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x34, 0xac, 0xd9, 0x40, 0x78)
		frame = append(frame, 0, 0, 0, 1, 0x68, 0xeb, 0xe3, 0xcb)
		frame = append(frame, 0, 0, 0, 1, 0x65)
	} else {
		frame = append(frame, 0, 0, 0, 1, 0x41)
	}
	frame = append(frame, fmt.Sprintf("pdanet-lab:%016x", now.Sub(s.origin).Nanoseconds())...)
	for len(frame) < size {
		frame = append(frame, byte(0x80|rand.IntN(128)))
	}
	s.host.writeVideo(append(hdr, frame...))
}

// viewerModel is ViewerPipeline in short: a queue with ViewerFrameQueue's limits in front of a
// decoder that takes a few milliseconds a frame. After a gap it waits for a keyframe and asks
// for one, at most every 300 ms, as the app does over the control channel.
type viewerModel struct {
	start     time.Time
	maxFrames int
	maxAge    time.Duration
	askKey    func()

	mu      sync.Mutex
	queue   []viewerFrame
	gap     bool
	wake    chan struct{}
	lastAsk time.Time
	resets  int
	samples []viewerSample
	shownAt []time.Duration // since start
}

type viewerSample struct {
	at, latency time.Duration // display time since start and send-to-display delay
	bytes       int
	timed       bool
}

type viewerFrame struct {
	flags byte
	data  []byte
	at    time.Time
	gap   bool
}

func (v *viewerModel) receive(flags byte, data []byte) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if v.wake == nil {
		v.wake = make(chan struct{}, 1)
	}
	now := time.Now()
	if len(v.queue) >= v.maxFrames || (len(v.queue) > 0 && now.Sub(v.queue[0].at) > v.maxAge) {
		// ViewerFrameQueue.TrimFor, keeping a recent keyframe and what follows it if it fits.
		keep := len(v.queue)
		if flags&frameKey == 0 {
			for i, f := range v.queue {
				if f.flags&frameKey != 0 && now.Sub(f.at) <= v.maxAge {
					keep = i
				}
			}
			if len(v.queue)-keep >= v.maxFrames {
				keep = len(v.queue)
			}
		}
		v.queue = v.queue[keep:]
		v.gap = true
		v.resets++
	}
	v.queue = append(v.queue, viewerFrame{flags: flags, data: data, at: now})
	select {
	case v.wake <- struct{}{}:
	default:
	}
}

func (v *viewerModel) decode(stop chan struct{}) {
	waitKey := true
	for {
		v.mu.Lock()
		if v.wake == nil {
			v.wake = make(chan struct{}, 1)
		}
		wake := v.wake
		var f viewerFrame
		ok := false
		now := time.Now()
		for len(v.queue) > 1 && now.Sub(v.queue[0].at) > v.maxAge {
			v.queue = v.queue[1:]
			v.gap = true
		}
		if len(v.queue) > 0 {
			f, v.queue, ok = v.queue[0], v.queue[1:], true
			f.gap, v.gap = v.gap, false
		}
		v.mu.Unlock()
		if !ok {
			select {
			case <-stop:
				return
			case <-wake:
			case <-time.After(50 * time.Millisecond):
			}
			continue
		}
		key := f.flags&frameKey != 0
		if time.Since(f.at) > v.maxAge || f.flags&frameAfterLoss != 0 || f.gap {
			waitKey = true
		}
		if waitKey && !key {
			v.ask()
			continue
		}
		waitKey = false
		// Decoding: a keyframe of a whole desktop takes longer than a frame of changes.
		if key {
			time.Sleep(12 * time.Millisecond)
		} else {
			time.Sleep(3 * time.Millisecond)
		}
		v.shown(f.data)
	}
}

func (v *viewerModel) ask() {
	v.mu.Lock()
	now := time.Now()
	fire := now.Sub(v.lastAsk) >= 300*time.Millisecond
	if fire {
		v.lastAsk = now
	}
	v.mu.Unlock()
	if fire && v.askKey != nil {
		v.askKey()
	}
}

func (v *viewerModel) shown(data []byte) {
	now := time.Now()
	v.mu.Lock()
	defer v.mu.Unlock()
	since := now.Sub(v.start)
	sample := viewerSample{at: since, bytes: len(data)}
	if i := bytes.Index(data, []byte("pdanet-lab:")); i >= 0 && i+11+16 <= len(data) {
		if sent, err := strconv.ParseInt(string(data[i+11:i+27]), 16, 64); err == nil {
			sample.latency, sample.timed = since-time.Duration(sent), true
		}
	}
	v.shownAt = append(v.shownAt, since)
	v.samples = append(v.samples, sample)
}

func (v *viewerModel) result(target []targetSample, s *screenModel, plis int, warm time.Duration) netLabResult {
	return v.resultAt(target, s, plis, warm, time.Since(v.start))
}

// resultAt uses one exact observation window for target, delivered payload,
// latency, motion FPS and freezes: [warm, end]. Post-stop draining is excluded.
// Keyframe, PLI and queue-reset counters remain whole-trial diagnostic counts.
func (v *viewerModel) resultAt(target []targetSample, s *screenModel, plis int, warm, end time.Duration) netLabResult {
	v.mu.Lock()
	defer v.mu.Unlock()
	var r netLabResult
	// The target over time, after the warmup.
	var sum, span float64
	low := 1e18
	for i, x := range target {
		from, to := x.at.Sub(v.start), end
		if i+1 < len(target) {
			to = target[i+1].at.Sub(v.start)
		}
		to = min(to, end)
		from = max(from, warm)
		if to <= from {
			continue
		}
		d := (to - from).Seconds()
		sum += float64(x.bps) * d
		span += d
		low = min(low, float64(x.bps))
	}
	if span > 0 {
		r.targetKbps, r.minTargetKbps = sum/span/1000, low/1000
	}
	var payload int64
	var lat []time.Duration
	for _, sample := range v.samples {
		if sample.at < warm || sample.at > end {
			continue
		}
		payload += int64(sample.bytes)
		if sample.timed {
			lat = append(lat, sample.latency)
		}
	}
	if end > warm {
		r.deliveredKbps = float64(payload*8) / (end - warm).Seconds() / 1000
	}
	slices.Sort(lat)
	if n := len(lat); n > 0 {
		r.p50, r.p95, r.max = lat[(n-1)/2], lat[(95*n+99)/100-1], lat[n-1]
	}
	// Frames shown during motion, and freezes: gaps while the screen moves.
	s.mu.Lock()
	r.keys = s.keys
	s.mu.Unlock()
	r.plis, r.resets = plis, v.resets
	shownInMotion := 0
	prev := warm
	gap := func(to time.Duration) {
		// An idle desktop intentionally sends no frames. A freeze is at least
		// 500 ms of continuous motion with no new picture, after window clipping.
		forMotionOverlap(prev, to, func(d time.Duration) {
			if d >= 500*time.Millisecond {
				r.frozen += d
				r.longest = max(r.longest, d)
			}
		})
	}
	for _, at := range v.shownAt {
		if at < warm {
			continue
		}
		if at > end {
			break
		}
		if at%(motionFor+restFor) < motionFor {
			shownInMotion++
		}
		gap(at)
		prev = at
	}
	gap(end)
	var motionTime time.Duration
	forMotionOverlap(warm, end, func(d time.Duration) { motionTime += d })
	if motionTime > 0 {
		r.motionFps = float64(shownInMotion) / motionTime.Seconds()
	}
	return r
}

// forMotionOverlap visits the overlap with each separate motion interval in the
// synthetic 3 s motion / 2 s rest workload. Idle portions never join two freezes.
func forMotionOverlap(from, to time.Duration, visit func(time.Duration)) {
	from = max(from, 0)
	if to <= from {
		return
	}
	cycle := motionFor + restFor
	for start := from / cycle * cycle; start < to; start += cycle {
		lo, hi := max(from, start), min(to, start+motionFor)
		if hi > lo {
			visit(hi - lo)
		}
	}
}
