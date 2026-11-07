package main

import (
	"net"
	"net/url"
	"testing"
)

// A proxy on this PC that does not answer is passed by, one that answers is used, and one
// elsewhere is used as given: PdaNet's own proxy runs only while PdaNet is connected to a phone,
// and a share that outlives it must still reach the relays.
func TestProxyForPassesALocalProxyThatIsGone(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	up, _ := url.Parse("http://" + ln.Addr().String())
	d := dialer{proxy: up}
	if p, _ := d.proxyFor(nil); p != up {
		t.Fatalf("proxy %v while it answers, want %v", p, up)
	}
	_ = ln.Close()
	if p, _ := d.proxyFor(nil); p != nil {
		t.Fatalf("proxy %v after it stopped, want a direct connection", p)
	}
	far, _ := url.Parse("http://192.0.2.1:8080") // a documentation address, never dialed
	if p, _ := (dialer{proxy: far}).proxyFor(nil); p != far {
		t.Fatalf("proxy %v elsewhere, want it used as given", p)
	}
}
