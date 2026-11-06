package main

import (
	"bufio"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/pion/stun/v4"
	"github.com/pion/transport/v5"
	"github.com/pion/transport/v5/deadline"
	"github.com/pion/transport/v5/stdnet"
)

// In Phone VPN mode the PC reaches the phone only through adb, which carries TCP. To send the
// screen through the phone anyway, the phone helper (agent/ProxyMain.java) relays UDP: every
// socket WebRTC opens here becomes a socket on the phone, behind its own TCP connection to the
// phone helper, so the screen leaves through the phone's network and its VPN.
//
// The connection starts with "PDANET-UDP /1 HTTP/1.1", the phone helper answers 200 with an
// X-Local header that holds the phone's own address and port. After that each datagram is
// one frame:
//
//	u16 big endian length of what follows | u8 family | address | u16 big endian port | payload
//
// Family 4 has a 4 byte IPv4 address, 6 a 16 byte IPv6 address. Family 0 frames look up a
// name on the phone: "R <id> <name>" goes out, "A <id> <ip>..." or "E <id> <why>" comes back.

var errNoInterface = errors.New("phone: no such interface")

// phoneNet is the phone's network as WebRTC sees it: one interface with the phone's address,
// sockets that live on the phone, and names looked up by the phone.
type phoneNet struct {
	*stdnet.Net
	agent string
	iface *transport.Interface

	mu  sync.Mutex
	dns map[string][]net.IP

	has6 bool // the phone has a public IPv6 address of its own, see findIPv6
}

func newPhoneNet(agent string) (*phoneNet, error) {
	std, err := stdnet.NewNet()
	if err != nil {
		return nil, err
	}
	probe, err := dialTunnel(agent)
	if err != nil {
		return nil, err
	}
	local := probe.local.IP
	_ = probe.Close()

	// The phone's own address only names the host candidate, which the other PC rarely
	// reaches. What carries the screen is the public address STUN finds through the phone.
	if local == nil || local.IsUnspecified() || local.To4() == nil {
		local = net.IPv4(10, 0, 0, 2)
	}
	ifc := transport.NewInterface(net.Interface{
		Index: 1, MTU: 1400, Name: "phone", Flags: net.FlagUp | net.FlagRunning | net.FlagBroadcast,
	})
	ifc.AddAddress(&net.IPNet{IP: local.To4(), Mask: net.CIDRMask(32, 32)})
	n := &phoneNet{Net: std, agent: agent, iface: ifc, dns: map[string][]net.IP{}}
	if ip := n.findIPv6(); ip != nil {
		ifc.AddAddress(&net.IPNet{IP: ip, Mask: net.CIDRMask(128, 128)})
		n.has6 = true
		log.Printf("phone: the mobile network gives the phone IPv6, the other PC may be reached straight over it")
	}
	return n, nil
}

// findIPv6 asks a STUN server over IPv6, through the phone, which address it sees. Mobile
// networks put IPv4 behind the carrier's NAT, where two PCs rarely reach each other without a
// relay, but many give each phone a public IPv6 address with no NAT in front of it. The phone
// helper's sockets take both kinds, so the phone's IPv6 address becomes one more address of
// this network, and a PC on the other side with IPv6 can link up with it straight. The phone
// only looks up IPv6 addresses for names when it has IPv6 itself, so on a network without it
// the answer comes at once. The address is returned only when it is a public one.
func (n *phoneNet) findIPv6() net.IP {
	for _, server := range []string{"stun.l.google.com:19302", "stun.cloudflare.com:3478"} {
		host, portText, _ := net.SplitHostPort(server)
		port, _ := strconv.Atoi(portText)
		ips, err := n.lookup(host)
		if err != nil {
			continue
		}
		var to net.IP
		for _, ip := range ips {
			if ip.To4() == nil && ip.IsGlobalUnicast() {
				to = ip
				break
			}
		}
		if to == nil {
			return nil // the phone looked up no IPv6 address: it has no IPv6
		}
		if ip := n.stunIPv6(&net.UDPAddr{IP: to, Port: port}); ip != nil {
			return ip
		}
	}
	return nil
}

// stunIPv6 sends a STUN binding request through the phone, three times within 1.2 seconds, and
// reads the address the server saw.
func (n *phoneNet) stunIPv6(server *net.UDPAddr) net.IP {
	t, err := dialTunnel(n.agent)
	if err != nil {
		return nil
	}
	defer t.Close()
	msg, err := stun.Build(stun.TransactionID, stun.BindingRequest)
	if err != nil {
		return nil
	}
	got := make(chan net.IP, 1)
	go func() {
		buf := make([]byte, 1500)
		for {
			k, _, err := t.ReadFrom(buf)
			if err != nil {
				return
			}
			reply := &stun.Message{Raw: append([]byte(nil), buf[:k]...)}
			if reply.Decode() != nil || reply.TransactionID != msg.TransactionID {
				continue
			}
			var xor stun.XORMappedAddress
			if xor.GetFrom(reply) == nil {
				got <- xor.IP
				return
			}
		}
	}()
	deadline := time.After(1200 * time.Millisecond)
	for try := 0; ; try++ {
		if try < 3 {
			if _, err := t.WriteTo(msg.Raw, server); err != nil {
				return nil
			}
		}
		select {
		case ip := <-got:
			if ip.To4() == nil && ip.IsGlobalUnicast() && !ip.IsPrivate() {
				return ip
			}
			return nil
		case <-deadline:
			return nil
		case <-time.After(400 * time.Millisecond):
		}
	}
}

func (n *phoneNet) Interfaces() ([]*transport.Interface, error) {
	return []*transport.Interface{n.iface}, nil
}

func (n *phoneNet) InterfaceByIndex(index int) (*transport.Interface, error) {
	if index == n.iface.Index {
		return n.iface, nil
	}
	return nil, errNoInterface
}

func (n *phoneNet) InterfaceByName(name string) (*transport.Interface, error) {
	if name == n.iface.Name {
		return n.iface, nil
	}
	return nil, errNoInterface
}

func (n *phoneNet) ListenUDP(string, *net.UDPAddr) (transport.UDPConn, error) {
	return dialTunnel(n.agent)
}

func (n *phoneNet) ListenPacket(network, address string) (net.PacketConn, error) {
	if strings.HasPrefix(network, "udp") {
		return dialTunnel(n.agent)
	}
	return n.Net.ListenPacket(network, address)
}

func (n *phoneNet) ResolveUDPAddr(network, address string) (*net.UDPAddr, error) {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	p, err := strconv.Atoi(port)
	if err != nil {
		return nil, err
	}
	ip, err := n.pick(network, host)
	if err != nil {
		return nil, err
	}
	return &net.UDPAddr{IP: ip, Port: p}, nil
}

func (n *phoneNet) ResolveIPAddr(network, address string) (*net.IPAddr, error) {
	ip, err := n.pick(network, address)
	if err != nil {
		return nil, err
	}
	return &net.IPAddr{IP: ip}, nil
}

// pick looks up host on the phone and returns an address of the family network asks for.
func (n *phoneNet) pick(network, host string) (net.IP, error) {
	ips, err := n.lookup(host)
	if err != nil {
		return nil, err
	}
	want6 := strings.HasSuffix(network, "6")
	want4 := strings.HasSuffix(network, "4")
	for _, ip := range ips {
		if is4 := ip.To4() != nil; (want4 && is4) || (want6 && !is4) || (!want4 && !want6) {
			return ip, nil
		}
	}
	return nil, fmt.Errorf("phone: no %s address for %s", network, host)
}

func (n *phoneNet) lookup(host string) ([]net.IP, error) {
	if ip := net.ParseIP(host); ip != nil {
		return []net.IP{ip}, nil
	}
	n.mu.Lock()
	ips, ok := n.dns[host]
	n.mu.Unlock()
	if ok {
		return ips, nil
	}

	t, err := dialTunnel(n.agent)
	if err != nil {
		return nil, err
	}
	defer t.Close()
	if err := t.ask("R 1 " + host); err != nil {
		return nil, err
	}
	var reply string
	select {
	case reply = <-t.dns:
	case <-time.After(5 * time.Second):
		return nil, fmt.Errorf("phone: looking up %s took too long", host)
	}
	f := strings.Fields(reply)
	if len(f) < 3 || f[0] != "A" {
		return nil, fmt.Errorf("phone: could not look up %s: %s", host, reply)
	}
	for _, s := range f[2:] {
		if ip := net.ParseIP(s); ip != nil {
			ips = append(ips, ip)
		}
	}
	if len(ips) == 0 {
		return nil, fmt.Errorf("phone: no address for %s", host)
	}
	n.mu.Lock()
	n.dns[host] = ips
	n.mu.Unlock()
	return ips, nil
}

// tunnelQueue is how many datagrams from the phone may wait for WebRTC to read them. The phone
// helper's link sometimes stalls for a second and then hands over everything at once; at a few
// megabits that is several hundred datagrams, and each one dropped here costs a resend.
const tunnelQueue = 4096

// tunnelConn is one UDP socket on the phone.
type tunnelConn struct {
	tcp    net.Conn
	local  *net.UDPAddr
	in     chan datagram
	dns    chan string
	wmu    sync.Mutex
	rdl    *deadline.Deadline
	closed chan struct{}
	once   sync.Once
}

type datagram struct {
	data []byte
	from *net.UDPAddr
}

type timeoutError struct{}

func (timeoutError) Error() string   { return "i/o timeout" }
func (timeoutError) Timeout() bool   { return true }
func (timeoutError) Temporary() bool { return true }

func dialTunnel(agent string) (*tunnelConn, error) {
	c, err := net.DialTimeout("tcp", agent, 5*time.Second)
	if err != nil {
		return nil, fmt.Errorf("phone helper: %w", err)
	}
	if tc, ok := c.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
	_ = c.SetDeadline(time.Now().Add(6 * time.Second))
	if _, err := io.WriteString(c, "PDANET-UDP /1 HTTP/1.1\r\nHost: pdanet\r\n\r\n"); err != nil {
		_ = c.Close()
		return nil, err
	}
	br := bufio.NewReaderSize(c, 64<<10)
	head, err := readHead(br)
	if err != nil || !strings.HasPrefix(head, "HTTP/1.1 200") {
		_ = c.Close()
		return nil, errors.New("the phone helper does not relay the screen, disconnect PdaNet and connect again")
	}
	local := &net.UDPAddr{}
	for _, line := range strings.Split(head, "\r\n") {
		if v, ok := strings.CutPrefix(line, "X-Local: "); ok {
			if a, err := net.ResolveUDPAddr("udp", strings.TrimSpace(v)); err == nil {
				local = a
			}
		}
	}
	_ = c.SetDeadline(time.Time{})

	t := &tunnelConn{
		tcp:    c,
		local:  local,
		in:     make(chan datagram, tunnelQueue),
		dns:    make(chan string, 2),
		rdl:    deadline.New(),
		closed: make(chan struct{}),
	}
	go t.readLoop(br)
	return t, nil
}

func readHead(br *bufio.Reader) (string, error) {
	var b strings.Builder
	for b.Len() < 4096 {
		line, err := br.ReadString('\n')
		if err != nil {
			return "", err
		}
		b.WriteString(line)
		if line == "\r\n" {
			return b.String(), nil
		}
	}
	return "", errors.New("head too long")
}

func (t *tunnelConn) readLoop(br *bufio.Reader) {
	defer t.Close()
	var size [2]byte
	for {
		if _, err := io.ReadFull(br, size[:]); err != nil {
			return
		}
		frame := make([]byte, binary.BigEndian.Uint16(size[:]))
		if _, err := io.ReadFull(br, frame); err != nil || len(frame) < 1 {
			return
		}
		switch frame[0] {
		case 0:
			select {
			case t.dns <- string(frame[1:]):
			default:
			}
		case 4, 6:
			alen := 4
			if frame[0] == 6 {
				alen = 16
			}
			if len(frame) < 1+alen+2 {
				continue
			}
			from := &net.UDPAddr{
				IP:   net.IP(frame[1 : 1+alen]),
				Port: int(binary.BigEndian.Uint16(frame[1+alen:])),
			}
			select {
			case t.in <- datagram{data: frame[1+alen+2:], from: from}:
			default: // full, drop it like a busy socket would
			}
		}
	}
}

func (t *tunnelConn) ask(q string) error {
	frame := make([]byte, 3+len(q))
	binary.BigEndian.PutUint16(frame, uint16(1+len(q)))
	copy(frame[3:], q)
	t.wmu.Lock()
	defer t.wmu.Unlock()
	_, err := t.tcp.Write(frame)
	return err
}

func (t *tunnelConn) ReadFrom(b []byte) (int, net.Addr, error) {
	select {
	case d := <-t.in:
		return copy(b, d.data), d.from, nil
	case <-t.rdl.Done():
		return 0, nil, timeoutError{}
	case <-t.closed:
		return 0, nil, net.ErrClosed
	}
}

func (t *tunnelConn) ReadFromUDP(b []byte) (int, *net.UDPAddr, error) {
	n, a, err := t.ReadFrom(b)
	ua, _ := a.(*net.UDPAddr)
	return n, ua, err
}

func (t *tunnelConn) Read(b []byte) (int, error) {
	n, _, err := t.ReadFrom(b)
	return n, err
}

func (t *tunnelConn) ReadMsgUDP(b, _ []byte) (n, oobn, flags int, addr *net.UDPAddr, err error) {
	n, addr, err = t.ReadFromUDP(b)
	return n, 0, 0, addr, err
}

func (t *tunnelConn) WriteTo(b []byte, addr net.Addr) (int, error) {
	ua, ok := addr.(*net.UDPAddr)
	if !ok {
		var err error
		if ua, err = net.ResolveUDPAddr("udp", addr.String()); err != nil {
			return 0, err
		}
	}
	fam, ip := byte(6), ua.IP.To16()
	if ip4 := ua.IP.To4(); ip4 != nil {
		fam, ip = 4, ip4
	}
	if ip == nil {
		return 0, errors.New("phone: bad address")
	}
	n := 1 + len(ip) + 2 + len(b)
	if n > 0xffff {
		return 0, errors.New("phone: datagram too big")
	}
	frame := make([]byte, 2+n)
	binary.BigEndian.PutUint16(frame, uint16(n))
	frame[2] = fam
	copy(frame[3:], ip)
	binary.BigEndian.PutUint16(frame[3+len(ip):], uint16(ua.Port))
	copy(frame[5+len(ip):], b)

	t.wmu.Lock()
	_, err := t.tcp.Write(frame)
	t.wmu.Unlock()
	if err != nil {
		return 0, err
	}
	return len(b), nil
}

func (t *tunnelConn) WriteToUDP(b []byte, addr *net.UDPAddr) (int, error) { return t.WriteTo(b, addr) }

func (t *tunnelConn) WriteMsgUDP(b, _ []byte, addr *net.UDPAddr) (n, oobn int, err error) {
	n, err = t.WriteTo(b, addr)
	return n, 0, err
}

func (t *tunnelConn) Write([]byte) (int, error) { return 0, errors.New("phone: socket not connected") }

func (t *tunnelConn) LocalAddr() net.Addr  { return t.local }
func (t *tunnelConn) RemoteAddr() net.Addr { return nil }

func (t *tunnelConn) SetDeadline(tm time.Time) error     { t.rdl.Set(tm); return nil }
func (t *tunnelConn) SetReadDeadline(tm time.Time) error { t.rdl.Set(tm); return nil }
func (t *tunnelConn) SetWriteDeadline(time.Time) error   { return nil }
func (t *tunnelConn) SetReadBuffer(int) error            { return nil }
func (t *tunnelConn) SetWriteBuffer(int) error           { return nil }

func (t *tunnelConn) Close() error {
	t.once.Do(func() {
		close(t.closed)
		_ = t.tcp.Close()
	})
	return nil
}
