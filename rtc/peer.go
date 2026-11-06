package main

import (
	"encoding/binary"
	"log"
	"net"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/interceptor"
	"github.com/pion/interceptor/pkg/cc"
	"github.com/pion/interceptor/pkg/gcc"
	"github.com/pion/interceptor/pkg/nack"
	"github.com/pion/rtcp"
	"github.com/pion/rtp/codecs"
	"github.com/pion/webrtc/v4"
	"github.com/pion/webrtc/v4/pkg/media"
	"github.com/pion/webrtc/v4/pkg/media/samplebuilder"
)

// The screen goes as H.264 on one video track, the remote control as data channels: "ctl"
// is reliable and ordered (keys, clicks, clipboard), "live" drops late messages (mouse moves,
// the remote pointer), where only the newest one matters.

// Both ends are PdaNet, so one H.264 entry is enough. The profile in the SDP is only matched,
// the decoder takes whatever profile the encoder picked.
const h264Fmtp = "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640034"

const (
	// Low enough that the first keyframe gets through a slow or capped relay (the Ludo relay held
	// each session to 512 kbit/s and dropped the rest, 2026-11-06); bursts the pacer spreads out
	// raise it within a few frames where the link carries more, see linkMeter.
	startBitrate = 1_000_000
	minBitrate   = 200_000
	maxBitrate   = 30_000_000
	gatherWait   = 2500 * time.Millisecond // then send the candidates found so far, the local ones come first
	relayWait    = 6 * time.Second         // with TURN servers, the most to wait for a relay address
	keyGap       = 300 * time.Millisecond  // at most one keyframe request this often

	// How long the PC that views holds the frames after a lost packet for the packet to come again,
	// before it gives the frame up and asks for a keyframe. Through the phone and a relay a resend
	// takes 300 to 900 ms, and the phone's link stalls for a second at times (2026-11-06); after
	// 400 ms, as before, nearly every lost packet cost a keyframe, and keyframes, the largest
	// frames, lost packets the most.
	frameWait = 2 * time.Second
	// Packets the PC that shares keeps to send again: frameWait at 16 Mbit/s.
	resendKeep = 4096
	// SRTP drops a packet that comes more than this many packets late as a replay. Pion's 64
	// dropped most resends at a megabit and more, see nackGen.
	srtpWindow = 4096
	// Frames handed to PdaNet wait here: after a resend the frames held behind it come all at
	// once, and each one is needed to decode the next.
	videoQueue = 256

	// The floor the link meter puts under GCC's estimate, see target.
	floorShare = 0.8                   // of the best burst of the last seconds
	queueOn    = 25 * time.Millisecond // the floor holds while the standing queue stays below this
	queueOff   = 60 * time.Millisecond // and goes away above this, or with more loss than lossOff
	lossOn     = 0.04
	lossOff    = 0.10

	transportCC = "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01"
)

type peerConfig struct {
	host      bool
	link      *ipc
	pnet      *phoneNet         // the phone's network in Phone VPN mode
	keepIP    func(net.IP) bool // the phone's network on this PC
	stun      []string
	turn      []iceServer
	relayOnly bool
	phoneIPs  []net.IP // addresses of the phone's network on this PC, to name the path
	viaPhone  bool
}

type videoOut struct {
	flags byte
	ts    uint32
	data  []byte
}

type peer struct {
	cfg  peerConfig
	pc   *webrtc.PeerConnection
	link *ipc

	ctl, live atomic.Pointer[webrtc.DataChannel]
	ctlMu     sync.Mutex
	ctlQueue  [][]byte // control messages sent before the channel opened

	track   *webrtc.TrackLocalStaticSample // the screen, on the PC that shares
	bwe     atomic.Pointer[estimator]
	meter   *linkMeter // what the link carries, on the PC that shares
	pacer   atomic.Pointer[burstPacer]
	gccBps  atomic.Int64
	rtt     atomic.Int64  // the round trip in nanoseconds, from the stats, for nackGen
	ssrc    atomic.Uint32 // the screen's SSRC, on the PC that views
	video   chan videoOut
	videoMu sync.Mutex
	dropped bool // a frame was dropped on the way to PdaNet, the next one says so

	needRestart chan struct{}

	mu          sync.Mutex
	localKinds  string // the kinds of address each side offered, see noteCandidates
	remoteKinds string
	connected   bool
	everUp      bool
	lostAt      time.Time
	lastKeyReq  time.Time
	lastKeyEv   time.Time
	lastTarget  int
	lastTargetT time.Time
	floorOn     bool
	statsOn     bool
	closeOnce   sync.Once
	closed      chan struct{}
}

type estimator struct{ cc.BandwidthEstimator }

func newPeer(cfg peerConfig) (*peer, error) {
	p := &peer{cfg: cfg, link: cfg.link, needRestart: make(chan struct{}, 1), closed: make(chan struct{}), floorOn: true}

	m := &webrtc.MediaEngine{}
	if err := m.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType:    webrtc.MimeTypeH264,
			ClockRate:   90000,
			SDPFmtpLine: h264Fmtp,
			RTCPFeedback: []webrtc.RTCPFeedback{
				{Type: "nack"}, {Type: "nack", Parameter: "pli"}, {Type: "ccm", Parameter: "fir"},
			},
		},
		PayloadType: 102,
	}, webrtc.RTPCodecTypeVideo); err != nil {
		return nil, err
	}

	reg := &interceptor.Registry{}
	if cfg.host {
		// Google Congestion Control: the PC that shares learns how much the link takes and tells
		// PdaNet, which sets the encoder to match. The pacer smooths what the encoder sends and
		// notes each packet for the link meter, which keeps GCC from sinking below what the
		// link shows it carries, see target.
		p.meter = newLinkMeter()
		ccf, err := cc.NewInterceptor(func() (cc.BandwidthEstimator, error) {
			pacer := newBurstPacer(startBitrate, p.meter)
			p.pacer.Store(pacer)
			return gcc.NewSendSideBWE(
				gcc.SendSideBWEInitialBitrate(startBitrate),
				gcc.SendSideBWEMinBitrate(minBitrate),
				gcc.SendSideBWEMaxBitrate(maxBitrate),
				gcc.SendSideBWEPacer(pacer),
			)
		})
		if err != nil {
			return nil, err
		}
		ccf.OnNewPeerConnection(func(_ string, e cc.BandwidthEstimator) {
			p.bwe.Store(&estimator{e})
			p.gccBps.Store(int64(e.GetTargetBitrate()))
			e.OnTargetBitrateChange(p.onGCC)
		})
		reg.Add(ccf)
		if err := webrtc.ConfigureTWCCHeaderExtensionSender(m, reg); err != nil {
			return nil, err
		}
	}
	// Pion's default interceptors, with nackGen in place of Pion's NACK generator: lost packets
	// are asked for and sent again, RTCP reports, stats and TWCC feedback.
	responder, err := nack.NewResponderInterceptor(nack.ResponderSize(resendKeep))
	if err != nil {
		return nil, err
	}
	m.RegisterFeedback(webrtc.RTCPFeedback{Type: "nack"}, webrtc.RTPCodecTypeVideo)
	m.RegisterFeedback(webrtc.RTCPFeedback{Type: "nack", Parameter: "pli"}, webrtc.RTPCodecTypeVideo)
	reg.Add(responder)
	reg.Add(&nackGenFactory{rtt: func() time.Duration { return time.Duration(p.rtt.Load()) }})
	if err := webrtc.ConfigureRTCPReports(reg); err != nil {
		return nil, err
	}
	if err := webrtc.ConfigureSimulcastExtensionHeaders(m); err != nil {
		return nil, err
	}
	if err := webrtc.ConfigureStatsInterceptor(reg); err != nil {
		return nil, err
	}
	if err := webrtc.ConfigureTWCCSender(m, reg); err != nil {
		return nil, err
	}

	var se webrtc.SettingEngine
	se.SetSRTPReplayProtectionWindow(srtpWindow)
	se.SetICETimeouts(3*time.Second, 12*time.Second, time.Second)
	se.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
	se.SetSTUNGatherTimeout(3 * time.Second)
	types := []webrtc.NetworkType{webrtc.NetworkTypeUDP4, webrtc.NetworkTypeUDP6}
	if cfg.pnet != nil {
		se.SetNet(cfg.pnet)
		// IPv6 only where the phone's mobile network gave the phone an address of its own there.
		if !cfg.pnet.has6 {
			types = types[:1]
		}
	}
	se.SetNetworkTypes(types)
	if cfg.keepIP != nil {
		se.SetIPFilter(cfg.keepIP)
	}

	api := webrtc.NewAPI(webrtc.WithMediaEngine(m), webrtc.WithInterceptorRegistry(reg), webrtc.WithSettingEngine(se))
	var servers []webrtc.ICEServer
	if len(cfg.stun) > 0 {
		servers = []webrtc.ICEServer{{URLs: cfg.stun}}
	}
	for _, t := range cfg.turn {
		servers = append(servers, webrtc.ICEServer{URLs: t.URLs, Username: t.Username, Credential: t.Credential})
	}
	conf := webrtc.Configuration{ICEServers: servers}
	if cfg.relayOnly {
		conf.ICETransportPolicy = webrtc.ICETransportPolicyRelay
	}
	pc, err := api.NewPeerConnection(conf)
	if err != nil {
		return nil, err
	}
	p.pc = pc
	pc.OnICEConnectionStateChange(p.onICEState)

	if cfg.host {
		p.track, err = webrtc.NewTrackLocalStaticSample(
			webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeH264, ClockRate: 90000, SDPFmtpLine: h264Fmtp},
			"screen", "pdanet")
		if err != nil {
			_ = pc.Close()
			return nil, err
		}
		pc.OnDataChannel(p.adopt)
		return p, nil
	}

	if _, err := pc.AddTransceiverFromKind(webrtc.RTPCodecTypeVideo,
		webrtc.RTPTransceiverInit{Direction: webrtc.RTPTransceiverDirectionRecvonly}); err != nil {
		_ = pc.Close()
		return nil, err
	}
	ordered, unordered, none := true, false, uint16(0)
	ctl, err := pc.CreateDataChannel("ctl", &webrtc.DataChannelInit{Ordered: &ordered})
	if err != nil {
		_ = pc.Close()
		return nil, err
	}
	live, err := pc.CreateDataChannel("live", &webrtc.DataChannelInit{Ordered: &unordered, MaxRetransmits: &none})
	if err != nil {
		_ = pc.Close()
		return nil, err
	}
	p.adopt(ctl)
	p.adopt(live)
	p.video = make(chan videoOut, videoQueue)
	go p.videoWriter()
	pc.OnTrack(p.receive)
	return p, nil
}

// ============================ Offer and answer ============================

// offer makes the offer of the PC that views.
func (p *peer) offer() (string, error) {
	o, err := p.pc.CreateOffer(nil)
	if err != nil {
		return "", err
	}
	return p.local(o)
}

// answer takes the offer on the PC that shares and makes the answer, with the screen track.
func (p *peer) answer(offer string) (string, error) {
	if err := p.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: offer}); err != nil {
		return "", err
	}
	p.noteCandidates(false, offer)
	sender, err := p.pc.AddTrack(p.track)
	if err != nil {
		return "", err
	}
	go p.readRTCP(sender)
	a, err := p.pc.CreateAnswer(nil)
	if err != nil {
		return "", err
	}
	sdp, err := p.local(a)
	if err != nil {
		return "", err
	}
	// The link meter reads the transport wide sequence number of each packet it times.
	for _, e := range sender.GetParameters().HeaderExtensions {
		if e.URI == transportCC && e.ID > 0 && e.ID < 256 {
			p.meter.setExt(uint8(e.ID))
			if pc := p.pacer.Load(); pc != nil {
				pc.setExt(uint8(e.ID))
			}
		}
	}
	return sdp, nil
}

// accept takes the answer on the PC that views.
func (p *peer) accept(answer string) error {
	if err := p.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: answer}); err != nil {
		return err
	}
	p.noteCandidates(false, answer)
	return nil
}

// restartOffer starts over the search for a path, after the network changed.
func (p *peer) restartOffer() (string, error) {
	o, err := p.pc.CreateOffer(&webrtc.OfferOptions{ICERestart: true})
	if err != nil {
		return "", err
	}
	return p.local(o)
}

func (p *peer) restartAnswer(offer string) (string, error) {
	if err := p.pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: offer}); err != nil {
		return "", err
	}
	p.noteCandidates(false, offer)
	a, err := p.pc.CreateAnswer(nil)
	if err != nil {
		return "", err
	}
	return p.local(a)
}

// local sets the description and waits for the candidates, without trickle ICE: the relays
// then carry one message each way.
func (p *peer) local(d webrtc.SessionDescription) (string, error) {
	gathered := webrtc.GatheringCompletePromise(p.pc)
	if err := p.pc.SetLocalDescription(d); err != nil {
		return "", err
	}
	select {
	case <-gathered:
	case <-time.After(gatherWait):
		p.waitRelay(gathered)
	}
	sdp := p.pc.LocalDescription().SDP
	p.noteCandidates(true, sdp)
	return sdp, nil
}

// waitRelay gives a relay address a few more seconds: it takes more round trips than the other
// kinds, over TLS a few more still, and without trickle ICE what comes later is left out.
func (p *peer) waitRelay(gathered <-chan struct{}) {
	if len(p.cfg.turn) == 0 {
		return
	}
	deadline := time.After(relayWait - gatherWait)
	for !strings.Contains(p.pc.LocalDescription().SDP, " typ relay") {
		select {
		case <-gathered:
			return
		case <-deadline:
			log.Printf("ice: no relay address yet, the offer goes without one")
			return
		case <-time.After(100 * time.Millisecond):
		}
	}
}

// candidateKinds lists the kinds of address an SDP offers, such as "host srflx", or "none":
// "host" is an address on the PC's own network, "public" one of the PC's own that is a public
// address, which a firewall may still keep the internet from, "srflx" the address a STUN server
// saw, "relay" one on a TURN server. "partial"
// follows when the side sent its addresses before it was done looking, so a kind it lacks may
// only have been late. A side done looking with none of public, srflx or relay heard nothing
// back from the STUN servers, likely because its network keeps it off the internet over UDP.
// That stays a guess: ICE says what it found, not why. It also leaves out a srflx that would
// only repeat a public address.
func candidateKinds(sdp string) string {
	var kinds []string
	done := false
	for _, line := range strings.Split(sdp, "\n") {
		line = strings.TrimSpace(line)
		if line == "a=end-of-candidates" {
			done = true
			continue
		}
		// a=candidate:<foundation> <component> <transport> <priority> <address> <port> typ <kind> ...
		f := strings.Fields(line)
		if !strings.HasPrefix(line, "a=candidate:") || len(f) < 8 || f[6] != "typ" {
			continue
		}
		kind := f[7]
		if ip := net.ParseIP(f[4]); kind == "host" && ip != nil && publicIP(ip) {
			kind = "public"
		}
		if !slices.Contains(kinds, kind) {
			kinds = append(kinds, kind)
		}
	}
	slices.Sort(kinds)
	if len(kinds) == 0 {
		kinds = []string{"none"}
	}
	if !done {
		kinds = append(kinds, "partial")
	}
	return strings.Join(kinds, " ")
}

// candidateFamilies tells which address families an SDP offers candidates in: "IPv4", "IPv6"
// or both, for the log, so a session that ends on a relay shows whether IPv6 could have linked
// the two PCs straight.
func candidateFamilies(sdp string) string {
	var v4, v6 bool
	for _, line := range strings.Split(sdp, "\n") {
		f := strings.Fields(strings.TrimSpace(line))
		if len(f) < 8 || !strings.HasPrefix(f[0], "a=candidate:") {
			continue
		}
		if ip := net.ParseIP(f[4]); ip != nil {
			if ip.To4() != nil {
				v4 = true
			} else {
				v6 = true
			}
		}
	}
	switch {
	case v4 && v6:
		return "IPv4 and IPv6"
	case v6:
		return "IPv6"
	case v4:
		return "IPv4"
	}
	return "no address"
}

// sharedSpace is 100.64.0.0/10, which carriers use behind their NAT: the internet does not route it.
var sharedSpace = net.IPNet{IP: net.IPv4(100, 64, 0, 0), Mask: net.CIDRMask(10, 32)}

// publicIP tells a public address, one the internet routes to with no NAT in between. It says
// nothing of firewalls, so it does not tell that the internet gets through to it.
func publicIP(ip net.IP) bool {
	return ip.IsGlobalUnicast() && !ip.IsPrivate() && !sharedSpace.Contains(ip)
}

// noteCandidates keeps the kinds of address one side offered. Once both sides are known it
// tells PdaNet and the log, so a session that finds no path can say what each PC found.
func (p *peer) noteCandidates(local bool, sdp string) {
	kinds := candidateKinds(sdp)
	if fam := candidateFamilies(sdp); local {
		log.Printf("ice: this PC offers addresses in %s", fam)
	} else {
		log.Printf("ice: the other PC offers addresses in %s", fam)
	}
	p.mu.Lock()
	if local {
		p.localKinds = kinds
	} else {
		p.remoteKinds = kinds
	}
	l, r := p.localKinds, p.remoteKinds
	p.mu.Unlock()
	if l == "" || r == "" {
		return
	}
	log.Printf("ice: this PC offers %s, the other PC %s", l, r)
	p.link.event(event{Ev: "candidates", Local: l, Remote: r})
}

// ============================ Data channels ============================

func (p *peer) adopt(dc *webrtc.DataChannel) {
	kind := kindCtl
	if dc.Label() == "live" {
		kind = kindLive
	}
	dc.OnOpen(func() {
		if kind == kindLive {
			p.live.Store(dc)
			return
		}
		p.ctlMu.Lock()
		p.ctl.Store(dc)
		queued := p.ctlQueue
		p.ctlQueue = nil
		p.ctlMu.Unlock()
		for _, b := range queued {
			_ = dc.Send(b)
		}
	})
	dc.OnMessage(func(msg webrtc.DataChannelMessage) { _ = p.link.write(kind, msg.Data) })
}

// sendCtl passes a control message on. The data channel copies it.
func (p *peer) sendCtl(b []byte) {
	if dc := p.ctl.Load(); dc != nil {
		_ = dc.Send(b)
		return
	}
	p.ctlMu.Lock()
	defer p.ctlMu.Unlock()
	if dc := p.ctl.Load(); dc != nil {
		_ = dc.Send(b)
	} else if len(p.ctlQueue) < 64 {
		p.ctlQueue = append(p.ctlQueue, append([]byte(nil), b...))
	}
}

// sendLive passes a live message on, unless the link is backed up: then a newer one follows soon.
func (p *peer) sendLive(b []byte) {
	if dc := p.live.Load(); dc != nil && dc.BufferedAmount() < 64<<10 {
		_ = dc.Send(b)
	}
}

// ============================ The screen, sending ============================

// writeVideo sends one frame from PdaNet: u32 duration in microseconds, u8 flags, H.264.
func (p *peer) writeVideo(b []byte) {
	if p.track == nil || len(b) < 5 {
		return
	}
	dur := time.Duration(binary.LittleEndian.Uint32(b[:4])) * time.Microsecond
	if dur <= 0 {
		dur = 33 * time.Millisecond
	}
	if err := p.track.WriteSample(media.Sample{Data: b[5:], Duration: dur}); err != nil {
		log.Printf("video: %v", err)
	}
}

// readRTCP watches what the PC that views says about the screen: a lost picture asks for a
// keyframe, and the arrival times go to the link meter.
func (p *peer) readRTCP(sender *webrtc.RTPSender) {
	for {
		pkts, _, err := sender.ReadRTCP()
		if err != nil {
			return
		}
		for _, pk := range pkts {
			switch v := pk.(type) {
			case *rtcp.TransportLayerCC:
				p.meter.onFeedback(v)
			case *rtcp.PictureLossIndication, *rtcp.FullIntraRequest:
				p.mu.Lock()
				fire := time.Since(p.lastKeyEv) >= keyGap
				if fire {
					p.lastKeyEv = time.Now()
				}
				p.mu.Unlock()
				if fire {
					p.link.event(event{Ev: "keyframe"})
				}
			}
		}
	}
}

// onGCC takes Google Congestion Control's new estimate.
func (p *peer) onGCC(bps int) {
	p.gccBps.Store(int64(bps))
	p.updateTarget()
}

// target is the bitrate for the encoder: GCC's estimate, raised to most of what the link
// carried in its best recent burst while the link keeps no standing queue and loses little.
// Past queueOff or lossOff the floor goes and the bursts measured so far are forgotten, so GCC
// alone brings the bitrate down; the floor only comes back below queueOn and lossOn, with
// bursts measured after that.
func (p *peer) target() (final, floor int) {
	gccBps := int(p.gccBps.Load())
	capacity, standing, loss := p.meter.snapshot()
	p.mu.Lock()
	was := p.floorOn
	switch {
	case standing > queueOff || loss > lossOff:
		p.floorOn = false
	case standing < queueOn && loss < lossOn:
		p.floorOn = true
	}
	on := p.floorOn
	p.mu.Unlock()
	if was && !on {
		p.meter.congested()
		capacity = 0
		log.Printf("link: a queue builds up (%d ms) or packets get lost (%.1f %%), the bitrate follows GCC alone",
			standing.Milliseconds(), loss*100)
	}
	if on && capacity > 0 {
		floor = min(int(floorShare*capacity), maxBitrate)
	}
	final = min(max(gccBps, floor, minBitrate), maxBitrate)
	return final, floor
}

// updateTarget sets the pacer and tells PdaNet the bitrate for the encoder when it changed
// enough: 8 % at once, any change after 2 seconds.
func (p *peer) updateTarget() {
	final, floor := p.target()
	if pc := p.pacer.Load(); pc != nil {
		pc.SetFloor(floor)
	}
	p.meter.setTarget(final)
	p.mu.Lock()
	last, at := p.lastTarget, p.lastTargetT
	change := final - last
	if change < 0 {
		change = -change
	}
	send := last == 0 || change*100 >= last*8 || (change > 0 && time.Since(at) > 2*time.Second)
	if send {
		p.lastTarget, p.lastTargetT = final, time.Now()
	}
	p.mu.Unlock()
	if send {
		p.link.event(event{Ev: "bitrate", Bps: final})
	}
}

// targetLoop follows the link meter between GCC's own updates, on the PC that shares.
func (p *peer) targetLoop() {
	t := time.NewTicker(200 * time.Millisecond)
	defer t.Stop()
	for {
		select {
		case <-p.closed:
			return
		case <-t.C:
			if p.isConnected() {
				p.updateTarget()
			}
		}
	}
}

// ============================ The screen, receiving ============================

func (p *peer) receive(tr *webrtc.TrackRemote, rx *webrtc.RTPReceiver) {
	if tr.Kind() != webrtc.RTPCodecTypeVideo {
		return
	}
	p.ssrc.Store(uint32(tr.SSRC()))
	go func() {
		buf := make([]byte, 1500)
		for {
			if _, _, err := rx.Read(buf); err != nil {
				return
			}
		}
	}()

	// Frames wait for late or resent packets up to frameWait, then the frame is given up and the
	// next one asks for a keyframe. The builder hands a frame out once the first packet of the
	// next one is in, so the PC that shares follows the last frame before a rest with a seal
	// frame, see FramePlanner and TestBuilderHoldsTheLastFrame.
	sb := samplebuilder.New(4096, &codecs.H264Packet{}, 90000, samplebuilder.WithMaxTimeDelay(frameWait))
	p.requestKeyframe()
	for {
		pkt, _, err := tr.ReadRTP()
		if err != nil {
			return
		}
		sb.Push(pkt)
		for s := sb.Pop(); s != nil; s = sb.Pop() {
			var flags byte
			if isKeyframe(s.Data) {
				flags |= frameKey
			}
			if s.PrevDroppedPackets > 0 {
				flags |= frameAfterLoss
				p.requestKeyframe()
			}
			p.queueVideo(videoOut{flags: flags, ts: s.PacketTimestamp, data: s.Data})
		}
	}
}

// queueVideo hands a frame to the writer. When PdaNet falls far behind, the frame is dropped
// instead of piling up delay, and the next frame says so. A run of frames that a resend let go
// at once fits in the queue.
func (p *peer) queueVideo(v videoOut) {
	p.videoMu.Lock()
	defer p.videoMu.Unlock()
	if p.dropped {
		v.flags |= frameAfterLoss
	}
	select {
	case p.video <- v:
		p.dropped = false
	default:
		if !p.dropped {
			p.dropped = true
			p.requestKeyframe()
		}
	}
}

func (p *peer) videoWriter() {
	var hdr [5]byte
	for {
		select {
		case v := <-p.video:
			hdr[0] = v.flags
			binary.LittleEndian.PutUint32(hdr[1:], v.ts)
			if err := p.link.write(kindVideo, hdr[:], v.data); err != nil {
				return
			}
		case <-p.closed:
			return
		}
	}
}

// requestKeyframe asks the PC that shares for a full picture.
func (p *peer) requestKeyframe() {
	ssrc := p.ssrc.Load()
	if ssrc == 0 || p.cfg.host {
		return
	}
	p.mu.Lock()
	if time.Since(p.lastKeyReq) < keyGap {
		p.mu.Unlock()
		return
	}
	p.lastKeyReq = time.Now()
	p.mu.Unlock()
	_ = p.pc.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: ssrc}})
}

// isKeyframe looks for an IDR slice among the NAL units of an Annex B frame.
func isKeyframe(b []byte) bool {
	for i := 0; i+3 < len(b); i++ {
		if b[i] == 0 && b[i+1] == 0 && b[i+2] == 1 {
			if b[i+3]&0x1f == 5 {
				return true
			}
			i += 2
		}
	}
	return false
}

// ============================ Connection state ============================

func (p *peer) onICEState(st webrtc.ICEConnectionState) {
	p.mu.Lock()
	switch st {
	case webrtc.ICEConnectionStateConnected, webrtc.ICEConnectionStateCompleted:
		p.connected, p.everUp, p.lostAt = true, true, time.Time{}
		startStats := !p.statsOn
		p.statsOn = true
		p.mu.Unlock()
		path, local, remote, detail := p.describePath()
		log.Printf("ice: connected %s, %s to %s, %s", path, local, remote, detail)
		p.link.event(event{Ev: "connected", Path: path, Local: local, Remote: remote, Detail: detail})
		if startStats {
			go p.statsLoop()
			if p.cfg.host {
				go p.targetLoop()
			}
		}
		return
	case webrtc.ICEConnectionStateDisconnected:
		if p.connected || p.lostAt.IsZero() {
			p.lostAt = time.Now()
		}
		p.connected = false
		// Short drops heal by themselves, a longer one needs a new path.
		go func() {
			select {
			case <-time.After(3 * time.Second):
				p.mu.Lock()
				still := !p.connected
				p.mu.Unlock()
				if still {
					p.signalRestart()
				}
			case <-p.closed:
			}
		}()
	case webrtc.ICEConnectionStateFailed:
		if p.lostAt.IsZero() {
			p.lostAt = time.Now()
		}
		p.connected = false
		p.mu.Unlock()
		p.signalRestart()
		p.link.event(event{Ev: "ice", Reason: st.String()})
		return
	}
	p.mu.Unlock()
	p.link.event(event{Ev: "ice", Reason: st.String()})
}

func (p *peer) signalRestart() {
	select {
	case p.needRestart <- struct{}{}:
	default:
	}
}

// lostFor is how long the link has been down after it was up once.
func (p *peer) lostFor() time.Duration {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.connected || !p.everUp || p.lostAt.IsZero() {
		return 0
	}
	return time.Since(p.lostAt)
}

func (p *peer) isConnected() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.connected
}

// describePath names the way the two PCs found: "lan" on the same network, "phone" through
// the phone, "relay" through a TURN server, "internet" otherwise. local and remote are the ICE
// candidate types. detail says the address family, "ipv4" or "ipv6", and for a relay the way
// this PC reaches it, such as "ipv4 relay udp".
func (p *peer) describePath() (path, local, remote, detail string) {
	path = "internet"
	if p.cfg.viaPhone {
		path = "phone"
	}
	sctp := p.pc.SCTP()
	if sctp == nil {
		return
	}
	pair, err := sctp.Transport().ICETransport().GetSelectedCandidatePair()
	if err != nil || pair == nil {
		return
	}
	local, remote = pair.Local.Typ.String(), pair.Remote.Typ.String()
	lip, rip := net.ParseIP(pair.Local.Address), net.ParseIP(pair.Remote.Address)
	detail = "ipv4"
	if lip != nil && lip.To4() == nil {
		detail = "ipv6"
	}
	if pair.Local.Typ == webrtc.ICECandidateTypeRelay || pair.Remote.Typ == webrtc.ICECandidateTypeRelay {
		if proto := p.relayProtocol(); proto != "" {
			detail += " relay " + proto
		} else {
			detail += " relay"
		}
		return "relay", local, remote, detail
	}
	if p.cfg.viaPhone {
		return
	}
	for _, ip := range p.cfg.phoneIPs {
		if ip.Equal(lip) {
			return "phone", local, remote, detail
		}
	}
	direct := pair.Local.Typ == webrtc.ICECandidateTypeHost &&
		(pair.Remote.Typ == webrtc.ICECandidateTypeHost || pair.Remote.Typ == webrtc.ICECandidateTypePrflx)
	if direct && sameNetwork(lip, rip) {
		path = "lan"
	}
	return
}

// relayProtocol is how this PC reaches the TURN server of the chosen pair: "udp", "tcp" or "tls".
func (p *peer) relayProtocol() string {
	stats := p.pc.GetStats()
	for _, s := range stats {
		pair, ok := s.(webrtc.ICECandidatePairStats)
		if !ok || !pair.Nominated || pair.State != webrtc.StatsICECandidatePairStateSucceeded {
			continue
		}
		if c, ok := stats[pair.LocalCandidateID].(webrtc.ICECandidateStats); ok {
			return c.RelayProtocol
		}
	}
	return ""
}

// sameNetwork tells two addresses of one local network: private IPv4 on both ends, or IPv6 in
// the same /64, which is how home and office networks hand out IPv6. An IPv6 pair with a unique
// local or link local end is local too: those never travel the internet, and IPv6 has no NAT to
// bridge them, while a private IPv4 end may sit behind one.
func sameNetwork(a, b net.IP) bool {
	if a == nil || b == nil {
		return false
	}
	local := func(ip net.IP) bool { return ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLoopback() }
	if a.To4() != nil && b.To4() != nil {
		return local(a) && local(b)
	}
	if a.To4() == nil && b.To4() == nil {
		return local(a) || local(b) || a.Mask(net.CIDRMask(64, 128)).Equal(b.Mask(net.CIDRMask(64, 128)))
	}
	return false
}

// statsLoop tells PdaNet about once a second how the link does, and the log every ten seconds.
func (p *peer) statsLoop() {
	t := time.NewTicker(time.Second)
	defer t.Stop()
	var lastSent, lastRecv uint64
	var lastLost int32
	var lastPkts uint32
	var lastAt time.Time
	ticks := 0
	for {
		select {
		case <-p.closed:
			return
		case <-t.C:
		}
		if !p.isConnected() {
			continue
		}
		var e event
		e.Ev = "stats"
		var sent, recv uint64
		var lost int32
		var pkts uint32
		for _, s := range p.pc.GetStats() {
			switch v := s.(type) {
			case webrtc.ICECandidatePairStats:
				if v.Nominated && v.State == webrtc.StatsICECandidatePairStateSucceeded {
					e.RTT = float64(int(v.CurrentRoundTripTime*10000)) / 10
					sent, recv = v.BytesSent, v.BytesReceived
					if v.CurrentRoundTripTime > 0 {
						p.rtt.Store(int64(v.CurrentRoundTripTime * float64(time.Second)))
					}
				}
			case webrtc.InboundRTPStreamStats:
				lost, pkts = v.PacketsLost, v.PacketsReceived
			}
		}
		now := time.Now()
		if !lastAt.IsZero() {
			secs := now.Sub(lastAt).Seconds()
			if sent >= lastSent {
				e.Send = int(float64(sent-lastSent) * 8 / 1000 / secs)
			}
			if recv >= lastRecv {
				e.Recv = int(float64(recv-lastRecv) * 8 / 1000 / secs)
			}
			if d := float64(pkts-lastPkts) + float64(lost-lastLost); pkts >= lastPkts && d > 0 && lost >= lastLost {
				e.Loss = float64(int(float64(lost-lastLost)/d*1000)) / 10
			}
		}
		lastSent, lastRecv, lastLost, lastPkts, lastAt = sent, recv, lost, pkts, now
		if b := p.bwe.Load(); b != nil {
			p.mu.Lock()
			e.Target = p.lastTarget / 1000
			p.mu.Unlock()
			e.Gcc = b.GetTargetBitrate() / 1000
			capacity, standing, loss := p.meter.snapshot()
			e.Capacity = int(capacity / 1000)
			e.Queue = int(standing.Milliseconds())
			e.Loss = float64(int(loss*1000)) / 10
		}
		p.link.event(e)
		if ticks++; ticks%10 == 1 {
			if p.cfg.host {
				log.Printf("stats: rtt %.0f ms, sending %d kbit/s, target %d kbit/s (gcc %d, best burst %d), standing queue %d ms, loss %.1f %%",
					e.RTT, e.Send, e.Target, e.Gcc, e.Capacity, e.Queue, e.Loss)
			} else {
				log.Printf("stats: rtt %.0f ms, receiving %d kbit/s, loss %.1f %%", e.RTT, e.Recv, e.Loss)
			}
		}
	}
}

func (p *peer) close() {
	p.closeOnce.Do(func() {
		close(p.closed)
		_ = p.pc.Close()
	})
}
