package main

import (
	"context"
	"crypto/hmac"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/url"
	"strings"
	"sync"
	"time"
	"unicode"
)

// How a session goes.
//
// The PC that shares makes a code, shows it, and waits at the meeting place the code names.
// The PC that views takes the code and says hello with its SPAKE2 share. The PC that shares
// replies with its own share and a key confirmation. If the code was right, the confirmation
// checks out, and the PC that views sends its confirmation together with the sealed WebRTC
// offer. The PC that shares checks that confirmation, and sends back the sealed answer.
// WebRTC then links the two PCs straight to each other, DTLS checks the fingerprints the
// sealed messages carried, so no relay could have put itself in between.
//
// Relays keep nothing, so whatever has no answer yet is said again every 1.5 seconds.

type route struct {
	Mode     string   `json:"mode"`               // "direct" or "phone"
	BindIP   string   `json:"bindIp,omitempty"`   // the phone's network on this PC: USB tethering or the phone's hotspot
	Tunnel   string   `json:"tunnel,omitempty"`   // the phone helper, 127.0.0.1:port, in Phone VPN mode
	Proxy    string   `json:"proxy,omitempty"`    // HTTP proxy for the relays, host:port
	PhoneIPs []string `json:"phoneIps,omitempty"` // addresses of the phone's network on this PC, to name the path
}

type options struct {
	Op        string      `json:"op"`             // "host" shares this screen, "join" views another
	Code      string      `json:"code,omitempty"` // the code to join, or the easy access code a host keeps
	Name      string      `json:"name,omitempty"`
	Route     route       `json:"route"`
	Relays    []string    `json:"relays,omitempty"`
	Stun      []string    `json:"stun,omitempty"`
	Turn      []iceServer `json:"turn,omitempty"`      // TURN servers with their credentials, see turn.go
	TurnKey   *turnKey    `json:"turnKey,omitempty"`   // a Cloudflare TURN key, traded for credentials
	RelayOnly bool        `json:"relayOnly,omitempty"` // only through the TURN servers, to test them
	NoLAN     bool        `json:"noLan,omitempty"`
	Tries     *tries      `json:"tries,omitempty"` // how the easy access code stands, as PdaNet kept it
}

// tries is how an easy access code stands against wrong tries. The helper reports every change
// and PdaNet keeps it, so a lockout and its doubling outlast a restart of the helper or of PdaNet.
type tries struct {
	Wrong    int   `json:"wrong"`           // tries that went wrong this round
	Lockouts int   `json:"lockouts"`        // rounds that ended in a lockout, see lockFor
	Until    int64 `json:"until,omitempty"` // Unix seconds until which no try is answered
}

// sdpBox is what the sealed offers and answers carry.
type sdpBox struct {
	SDP  string `json:"sdp"`
	Name string `json:"name,omitempty"`
	Ver  string `json:"ver,omitempty"`
}

const (
	repeatEvery   = 1500 * time.Millisecond // the first copy of what waits for an answer
	repeatMax     = 12 * time.Second        // later copies wait twice as long, up to this
	maxWrongTries = 5                       // then the PC that shares makes a new code
	attemptTTL    = 30 * time.Second        // a hello without an offer by then counts as a wrong try
	findTimeout   = 30 * time.Second
	wrongWait     = 5 * time.Second // a reply that does not check out, and no other within this
	answerTimeout = 30 * time.Second
	lostTimeout   = 90 * time.Second
)

type session struct {
	link *ipc
	opt  options
	self string

	pnet     *phoneNet
	keepIP   func(net.IP) bool
	phoneIPs []net.IP
	stun     []string
	turn     []iceServer // ready once turnDone closes, see startTURN
	turnAt   time.Time
	turnDone chan struct{}
	dial     dialer

	mu      sync.Mutex
	rv      *rendezvous
	peer    *peer
	partner string
	box     *box
	relayUp int

	done chan struct{}
	once sync.Once
}

func runSession(in io.Reader, out io.Writer) int {
	link := newIPC(in, out)
	kind, first, err := link.read()
	if err != nil || kind != kindJSON {
		log.Printf("no start message: %v", err)
		return 2
	}
	var opt options
	if err := json.Unmarshal(first, &opt); err != nil {
		log.Printf("bad start message: %v", err)
		return 2
	}
	return newSession(link, opt).run()
}

func newSession(link *ipc, opt options) *session {
	return &session{link: link, opt: opt, self: randHex(8), done: make(chan struct{})}
}

func (s *session) run() int {
	go s.readLoop()
	if err := s.prepare(); err != nil {
		s.fail("noroute", err.Error())
	} else {
		s.startTURN()
		switch s.opt.Op {
		case "host":
			go s.host()
		case "join":
			go s.join()
		default:
			s.fail("internal", "unknown op "+s.opt.Op)
		}
	}
	<-s.done
	s.teardown()
	return 0
}

// prepare sets up the route: the relays and WebRTC go the way PdaNet chose.
func (s *session) prepare() error {
	s.stun = s.opt.Stun
	if len(s.stun) == 0 {
		s.stun = defaultStun
	} else if len(s.stun) == 1 && s.stun[0] == "none" {
		s.stun = nil
	}
	r := s.opt.Route
	for _, a := range r.PhoneIPs {
		if ip := net.ParseIP(a); ip != nil {
			s.phoneIPs = append(s.phoneIPs, ip)
		}
	}
	if r.Proxy != "" {
		u, err := url.Parse("http://" + r.Proxy)
		if err != nil {
			return err
		}
		s.dial.proxy = u
	}
	if r.Mode != "phone" {
		return nil
	}
	switch {
	case r.Tunnel != "":
		pn, err := newPhoneNet(r.Tunnel)
		if err != nil {
			return err
		}
		s.pnet = pn
	case r.BindIP != "":
		ip := net.ParseIP(r.BindIP)
		if ip == nil {
			return errors.New("bad address for the phone's network")
		}
		s.keepIP = func(x net.IP) bool { return x.Equal(ip) }
		if s.dial.proxy == nil {
			s.dial.bind = ip
		}
	default:
		return errors.New("PdaNet is not connected to the phone")
	}
	return nil
}

func (s *session) peerConfig(host bool) peerConfig {
	return peerConfig{
		host:      host,
		link:      s.link,
		pnet:      s.pnet,
		keepIP:    s.keepIP,
		stun:      s.stun,
		turn:      s.turnServers(),
		relayOnly: s.opt.RelayOnly,
		phoneIPs:  s.phoneIPs,
		viaPhone:  s.opt.Route.Mode == "phone",
	}
}

// startTURN gets the TURN servers ready while the pairing runs: a Cloudflare key takes one
// request to Cloudflare, through the same route as the relays.
func (s *session) startTURN() {
	s.turnDone = make(chan struct{})
	go func() {
		defer close(s.turnDone)
		s.turn = s.fetchTURN()
	}()
}

// fetchTURN gives the TURN servers PdaNet passed, and those a Cloudflare key brings.
func (s *session) fetchTURN() []iceServer {
	list := cleanTURN(s.opt.Turn)
	if k := s.opt.TurnKey; k != nil {
		ctx, cancel := context.WithTimeout(context.Background(), turnFetchWait)
		defer cancel()
		cf, err := cloudflareServers(ctx, s.dial, *k)
		if err != nil {
			log.Printf("relay: Cloudflare: %v", err)
			s.link.event(event{Ev: "relay", Reason: "key", Detail: err.Error()})
		}
		list = append(list, cf...)
	}
	if len(list) > 0 {
		log.Printf("relay: %s", turnURLs(list))
	}
	s.turnAt = time.Now()
	return list
}

// turnServers waits for startTURN. Cloudflare's credentials last a day and an easy access share
// can wait longer for the other PC, so then it asks for new ones.
func (s *session) turnServers() []iceServer {
	select {
	case <-s.turnDone:
	case <-s.done:
		return nil
	}
	if s.opt.TurnKey != nil && time.Since(s.turnAt) > turnTTL*time.Second/2 {
		s.turn = s.fetchTURN()
	}
	return s.turn
}

// readLoop takes what PdaNet sends: the screen, remote control and stop.
func (s *session) readLoop() {
	for {
		kind, payload, err := s.link.read()
		if err != nil {
			s.end(event{Ev: "ended", Reason: "closed"}, true)
			return
		}
		p := s.currentPeer()
		switch kind {
		case kindJSON:
			var c struct {
				Op string `json:"op"`
			}
			_ = json.Unmarshal(payload, &c)
			switch c.Op {
			case "stop":
				s.end(event{Ev: "ended", Reason: "stopped"}, true)
			case "keyframe":
				if p != nil {
					p.requestKeyframe()
				}
			}
		case kindVideo:
			if p != nil {
				p.writeVideo(payload)
			}
		case kindCtl:
			if p != nil {
				p.sendCtl(payload)
			}
		case kindLive:
			if p != nil {
				p.sendLive(payload)
			}
		}
	}
}

// ============================ The PC that shares ============================

type attempt struct {
	pa         string
	keys       *pakeKeys
	reply      wireMsg
	lastReply  time.Time
	started    time.Time
	working    bool
	answer     *wireMsg
	lastAnswer time.Time
}

func (s *session) host() {
	// Easy access brings a code that stays the same, else every share gets a new one.
	code, fixed := newCode(), s.opt.Code != ""
	if fixed {
		c, err := parseCode(s.opt.Code)
		if err != nil {
			s.fail("badcode", err.Error())
			return
		}
		code = c
	}
	rv := s.openRendezvous(code.room())
	s.link.event(event{Ev: "code", Code: string(code)})
	waitingSince := time.Now()

	pending := map[string]*attempt{}
	wrong, lockouts := 0, 0
	var lockedUntil time.Time
	if t := s.opt.Tries; fixed && t != nil {
		wrong, lockouts = min(max(t.Wrong, 0), maxWrongTries-1), max(t.Lockouts, 0)
		if t.Until > 0 {
			lockedUntil = time.Unix(t.Until, 0)
		}
		if wait := time.Until(lockedUntil); wait > 0 {
			s.link.event(event{Ev: "locked", Wait: int(wait.Seconds())})
		}
	}
	// report tells PdaNet how the easy access code stands, for it to keep.
	report := func() {
		if fixed {
			t := tries{Wrong: wrong, Lockouts: lockouts}
			if !lockedUntil.IsZero() {
				t.Until = lockedUntil.Unix()
			}
			s.link.event(event{Ev: "tries", Tries: &t})
		}
	}
	var partner string
	answers := make(chan wireMsg, 1)
	restarts := make(chan wireMsg, 1)
	var restartSDP string
	var restartAnswer *wireMsg

	countWrong := func() {
		wrong++
		if wrong < maxWrongTries {
			report()
			s.link.event(event{Ev: "wrongtry", Left: maxWrongTries - wrong})
			return
		}
		wrong = 0
		clear(pending)
		if fixed {
			// The owner of an easy access code must still get in, so the code stays, and
			// nobody gets another try for a while instead, longer each time.
			wait := lockFor(lockouts)
			lockouts++
			lockedUntil = time.Now().Add(wait)
			report()
			s.link.event(event{Ev: "locked", Wait: int(wait.Seconds())})
			return
		}
		// Too many wrong tries: whoever tries learns nothing more from this code.
		code = newCode()
		rv = s.openRendezvous(code.room())
		waitingSince = time.Now()
		s.link.event(event{Ev: "code", Code: string(code), Reason: "tries"})
	}

	tick := time.NewTicker(repeatEvery)
	defer tick.Stop()
	for {
		select {
		case <-s.done:
			return

		case m := <-rv.in:
			switch m.T {
			case "hello":
				if partner != "" || m.To != "" || m.PA == "" || time.Now().Before(lockedUntil) {
					continue
				}
				if at := pending[m.F]; at != nil {
					if at.pa == m.PA && time.Since(at.lastReply) > repeatEvery/2 {
						at.lastReply = time.Now()
						rv.publish(at.reply)
					}
					continue
				}
				// Each answer lets whoever asked test one guess at the code, so no more go out
				// than wrong tries are left this round: a stranger gets maxWrongTries guesses
				// before the code changes, or easy access waits.
				if wrong+len(pending) >= maxWrongTries {
					continue
				}
				sp, err := newSpake2(false, code.w(), nil)
				if err != nil {
					continue
				}
				keys, err := sp.finish(viewerID(m.F), hostID(s.self), unb64(m.PA), pakeAAD(code.room()))
				if err != nil {
					continue // not a real share, no try counted
				}
				reply := wireMsg{T: "reply", To: m.F, PB: b64(sp.share), CB: b64(keys.confirmB)}
				pending[m.F] = &attempt{pa: m.PA, keys: keys, reply: reply, lastReply: time.Now(), started: time.Now()}
				rv.publish(reply)

			case "offer":
				at := pending[m.F]
				if m.To != s.self || at == nil || (partner != "" && partner != m.F) {
					continue
				}
				if at.answer != nil {
					if time.Since(at.lastAnswer) > repeatEvery/2 {
						at.lastAnswer = time.Now()
						rv.publish(*at.answer)
					}
					continue
				}
				if at.working {
					continue
				}
				if !hmac.Equal(unb64(m.CA), at.keys.confirmA) {
					delete(pending, m.F)
					countWrong()
					continue
				}
				bx, err := newBox(at.keys.ke)
				if err != nil {
					continue
				}
				plain, err := bx.open("offer", m.F, s.self, m.Box)
				var offer sdpBox
				if err != nil || json.Unmarshal(plain, &offer) != nil || offer.SDP == "" {
					continue
				}
				// The code is used: nobody else gets in with it. The others each got an answer,
				// so each tested a guess, and counts as a wrong try.
				partner = m.F
				at.working = true
				for id := range pending {
					if id != partner {
						delete(pending, id)
						wrong++
					}
				}
				report()
				s.setPartner(partner, bx)
				s.link.event(event{Ev: "paired", Peer: cleanName(offer.Name)})
				viewer := partner
				go func() {
					ans, err := s.hostAnswer(viewer, bx, offer.SDP)
					if err != nil {
						s.fail("internal", err.Error())
						return
					}
					answers <- ans
				}()

			case "restart":
				if m.To != s.self || m.F != partner {
					continue
				}
				bx := s.currentBox()
				plain, err := bx.open("restart", m.F, s.self, m.Box)
				var offer sdpBox
				if err != nil || json.Unmarshal(plain, &offer) != nil || offer.SDP == "" {
					continue
				}
				if offer.SDP == restartSDP {
					if restartAnswer != nil {
						rv.publish(*restartAnswer)
					}
					continue
				}
				restartSDP, restartAnswer = offer.SDP, nil
				p := s.currentPeer()
				viewer := partner
				go func() {
					sdp, err := p.restartAnswer(offer.SDP)
					if err != nil {
						log.Printf("restart: %v", err)
						return
					}
					body, _ := json.Marshal(sdpBox{SDP: sdp})
					restarts <- wireMsg{T: "rans", To: viewer, Box: bx.seal("rans", s.self, viewer, body)}
				}()

			case "bye":
				if s.isBye(m, partner) {
					s.end(event{Ev: "ended", Reason: "peer"}, false)
					return
				}
			}

		case a := <-answers:
			if at := pending[a.To]; at != nil {
				at.answer, at.lastAnswer = &a, time.Now()
			}
			rv.publish(a)

		case a := <-restarts:
			restartAnswer = &a
			rv.publish(a)

		case <-tick.C:
			s.reportRelays(rv)
			if partner == "" && len(pending) == 0 && time.Since(waitingSince) > 20*time.Second {
				rv.startLAN() // the other PC may only reach this one locally
			}
			if partner == "" {
				for id, at := range pending {
					if time.Since(at.started) > attemptTTL {
						delete(pending, id)
						countWrong()
					}
				}
			} else if p := s.currentPeer(); p != nil && p.lostFor() > lostTimeout {
				s.end(event{Ev: "ended", Reason: "lost"}, false)
				return
			}
		}
	}
}

func (s *session) hostAnswer(viewer string, bx *box, offer string) (wireMsg, error) {
	p, err := newPeer(s.peerConfig(true))
	if err != nil {
		return wireMsg{}, err
	}
	s.setPeer(p)
	sdp, err := p.answer(offer)
	if err != nil {
		return wireMsg{}, err
	}
	body, _ := json.Marshal(sdpBox{SDP: sdp, Name: s.opt.Name, Ver: version})
	return wireMsg{T: "answer", To: viewer, Box: bx.seal("answer", s.self, viewer, body)}, nil
}

// ============================ The PC that views ============================

func (s *session) join() {
	code, err := parseCode(s.opt.Code)
	if err != nil {
		s.fail("badcode", err.Error())
		return
	}
	rv := s.openRendezvous(code.room())

	// The offer gathers its candidates while the pairing runs.
	p, err := newPeer(s.peerConfig(false))
	if err != nil {
		s.fail("internal", err.Error())
		return
	}
	s.setPeer(p)
	offers := make(chan string, 1)
	go func() {
		sdp, err := p.offer()
		if err != nil {
			s.fail("internal", err.Error())
			return
		}
		offers <- sdp
	}()

	sp, err := newSpake2(true, code.w(), nil)
	if err != nil {
		s.fail("internal", err.Error())
		return
	}
	hello := wireMsg{T: "hello", PA: b64(sp.share)}
	rv.ask(hello)
	var again repeater // the copies of what waits for an answer
	again.sent(true)

	var (
		host, offer       string
		keys              *pakeKeys
		bx                *box
		answered          bool
		started           = time.Now()
		foundAt, badSince time.Time
		restarting        bool
		restartSDP        string
		restartAt         time.Time
	)
	restartOffers := make(chan string, 1)

	sendOffer := func(first bool) {
		body, _ := json.Marshal(sdpBox{SDP: offer, Name: s.opt.Name, Ver: version})
		rv.ask(wireMsg{T: "offer", To: host, CA: b64(keys.confirmA), Box: bx.seal("offer", s.self, host, body)})
		again.sent(first)
	}
	sendRestart := func(first bool) {
		body, _ := json.Marshal(sdpBox{SDP: restartSDP})
		rv.ask(wireMsg{T: "restart", To: host, Box: bx.seal("restart", s.self, host, body)})
		again.sent(first)
	}

	tick := time.NewTicker(repeatEvery)
	defer tick.Stop()
	for {
		select {
		case <-s.done:
			return

		case sdp := <-offers:
			offer = sdp
			if host != "" && !answered {
				sendOffer(true)
			}

		case m := <-rv.in:
			if m.To != s.self {
				continue
			}
			switch m.T {
			case "reply":
				if host != "" {
					continue
				}
				k, err := sp.finish(viewerID(s.self), hostID(m.F), unb64(m.PB), pakeAAD(code.room()))
				if err != nil || !hmac.Equal(unb64(m.CB), k.confirmB) {
					// Someone waits with these four digits, but not with this code. Another PC
					// may still answer, so wait a little before saying the code is wrong.
					if badSince.IsZero() {
						badSince = time.Now()
					}
					continue
				}
				if bx, err = newBox(k.ke); err != nil {
					continue
				}
				host, keys, foundAt = m.F, k, time.Now()
				s.setPartner(host, bx)
				s.link.event(event{Ev: "found"})
				if offer != "" {
					sendOffer(true)
				}

			case "answer":
				if m.F != host || answered {
					continue
				}
				plain, err := bx.open("answer", m.F, s.self, m.Box)
				var ans sdpBox
				if err != nil || json.Unmarshal(plain, &ans) != nil || ans.SDP == "" {
					continue
				}
				if err := p.accept(ans.SDP); err != nil {
					s.fail("internal", err.Error())
					return
				}
				answered = true
				rv.answered()
				s.link.event(event{Ev: "paired", Peer: cleanName(ans.Name)})

			case "rans":
				if m.F != host || !restarting {
					continue
				}
				plain, err := bx.open("rans", m.F, s.self, m.Box)
				var ans sdpBox
				if err != nil || json.Unmarshal(plain, &ans) != nil || ans.SDP == "" {
					continue
				}
				if err := p.accept(ans.SDP); err != nil {
					log.Printf("restart: %v", err)
					continue
				}
				restarting, restartSDP = false, ""
				rv.answered()

			case "bye":
				if s.isBye(m, host) {
					s.end(event{Ev: "ended", Reason: "peer"}, false)
					return
				}
			}

		case <-p.needRestart:
			// The network changed or dropped: look for a new path, the pairing still holds.
			if !answered || (restarting && time.Since(restartAt) < 8*time.Second) {
				continue
			}
			restarting, restartSDP, restartAt = true, "", time.Now()
			s.link.event(event{Ev: "reconnecting"})
			go func() {
				sdp, err := p.restartOffer()
				if err != nil {
					log.Printf("restart: %v", err)
					return
				}
				restartOffers <- sdp
			}()

		case sdp := <-restartOffers:
			restartSDP = sdp
			sendRestart(true)

		case <-tick.C:
			s.reportRelays(rv)
			switch {
			case host == "":
				if time.Since(started) > 8*time.Second {
					rv.startLAN() // the other PC may only be reachable locally
				}
				if !badSince.IsZero() && time.Since(badSince) > wrongWait {
					s.fail("wrongcode", "")
					return
				}
				if time.Since(started) > findTimeout {
					s.fail("nohost", "")
					return
				}
				if again.due() {
					rv.ask(hello)
					again.sent(false)
				}
			case !answered:
				if time.Since(foundAt) > answerTimeout {
					s.fail("noanswer", "")
					return
				}
				if offer != "" && again.due() {
					sendOffer(false)
				}
			case restarting:
				if p.isConnected() {
					restarting = false
					rv.answered()
				} else if restartSDP != "" && again.due() {
					sendRestart(false)
				}
			}
			if answered && p.lostFor() > lostTimeout {
				s.end(event{Ev: "ended", Reason: "lost"}, false)
				return
			}
		}
	}
}

// ============================ Shared parts ============================

func (s *session) openRendezvous(room string) *rendezvous {
	relays := s.opt.Relays
	if len(relays) == 0 {
		relays = defaultRelays
	}
	rv := newRendezvous(room, s.self, relays, !s.opt.NoLAN, s.dial)
	s.mu.Lock()
	old := s.rv
	s.rv = rv
	s.relayUp = -1
	s.mu.Unlock()
	if old != nil {
		old.close()
	}
	return rv
}

// lockFor is how long an easy access code takes no tries after round n of wrong ones: a
// quarter of an hour, twice as long each round after, a day at most. A stranger who knows the
// first four digits then gets a handful of guesses a day at the last six.
func lockFor(n int) time.Duration {
	return min(15*time.Minute<<min(n, 7), 24*time.Hour)
}

// repeater spaces out the copies of a message that waits for an answer. A copy only matters
// when every relay lost the one before, and public relays ban those who post too often, so
// each copy waits twice as long as the one before, up to repeatMax.
type repeater struct {
	next time.Time
	gap  time.Duration
}

// sent notes a copy that just went out. The first copy of a new message starts over.
func (r *repeater) sent(first bool) {
	if first || r.gap == 0 {
		r.gap = repeatEvery
	} else {
		r.gap = min(2*r.gap, repeatMax)
	}
	r.next = time.Now().Add(r.gap)
}

// due tells whether the next copy should go out now. The ticks that check come every
// repeatEvery, so one a little early counts.
func (r *repeater) due() bool {
	return !time.Now().Add(repeatEvery / 4).Before(r.next)
}

// reportRelays tells PdaNet how many relays are up, when that changes.
func (s *session) reportRelays(rv *rendezvous) {
	up, total := rv.status()
	s.mu.Lock()
	changed := up != s.relayUp
	s.relayUp = up
	s.mu.Unlock()
	if changed {
		s.link.event(event{Ev: "relays", Up: up, Total: total})
	}
}

func (s *session) setPeer(p *peer) {
	s.mu.Lock()
	old := s.peer
	s.peer = p
	s.mu.Unlock()
	if old != nil {
		old.close()
	}
}

func (s *session) currentPeer() *peer {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.peer
}

func (s *session) setPartner(id string, bx *box) {
	s.mu.Lock()
	s.partner, s.box = id, bx
	s.mu.Unlock()
}

func (s *session) currentBox() *box {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.box
}

// isBye checks that a bye comes from the other PC: only it can seal one.
func (s *session) isBye(m wireMsg, from string) bool {
	bx := s.currentBox()
	if from == "" || m.F != from || bx == nil {
		return false
	}
	_, err := bx.open("bye", m.F, s.self, m.Box)
	return err == nil
}

// end finishes the session once: it tells the other PC when asked, then PdaNet.
func (s *session) end(e event, bye bool) {
	s.once.Do(func() {
		if bye {
			s.mu.Lock()
			rv, to, bx := s.rv, s.partner, s.box
			s.mu.Unlock()
			if rv != nil && to != "" && bx != nil {
				rv.publish(wireMsg{T: "bye", To: to, Box: bx.seal("bye", s.self, to, []byte("{}"))})
				time.Sleep(300 * time.Millisecond) // the relays send in the background
			}
		}
		s.link.event(e)
		close(s.done)
	})
}

func (s *session) fail(reason, detail string) {
	if detail != "" {
		log.Printf("failed, %s: %s", reason, detail)
	}
	s.end(event{Ev: "failed", Reason: reason, Detail: detail}, false)
}

func (s *session) teardown() {
	s.mu.Lock()
	p, rv := s.peer, s.rv
	s.mu.Unlock()
	if p != nil {
		p.close()
	}
	if rv != nil {
		rv.close()
	}
}

// cleanName keeps a name from the other PC printable and short.
func cleanName(n string) string {
	n = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}
		return r
	}, n)
	if r := []rune(strings.TrimSpace(n)); len(r) > 48 {
		n = string(r[:48])
	}
	return strings.TrimSpace(n)
}
