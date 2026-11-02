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
	startBitrate = 2_500_000
	minBitrate   = 200_000
	maxBitrate   = 30_000_000
	gatherWait   = 2500 * time.Millisecond // then send the candidates found so far, the local ones come first
	keyGap       = 300 * time.Millisecond  // at most one keyframe request this often
)

type peerConfig struct {
	host     bool
	link     *ipc
	pnet     *phoneNet         // the phone's network in Phone VPN mode
	keepIP   func(net.IP) bool // the phone's network on this PC
	stun     []string
	phoneIPs []net.IP // addresses of the phone's network on this PC, to name the path
	viaPhone bool
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
	statsOn     bool
	closeOnce   sync.Once
	closed      chan struct{}
}

type estimator struct{ cc.BandwidthEstimator }

func newPeer(cfg peerConfig) (*peer, error) {
	p := &peer{cfg: cfg, link: cfg.link, needRestart: make(chan struct{}, 1), closed: make(chan struct{})}

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
		// PdaNet, which sets the encoder to match. The pacer smooths what the encoder sends.
		ccf, err := cc.NewInterceptor(func() (cc.BandwidthEstimator, error) {
			return gcc.NewSendSideBWE(
				gcc.SendSideBWEInitialBitrate(startBitrate),
				gcc.SendSideBWEMinBitrate(minBitrate),
				gcc.SendSideBWEMaxBitrate(maxBitrate),
				gcc.SendSideBWEPacer(newBurstPacer(startBitrate)),
			)
		})
		if err != nil {
			return nil, err
		}
		ccf.OnNewPeerConnection(func(_ string, e cc.BandwidthEstimator) {
			p.bwe.Store(&estimator{e})
			e.OnTargetBitrateChange(p.onTarget)
		})
		reg.Add(ccf)
		if err := webrtc.ConfigureTWCCHeaderExtensionSender(m, reg); err != nil {
			return nil, err
		}
	}
	// NACK (lost packets are sent again), RTCP reports and TWCC feedback.
	if err := webrtc.RegisterDefaultInterceptors(m, reg); err != nil {
		return nil, err
	}

	var se webrtc.SettingEngine
	se.SetICETimeouts(3*time.Second, 12*time.Second, time.Second)
	se.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
	se.SetSTUNGatherTimeout(3 * time.Second)
	types := []webrtc.NetworkType{webrtc.NetworkTypeUDP4, webrtc.NetworkTypeUDP6}
	if cfg.pnet != nil {
		se.SetNet(cfg.pnet)
		types = types[:1]
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
	pc, err := api.NewPeerConnection(webrtc.Configuration{ICEServers: servers})
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
	p.video = make(chan videoOut, 6)
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
	return p.local(a)
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
	}
	sdp := p.pc.LocalDescription().SDP
	p.noteCandidates(true, sdp)
	return sdp, nil
}

// candidateKinds lists the kinds of address an SDP offers, such as "host srflx", or "none".
// A side without "srflx" heard nothing back from the STUN servers: its network keeps it off
// the internet over UDP, and with that from a direct link.
func candidateKinds(sdp string) string {
	var kinds []string
	for _, line := range strings.Split(sdp, "\n") {
		if !strings.HasPrefix(line, "a=candidate:") {
			continue
		}
		f := strings.Fields(line)
		for i := 0; i+1 < len(f); i++ {
			if f[i] == "typ" && !slices.Contains(kinds, f[i+1]) {
				kinds = append(kinds, f[i+1])
			}
		}
	}
	if len(kinds) == 0 {
		return "none"
	}
	slices.Sort(kinds)
	return strings.Join(kinds, " ")
}

// noteCandidates keeps the kinds of address one side offered. Once both sides are known it
// tells PdaNet and the log, so a session that finds no path can say whose network blocks it.
func (p *peer) noteCandidates(local bool, sdp string) {
	kinds := candidateKinds(sdp)
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
// keyframe.
func (p *peer) readRTCP(sender *webrtc.RTPSender) {
	for {
		pkts, _, err := sender.ReadRTCP()
		if err != nil {
			return
		}
		for _, pk := range pkts {
			switch pk.(type) {
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

func (p *peer) onTarget(bps int) {
	p.mu.Lock()
	last, at := p.lastTarget, p.lastTargetT
	change := bps - last
	if change < 0 {
		change = -change
	}
	send := last == 0 || change*100 >= last*8 || (change > 0 && time.Since(at) > 2*time.Second)
	if send {
		p.lastTarget, p.lastTargetT = bps, time.Now()
	}
	p.mu.Unlock()
	if send {
		p.link.event(event{Ev: "bitrate", Bps: bps})
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

	// Frames wait for late or resent packets up to 400 ms, then the frame is given up and the
	// next one asks for a keyframe.
	sb := samplebuilder.New(2048, &codecs.H264Packet{}, 90000, samplebuilder.WithMaxTimeDelay(400*time.Millisecond))
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

// queueVideo hands a frame to the writer. When PdaNet falls behind, the frame is dropped
// instead of piling up delay, and the next frame says so.
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
		path, local, remote := p.describePath()
		p.link.event(event{Ev: "connected", Path: path, Local: local, Remote: remote})
		if startStats {
			go p.statsLoop()
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
// the phone, "internet" otherwise. local and remote are the ICE candidate types.
func (p *peer) describePath() (path, local, remote string) {
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
	if p.cfg.viaPhone {
		return
	}
	lip, rip := net.ParseIP(pair.Local.Address), net.ParseIP(pair.Remote.Address)
	for _, ip := range p.cfg.phoneIPs {
		if ip.Equal(lip) {
			return "phone", local, remote
		}
	}
	direct := pair.Local.Typ == webrtc.ICECandidateTypeHost &&
		(pair.Remote.Typ == webrtc.ICECandidateTypeHost || pair.Remote.Typ == webrtc.ICECandidateTypePrflx)
	if direct && sameNetwork(lip, rip) {
		path = "lan"
	}
	return
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

// statsLoop tells PdaNet about once a second how the link does.
func (p *peer) statsLoop() {
	t := time.NewTicker(time.Second)
	defer t.Stop()
	var lastSent, lastRecv uint64
	var lastLost int32
	var lastPkts uint32
	var lastAt time.Time
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
			e.Target = b.GetTargetBitrate() / 1000
			if st := b.GetStats(); st != nil {
				if l, ok := st["averageLoss"].(float64); ok {
					e.Loss = float64(int(l*1000)) / 10
				}
			}
		}
		p.link.event(e)
	}
}

func (p *peer) close() {
	p.closeOnce.Do(func() {
		close(p.closed)
		_ = p.pc.Close()
	})
}
