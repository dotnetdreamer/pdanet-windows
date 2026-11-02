package main

import "sync"

// memRelay passes messages between sessions in this process, for the self test.
type memRelay struct {
	rv  *rendezvous
	hub string
}

var memHubs = struct {
	sync.Mutex
	m map[string][]*memRelay
}{m: map[string][]*memRelay{}}

func newMemRelay(rv *rendezvous, hub string) relay {
	r := &memRelay{rv: rv, hub: hub}
	memHubs.Lock()
	memHubs.m[hub] = append(memHubs.m[hub], r)
	memHubs.Unlock()
	return r
}

func (r *memRelay) name() string { return "memory " + r.hub }
func (r *memRelay) up() bool     { return true }

func (r *memRelay) publish(msg []byte) {
	memHubs.Lock()
	others := append([]*memRelay(nil), memHubs.m[r.hub]...)
	memHubs.Unlock()
	for _, o := range others {
		if o != r {
			go o.rv.deliver(append([]byte(nil), msg...))
		}
	}
}

func (r *memRelay) close() {
	memHubs.Lock()
	defer memHubs.Unlock()
	list := memHubs.m[r.hub]
	for i, o := range list {
		if o == r {
			memHubs.m[r.hub] = append(list[:i], list[i+1:]...)
			break
		}
	}
}
