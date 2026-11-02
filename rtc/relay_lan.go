package main

import (
	"context"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
)

// lanPort is where PdaNet listens for the other PC on the local network. It lets two PCs on
// the same network pair without any internet, and it is usually the fastest way to meet.
const lanPort = 47823

// lanRelay broadcasts on every local network and answers whoever spoke to it.
type lanRelay struct {
	rv    *rendezvous
	conn  net.PacketConn
	bcast []*net.UDPAddr

	mu    sync.Mutex
	peers []*net.UDPAddr
	isUp  atomic.Bool
}

func newLANRelay(rv *rendezvous) relay {
	// A second PdaNet on this PC shares the port, both get each broadcast.
	lc := net.ListenConfig{Control: reuseAddr}
	conn, err := lc.ListenPacket(context.Background(), "udp4", net.JoinHostPort("", strconv.Itoa(lanPort)))
	if err != nil {
		// Taken without sharing: still broadcast, the answers come back to this port.
		if conn, err = net.ListenPacket("udp4", ":0"); err != nil {
			return nil
		}
	}
	r := &lanRelay{rv: rv, conn: conn, bcast: broadcastAddrs()}
	r.isUp.Store(true)
	go r.read()
	return r
}

func (r *lanRelay) name() string { return "local network" }
func (r *lanRelay) up() bool     { return r.isUp.Load() }

func (r *lanRelay) close() {
	r.isUp.Store(false)
	_ = r.conn.Close()
}

func (r *lanRelay) read() {
	buf := make([]byte, 64<<10)
	for {
		n, from, err := r.conn.ReadFrom(buf)
		if err != nil {
			r.isUp.Store(false)
			return
		}
		if ua, ok := from.(*net.UDPAddr); ok {
			r.remember(ua)
		}
		r.rv.deliver(append([]byte(nil), buf[:n]...))
	}
}

// remember keeps who spoke to us, so answers reach a PC that listens on another port.
func (r *lanRelay) remember(a *net.UDPAddr) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, p := range r.peers {
		if p.IP.Equal(a.IP) && p.Port == a.Port {
			return
		}
	}
	if len(r.peers) >= 16 {
		r.peers = r.peers[1:]
	}
	r.peers = append(r.peers, a)
}

func (r *lanRelay) publish(msg []byte) {
	if len(msg) > 60000 {
		return
	}
	for _, a := range r.bcast {
		_, _ = r.conn.WriteTo(msg, a)
	}
	r.mu.Lock()
	peers := append([]*net.UDPAddr(nil), r.peers...)
	r.mu.Unlock()
	for _, a := range peers {
		_, _ = r.conn.WriteTo(msg, a)
	}
}

// broadcastAddrs lists the broadcast address of every IPv4 network this PC is on.
func broadcastAddrs() []*net.UDPAddr {
	out := []*net.UDPAddr{{IP: net.IPv4bcast, Port: lanPort}}
	ifaces, err := net.Interfaces()
	if err != nil {
		return out
	}
	for _, ifc := range ifaces {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, _ := ifc.Addrs()
		for _, a := range addrs {
			ipn, ok := a.(*net.IPNet)
			if !ok || ipn.IP.To4() == nil || ipn.IP.IsLinkLocalUnicast() {
				continue
			}
			ip, mask := ipn.IP.To4(), ipn.Mask
			if len(mask) == 16 {
				mask = mask[12:]
			}
			b := make(net.IP, 4)
			for i := range b {
				b[i] = ip[i] | ^mask[i]
			}
			out = append(out, &net.UDPAddr{IP: b, Port: lanPort})
		}
	}
	return out
}
