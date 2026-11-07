package main

import (
	"math/rand/v2"
	"slices"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtcp"
)

// nackGen asks the PC that shares for lost packets again, on the PC that views.
//
// Pion's own generator asks for every missing packet every 100 ms until it comes. Through the
// phone and a relay the round trip is 200 to 700 ms (2026-11-06), so it asked again and again
// before the first resend could arrive, and the PC that shares sent many packets two or three
// times. SRTP drops the extra copies as replays before the transport wide feedback sees them, so
// they count as lost: the feedback said 10 to 65 % while the network lost 2 %, and that loss held
// GCC at its floor. This one asks at once, then again only after one and a half round trips, at
// most nackTries times, and stops once the viewer no longer waits for the frame (frameWait).
type nackGenFactory struct {
	rtt func() time.Duration // the round trip now, 0 while unknown
	gen atomic.Pointer[nackGen]
}

type nackGen struct {
	interceptor.NoOp
	rtt     func() time.Duration
	mu      sync.Mutex
	streams map[uint32]*nackStream
	writer  interceptor.RTCPWriter
	started bool
	close   chan struct{}
	once    sync.Once
	wg      sync.WaitGroup
}

type nackStream struct {
	ssrc    uint32
	started bool
	newest  uint16
	missing map[uint16]*nackMiss
	parity  [4096]int32 // sequence number + 1 of the slots that hold parity, never asked for
}

type nackMiss struct {
	since time.Time // when the gap showed
	next  time.Time // when to ask, again
	tries int
}

const (
	nackTick     = 10 * time.Millisecond
	nackGrace    = 5 * time.Millisecond // a packet only a little out of order is not lost
	nackTries    = 6
	nackMinWait  = 120 * time.Millisecond // between two asks for one packet
	nackRTTGuess = 300 * time.Millisecond // while the round trip is unknown
	nackMaxGap   = 1000                   // a jump this far is a new start, not a loss
)

func (f *nackGenFactory) NewInterceptor(string) (interceptor.Interceptor, error) {
	g := &nackGen{rtt: f.rtt, streams: map[uint32]*nackStream{}, close: make(chan struct{})}
	f.gen.Store(g)
	return g, nil
}

// notMedia tells n sequence numbers from from on of stream ssrc that hold parity, see
// fecReceiver: their loss loses nothing of the screen, so they are never asked for.
func (g *nackGen) notMedia(ssrc uint32, from uint16, n int) {
	g.mu.Lock()
	defer g.mu.Unlock()
	st := g.streams[ssrc]
	if st == nil {
		return
	}
	for i := 0; i < n && i < len(st.parity); i++ {
		s := from + uint16(i)
		st.parity[int(s)%len(st.parity)] = int32(s) + 1
		delete(st.missing, s)
	}
}

// recovered tells a packet of stream ssrc that parity rebuilt: no need to ask for it again.
func (g *nackGen) recovered(ssrc uint32, seq uint16) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if st := g.streams[ssrc]; st != nil {
		delete(st.missing, seq)
	}
}

// supportsNack tells a stream whose packets may be asked for again.
func supportsNack(info *interceptor.StreamInfo) bool {
	for _, fb := range info.RTCPFeedback {
		if fb.Type == "nack" && fb.Parameter == "" {
			return true
		}
	}
	return false
}

func (g *nackGen) BindRTCPWriter(w interceptor.RTCPWriter) interceptor.RTCPWriter {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.writer = w
	if !g.started {
		g.started = true
		g.wg.Add(1)
		go g.loop()
	}
	return w
}

func (g *nackGen) BindRemoteStream(info *interceptor.StreamInfo, r interceptor.RTPReader) interceptor.RTPReader {
	if !supportsNack(info) {
		return r
	}
	st := &nackStream{ssrc: info.SSRC, missing: map[uint16]*nackMiss{}}
	g.mu.Lock()
	g.streams[info.SSRC] = st
	g.mu.Unlock()
	return interceptor.RTPReaderFunc(func(b []byte, a interceptor.Attributes) (int, interceptor.Attributes, error) {
		n, attr, err := r.Read(b, a)
		if err != nil {
			return 0, nil, err
		}
		if attr == nil {
			attr = make(interceptor.Attributes)
		}
		h, err := attr.GetRTPHeader(b[:n])
		if err != nil {
			return 0, nil, err
		}
		g.arrived(st, h.SequenceNumber, time.Now())
		return n, attr, nil
	})
}

func (g *nackGen) UnbindRemoteStream(info *interceptor.StreamInfo) {
	g.mu.Lock()
	delete(g.streams, info.SSRC)
	g.mu.Unlock()
}

func (g *nackGen) Close() error {
	g.mu.Lock()
	g.started = true // no loop starts after this
	g.mu.Unlock()
	g.once.Do(func() { close(g.close) })
	g.wg.Wait()
	return nil
}

// arrived notes a packet: the ones it skipped are missing, and it is not missing anymore.
func (g *nackGen) arrived(st *nackStream, seq uint16, now time.Time) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if !st.started {
		st.started, st.newest = true, seq
		return
	}
	switch d := int16(seq - st.newest); {
	case d > 0 && d <= nackMaxGap:
		for s := st.newest + 1; s != seq; s++ {
			if st.parity[int(s)%len(st.parity)] != int32(s)+1 {
				st.missing[s] = &nackMiss{since: now, next: now.Add(nackGrace)}
			}
		}
		st.newest = seq
	case d > nackMaxGap || d < -nackMaxGap:
		clear(st.missing)
		st.newest = seq
	default:
		delete(st.missing, seq) // late, or sent again
	}
}

func (g *nackGen) loop() {
	defer g.wg.Done()
	t := time.NewTicker(nackTick)
	defer t.Stop()
	sender := rand.Uint32()
	for {
		select {
		case <-g.close:
			return
		case <-t.C:
		}
		pkts := g.due(time.Now(), sender)
		if len(pkts) == 0 {
			continue
		}
		g.mu.Lock()
		w := g.writer
		g.mu.Unlock()
		if w != nil {
			_, _ = w.Write(pkts, interceptor.Attributes{})
		}
	}
}

// due takes the packets to ask for now, one NACK per stream.
func (g *nackGen) due(now time.Time, sender uint32) []rtcp.Packet {
	rtt := g.rtt()
	if rtt <= 0 {
		rtt = nackRTTGuess
	}
	again := max(rtt*3/2, nackMinWait)
	g.mu.Lock()
	defer g.mu.Unlock()
	var out []rtcp.Packet
	for _, st := range g.streams {
		var seqs []uint16
		for s, m := range st.missing {
			if m.tries >= nackTries || now.Sub(m.since) > frameWait {
				delete(st.missing, s)
				continue
			}
			if now.Before(m.next) {
				continue
			}
			m.tries++
			m.next = now.Add(again)
			seqs = append(seqs, s)
		}
		if len(seqs) == 0 {
			continue
		}
		// In order, across the wrap: all of them lie within nackMaxGap of each other.
		slices.SortFunc(seqs, func(a, b uint16) int { return int(int16(a - b)) })
		out = append(out, &rtcp.TransportLayerNack{SenderSSRC: sender, MediaSSRC: st.ssrc, Nacks: rtcp.NackPairsFromSequenceNumbers(seqs)})
	}
	return out
}
