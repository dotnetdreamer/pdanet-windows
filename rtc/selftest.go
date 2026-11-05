package main

import (
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/pion/logging"
	"github.com/pion/stun/v4"
	"github.com/pion/turn/v5"
)

// runSelftest checks the parts that must be exactly right, then runs a whole session between
// two helpers in this process: pairing, WebRTC on this PC's own addresses, both data channels
// and one frame of screen. It needs no internet.
//
// "selftest phone 127.0.0.1:<port>" instead checks the route through the phone helper at that
// address: a name lookup and STUN through it, then a session that goes through it.
func runSelftest(args []string) int {
	failed := 0
	check := func(name string, err error) {
		if err != nil {
			failed++
			fmt.Printf("FAIL %s: %v\n", name, err)
			return
		}
		fmt.Printf("ok   %s\n", name)
	}
	if len(args) == 2 && args[0] == "phone" {
		check("STUN through the phone helper", phoneSTUN(args[1]))
		_, err := loopback(route{Mode: "phone", Tunnel: args[1]}, route{Mode: "direct"}, options{Stun: defaultStun})
		check("a whole session through the phone helper", err)
		if failed > 0 {
			return 1
		}
		fmt.Println("all checks passed")
		return 0
	}
	check("SPAKE2 test vector 1 (RFC 9382)", spakeVector("server", "client",
		"2ee57912099d31560b3a44b1184b9b4866e904c49d12ac5042c97dca461b1a5f",
		"43dd0fd7215bdcb482879fca3220c6a968e66d70b1356cac18bb26c84a78d729",
		"dcb60106f276b02606d8ef0a328c02e4b629f84f89786af5befb0bc75b6e66be",
		"04a56fa807caaa53a4d28dbb9853b9815c61a411118a6fe516a8798434751470f9010153ac33d0d5f2047ffdb1a3e42c9b4e6be662766e1eeb4116988ede5f912c",
		"0406557e482bd03097ad0cbaa5df82115460d951e3451962f1eaf4367a420676d09857ccbc522686c83d1852abfa8ed6e4a1155cf8f1543ceca528afb591a1e0b7",
		"0e0672dc86f8e45565d338b0540abe69",
		"58ad4aa88e0b60d5061eb6b5dd93e80d9c4f00d127c65b3b35b1b5281fee38f0",
		"d3e2e547f1ae04f2dbdbf0fc4b79f8ecff2dff314b5d32fe9fcef2fb26dc459b"))
	check("SPAKE2 test vector 2 (RFC 9382)", spakeVector("", "client",
		"0548d8729f730589e579b0475a582c1608138ddf7054b73b5381c7e883e2efae",
		"403abbe3b1b4b9ba17e3032849759d723939a27a27b9d921c500edde18ed654b",
		"903023b6598908936ea7c929bd761af6039577a9c3f9581064187c3049d87065",
		"04a897b769e681c62ac1c2357319a3d363f610839c4477720d24cbe32f5fd85f44fb92ba966578c1b712be6962498834078262caa5b441ecfa9d4a9485720e918a",
		"04e0f816fd1c35e22065d5556215c097e799390d16661c386e0ecc84593974a61b881a8c82327687d0501862970c64565560cb5671f696048050ca66ca5f8cc7fc",
		"642f05c473c2cd79909f9a841e2f30a7",
		"47d29e6666af1b7dd450d571233085d7a9866e4d49d2645e2df975489521232b",
		"3313c5cefc361d27fb16847a91c2a73b766ffa90a4839122a9b70a2f6bd1d6df"))
	check("a wrong code fails the key confirmation", wrongCode())
	check("sealed messages", boxRoundTrip())
	check("repeats, kinds of address, easy access waits", spacingAndKinds())
	check("wrong guesses at an easy access code, and a restart", wrongTries())
	_, err := loopback(route{Mode: "direct"}, route{Mode: "direct"}, options{Stun: []string{"none"}})
	check("a whole session on this PC", err)
	_, err = loopback(route{Mode: "direct"}, route{Mode: "direct"}, options{Stun: []string{"none"}, Code: "5550123456"})
	check("a whole session with an easy access code", err)
	check("a whole session through a TURN relay over TCP", turnLoopback())
	if failed > 0 {
		fmt.Printf("%d check(s) failed\n", failed)
		return 1
	}
	fmt.Println("all checks passed")
	return 0
}

// spacingAndKinds checks the gaps between the copies of a message that waits for an answer,
// the kinds of address read out of an SDP, and how long easy access waits after wrong tries.
func spacingAndKinds() error {
	var r repeater
	var gaps []time.Duration
	r.sent(true)
	gaps = append(gaps, r.gap)
	for range 5 {
		r.sent(false)
		gaps = append(gaps, r.gap)
	}
	want := []time.Duration{1500 * time.Millisecond, 3 * time.Second, 6 * time.Second, 12 * time.Second, 12 * time.Second, 12 * time.Second}
	if !slices.Equal(gaps, want) {
		return fmt.Errorf("gaps %v, want %v", gaps, want)
	}
	if r.sent(true); r.gap != repeatEvery {
		return errors.New("a new message does not start over")
	}
	sdp := "v=0\r\n" +
		"a=candidate:1 1 udp 2130706431 192.168.1.20 50000 typ host\r\n" +
		"a=candidate:2 1 udp 1694498815 203.0.113.7 50000 typ srflx raddr 0.0.0.0 rport 50000\r\n" +
		"a=candidate:3 1 udp 2130706431 100.70.1.2 50002 typ host\r\n" +
		"a=end-of-candidates\r\n"
	for _, c := range []struct{ sdp, want string }{
		{sdp, "host srflx"},
		{strings.Replace(sdp, "a=end-of-candidates\r\n", "", 1), "host srflx partial"},
		{"v=0\r\na=candidate:4 1 udp 2130706431 198.51.100.9 50004 typ host\r\na=end-of-candidates\r\n", "public"},
		{"v=0\r\na=end-of-candidates\r\n", "none"},
		{"v=0\r\n", "none partial"},
	} {
		if k := candidateKinds(c.sdp); k != c.want {
			return fmt.Errorf("kinds %q, want %q", k, c.want)
		}
	}
	locks := []time.Duration{lockFor(0), lockFor(1), lockFor(6), lockFor(7), lockFor(60)}
	if !slices.Equal(locks, []time.Duration{15 * time.Minute, 30 * time.Minute, 16 * time.Hour, 24 * time.Hour, 24 * time.Hour}) {
		return fmt.Errorf("easy access waits %v after wrong tries", locks)
	}
	return nil
}

// wrongTries plays strangers who know the first four digits of an easy access code: of eight
// guesses sent at once only five get an answer, five wrong ones lock the code, and a host that
// starts again with what PdaNet kept is still locked, and answers no guess.
func wrongTries() error {
	hub := "mem+selftest-" + randHex(4)
	code := pairCode("5550123456")
	opt := options{Op: "host", Name: "Sharing PC", Code: string(code), Route: route{Mode: "direct"},
		Relays: []string{hub}, Stun: []string{"none"}, NoLAN: true}
	host := startHelper(opt)
	defer host.stop()
	if _, err := host.waitFor("code", 5*time.Second); err != nil {
		return fmt.Errorf("host: %w", err)
	}
	answered := guess(hub, code.room(), 8)
	defer closeAll(answered)
	if len(answered) != maxWrongTries {
		return fmt.Errorf("%d of 8 guesses got an answer, want %d", len(answered), maxWrongTries)
	}
	// Each one that got an answer knows its guess is wrong, and goes on with a wrong offer.
	for _, g := range answered {
		g.rv.publish(wireMsg{T: "offer", To: g.host, CA: b64([]byte("wrong"))})
	}
	var kept *tries
	for deadline := time.After(5 * time.Second); kept == nil || kept.Until == 0; {
		select {
		case e, ok := <-host.events:
			if !ok {
				return errors.New("host stopped")
			}
			if e.Ev == "tries" {
				kept = e.Tries
			}
		case <-deadline:
			return fmt.Errorf("five wrong guesses did not lock the code, kept %+v", kept)
		}
	}
	if kept.Lockouts != 1 || time.Until(time.Unix(kept.Until, 0)) < 14*time.Minute {
		return fmt.Errorf("after five wrong guesses PdaNet keeps %+v", *kept)
	}
	host.stop()

	// PdaNet starts the helper again, with what it kept.
	opt.Tries = kept
	again := startHelper(opt)
	defer again.stop()
	if _, err := again.waitFor("locked", 5*time.Second); err != nil {
		return fmt.Errorf("a host that starts again is not locked: %w", err)
	}
	late := guess(hub, code.room(), 1)
	defer closeAll(late)
	if len(late) != 0 {
		return errors.New("a host that starts again answers guesses while locked")
	}
	return nil
}

// guesser is a PC that guessed the code, and the host that answered it.
type guesser struct {
	rv   *rendezvous
	host string
}

// guess sends hellos at once from n PCs with a wrong code for the room, and gives back those
// that got an answer within a second and a half.
func guess(hub, room string, n int) []guesser {
	wrong := pairCode(room + "999999")
	answers := make(chan guesser, n)
	var all []*rendezvous
	for range n {
		rv := newRendezvous(room, "guess-"+randHex(6), []string{hub}, false, dialer{})
		all = append(all, rv)
		sp, err := newSpake2(true, wrong.w(), nil)
		if err != nil {
			continue
		}
		go func() {
			for {
				select {
				case m := <-rv.in:
					if m.T == "reply" && m.To == rv.self {
						answers <- guesser{rv: rv, host: m.F}
						return
					}
				case <-rv.done:
					return
				}
			}
		}()
		rv.publish(wireMsg{T: "hello", PA: b64(sp.share)})
	}
	time.Sleep(1500 * time.Millisecond)
	var got []guesser
	for len(answers) > 0 {
		got = append(got, <-answers)
	}
	for _, rv := range all {
		if !slices.ContainsFunc(got, func(g guesser) bool { return g.rv == rv }) {
			rv.close()
		}
	}
	return got
}

func closeAll(gs []guesser) {
	for _, g := range gs {
		g.rv.close()
	}
}

// stop ends a helper the way PdaNet does, and waits for it to go.
func (f *fakePdaNet) stop() {
	_ = f.link.write(kindJSON, []byte(`{"op":"stop"}`))
	for deadline := time.After(3 * time.Second); ; {
		select {
		case _, ok := <-f.events:
			if !ok {
				return
			}
		case <-deadline:
			return
		}
	}
}

func spakeVector(idA, idB, w, x, y, pA, pB, ke, cA, cB string) error {
	num := func(h string) *big.Int { v, _ := new(big.Int).SetString(h, 16); return v }
	a, err := newSpake2(true, num(w), num(x))
	if err != nil {
		return err
	}
	b, err := newSpake2(false, num(w), num(y))
	if err != nil {
		return err
	}
	if hex.EncodeToString(a.share) != pA || hex.EncodeToString(b.share) != pB {
		return errors.New("shares differ from the RFC")
	}
	ka, err := a.finish(idA, idB, b.share, nil)
	if err != nil {
		return err
	}
	kb, err := b.finish(idA, idB, a.share, nil)
	if err != nil {
		return err
	}
	for _, c := range []struct {
		got  []byte
		want string
	}{
		{ka.ke, ke}, {kb.ke, ke}, {ka.confirmA, cA}, {kb.confirmA, cA}, {ka.confirmB, cB}, {kb.confirmB, cB},
	} {
		if hex.EncodeToString(c.got) != c.want {
			return fmt.Errorf("got %x, want %s", c.got, c.want)
		}
	}
	return nil
}

func wrongCode() error {
	right, wrong := pairCode("1234567890"), pairCode("1234567891")
	a, _ := newSpake2(true, wrong.w(), nil)
	b, _ := newSpake2(false, right.w(), nil)
	ka, err := a.finish("a", "b", b.share, pakeAAD(right.room()))
	if err != nil {
		return err
	}
	kb, err := b.finish("a", "b", a.share, pakeAAD(right.room()))
	if err != nil {
		return err
	}
	if bytes.Equal(ka.confirmB, kb.confirmB) || bytes.Equal(ka.ke, kb.ke) {
		return errors.New("a wrong code produced the same keys")
	}
	if _, err := parseCode("482 619-3075"); err != nil {
		return err
	}
	if _, err := parseCode("48261930"); err == nil {
		return errors.New("a short code was taken")
	}
	return nil
}

func boxRoundTrip() error {
	k := make([]byte, 16)
	bx, err := newBox(k)
	if err != nil {
		return err
	}
	msg := bytes.Repeat([]byte("v=0 a=candidate "), 200)
	sealed := bx.seal("offer", "a", "b", msg)
	got, err := bx.open("offer", "a", "b", sealed)
	if err != nil || !bytes.Equal(got, msg) {
		return fmt.Errorf("round trip: %v", err)
	}
	if _, err := bx.open("answer", "a", "b", sealed); err == nil {
		return errors.New("a sealed offer opened as an answer")
	}
	if _, err := bx.open("offer", "b", "a", sealed); err == nil {
		return errors.New("a sealed offer opened in the other direction")
	}
	if len(sealed) > len(msg)/2 {
		return fmt.Errorf("SDP did not compress: %d of %d", len(sealed), len(msg))
	}
	return nil
}

// fakePdaNet is the PdaNet side of a helper in this process.
type fakePdaNet struct {
	link   *ipc
	events chan event
	ctl    chan []byte
	video  chan []byte
}

func startHelper(opt options) *fakePdaNet {
	toHelperR, toHelperW := io.Pipe()
	fromHelperR, fromHelperW := io.Pipe()
	f := &fakePdaNet{
		link:   newIPC(fromHelperR, toHelperW),
		events: make(chan event, 64),
		ctl:    make(chan []byte, 8),
		video:  make(chan []byte, 8),
	}
	go func() { runSession(toHelperR, fromHelperW); _ = fromHelperW.Close() }()
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
				f.events <- e
			case kindCtl:
				f.ctl <- b
			case kindVideo:
				f.video <- b
			}
		}
	}()
	return f
}

func (f *fakePdaNet) waitFor(ev string, d time.Duration) (event, error) {
	deadline := time.After(d)
	for {
		select {
		case e, ok := <-f.events:
			if !ok {
				return event{}, errors.New("helper stopped")
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

// loopback runs a whole session between two helpers in this process, with the STUN and TURN
// servers in base, and gives the path the sharing PC names. With a code in base, the host keeps
// it, the way easy access does.
func loopback(hostRoute, viewRoute route, base options) (string, error) {
	hub := "mem+selftest-" + randHex(4)
	base.Relays, base.NoLAN = []string{hub}, true

	hostOpt := base
	hostOpt.Op, hostOpt.Name, hostOpt.Route = "host", "Sharing PC", hostRoute
	host := startHelper(hostOpt)
	e, err := host.waitFor("code", 5*time.Second)
	if err != nil {
		return "", fmt.Errorf("host: %w", err)
	}
	if hostOpt.Code != "" && e.Code != hostOpt.Code {
		return "", fmt.Errorf("the host shares with %s, not with its own code", e.Code)
	}

	viewOpt := base
	viewOpt.Op, viewOpt.Name, viewOpt.Code, viewOpt.Route = "join", "Viewing PC", e.Code, viewRoute
	view := startHelper(viewOpt)

	var wg sync.WaitGroup
	var hostErr, viewErr error
	var paired, connected event
	wg.Add(2)
	go func() {
		defer wg.Done()
		if paired, hostErr = host.waitFor("paired", 15*time.Second); hostErr == nil {
			connected, hostErr = host.waitFor("connected", 20*time.Second)
		}
	}()
	go func() { defer wg.Done(); _, viewErr = view.waitFor("connected", 35*time.Second) }()
	wg.Wait()
	if hostErr != nil || viewErr != nil {
		return "", fmt.Errorf("connect: host %v, viewer %v", hostErr, viewErr)
	}
	if paired.Peer != "Viewing PC" {
		return "", fmt.Errorf("the sharing PC sees %q", paired.Peer)
	}

	// Remote control both ways, on the reliable channel.
	_ = view.link.write(kindCtl, []byte("key down"))
	_ = host.link.write(kindCtl, []byte("cursor"))
	for _, c := range []struct {
		ch   chan []byte
		want string
	}{{host.ctl, "key down"}, {view.ctl, "cursor"}} {
		select {
		case b := <-c.ch:
			if string(b) != c.want {
				return "", fmt.Errorf("control message %q, want %q", b, c.want)
			}
		case <-time.After(10 * time.Second):
			return "", fmt.Errorf("control message %q did not arrive", c.want)
		}
	}

	// One keyframe of screen, big enough to need many packets.
	frame := append([]byte{0, 0, 0, 1, 0x65}, bytes.Repeat([]byte{0x88, 0x84, 0x21}, 20000)...)
	hdr := make([]byte, 5)
	binary.LittleEndian.PutUint32(hdr, 33333)
	hdr[4] = 1
	deadline := time.After(15 * time.Second)
	for {
		_ = host.link.write(kindVideo, hdr, frame)
		select {
		case b := <-view.video:
			if len(b) < 5 || b[0]&frameKey == 0 || !bytes.Equal(b[5:], frame) {
				return "", fmt.Errorf("the frame arrived changed: %d bytes, flags %d", len(b), b[0])
			}
			_ = view.link.write(kindJSON, []byte(`{"op":"stop"}`))
			if _, err := host.waitFor("ended", 10*time.Second); err != nil {
				return "", fmt.Errorf("the sharing PC did not hear the viewer leave: %w", err)
			}
			return connected.Path, nil
		case <-time.After(500 * time.Millisecond):
		case <-deadline:
			return "", errors.New("no frame arrived")
		}
	}
}

// turnLoopback runs a TURN server on this PC, on TCP as a company network would leave it, and a
// whole session that may only go through it.
func turnLoopback() error {
	ln, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	pass := randHex(16)
	key := turn.GenerateAuthKey("pdanet", "pdanet", pass)
	lf := logging.NewDefaultLoggerFactory()
	lf.DefaultLogLevel = logging.LogLevelDisabled
	server, err := turn.NewServer(turn.ServerConfig{
		Realm: "pdanet",
		AuthHandler: func(ra *turn.RequestAttributes) (string, []byte, bool) {
			return ra.Username, key, ra.Username == "pdanet"
		},
		ListenerConfigs: []turn.ListenerConfig{{
			Listener:              ln,
			RelayAddressGenerator: &turn.RelayAddressGeneratorStatic{RelayAddress: net.IPv4(127, 0, 0, 1), Address: "127.0.0.1"},
		}},
		LoggerFactory: lf,
	})
	if err != nil {
		_ = ln.Close()
		return err
	}
	defer server.Close()
	path, err := loopback(route{Mode: "direct"}, route{Mode: "direct"}, options{
		Stun:      []string{"none"},
		Turn:      []iceServer{{URLs: []string{"turn:" + ln.Addr().String() + "?transport=tcp"}, Username: "pdanet", Credential: pass}},
		RelayOnly: true,
	})
	if err != nil {
		return err
	}
	if path != "relay" {
		return fmt.Errorf("the session went %q, not through the relay", path)
	}
	return nil
}

// phoneSTUN asks a STUN server for this route's public address, through a socket on the phone
// helper, with the server's name looked up there too.
func phoneSTUN(agent string) error {
	pn, err := newPhoneNet(agent)
	if err != nil {
		return err
	}
	server, err := pn.ResolveUDPAddr("udp4", "stun.l.google.com:19302")
	if err != nil {
		return fmt.Errorf("lookup: %w", err)
	}
	conn, err := pn.ListenUDP("udp4", nil)
	if err != nil {
		return err
	}
	defer conn.Close()
	req := stun.MustBuild(stun.TransactionID, stun.BindingRequest)
	buf := make([]byte, 1500)
	for try := 0; try < 3; try++ {
		if _, err := conn.WriteTo(req.Raw, server); err != nil {
			return err
		}
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			continue
		}
		res := &stun.Message{Raw: buf[:n]}
		var mapped stun.XORMappedAddress
		if res.Decode() != nil || mapped.GetFrom(res) != nil {
			return errors.New("not a STUN answer")
		}
		fmt.Printf("     the phone helper's socket is %s, the internet sees it as %s\n",
			conn.LocalAddr(), &net.UDPAddr{IP: mapped.IP, Port: mapped.Port})
		return nil
	}
	return errors.New("no answer from the STUN server")
}

// runProbe checks which public relays work from here: two clients meet on each relay alone.
func runProbe(args []string) int {
	urls := args
	if len(urls) == 0 {
		urls = defaultRelays
	}
	room := randHex(2)
	results := make([]string, len(urls))
	var wg sync.WaitGroup
	good := 0
	var mu sync.Mutex
	for i, u := range urls {
		wg.Add(1)
		go func() {
			defer wg.Done()
			a := newRendezvous(room, "a"+randHex(4), []string{u}, false, dialer{})
			b := newRendezvous(room, "b"+randHex(4), []string{u}, false, dialer{})
			defer a.close()
			defer b.close()
			start := time.Now()
			for time.Since(start) < 12*time.Second {
				ua, _ := a.status()
				ub, _ := b.status()
				if ua == 1 && ub == 1 {
					break
				}
				time.Sleep(50 * time.Millisecond)
			}
			if ua, _ := a.status(); ua != 1 {
				results[i] = "FAIL  could not connect"
				return
			}
			up := time.Since(start)
			sent := time.Now()
			for k := 0; k < 8; k++ {
				a.publish(wireMsg{T: "probe"})
				select {
				case <-b.in:
					results[i] = fmt.Sprintf("ok    connected in %v, message in %v",
						up.Round(time.Millisecond), time.Since(sent).Round(time.Millisecond))
					mu.Lock()
					good++
					mu.Unlock()
					return
				case <-time.After(time.Second):
				}
			}
			results[i] = "FAIL  connected, but no message came through"
		}()
	}
	wg.Wait()
	for i, u := range urls {
		fmt.Printf("%-44s %s\n", u, results[i])
	}
	if good == 0 {
		return 1
	}
	return 0
}
