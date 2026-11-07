package main

import (
	"encoding/binary"
	"math"
	"strings"
	"sync"
	"time"

	"github.com/klauspost/reedsolomon"
	"github.com/pion/interceptor"
	"github.com/pion/rtp"
)

// Forward error correction for the screen, between two PdaNets.
//
// Through a relay a packet sent again takes 200 to 500 ms, and the PC that views holds every
// frame behind a lost packet until it comes. With 2 % of the packets lost at random the picture
// stalled several times a second, and lost keyframes, the largest frames, froze it for seconds
// (the network lab, 2026-11-07). So while the link loses packets, the PC that shares sends a few
// parity packets after each frame (Reed-Solomon), and the PC that views rebuilds the lost packets
// as soon as enough of the frame is in, with no round trip. What the parity cannot cover is asked
// for again, as before.
//
// Each packet of such a frame carries a header extension: its place in the frame, and how many
// data and parity packets the frame has. So the PC that views knows which sequence numbers hold
// parity even when those are lost, and neither waits for them nor asks for them. Both PCs offer
// the extension in their SDP; with a PdaNet that does not, no parity goes out.
const fecURI = "urn:x-pdanet:fec-01"

const (
	fecExtLen   = 5   // the extension: index in the frame (2 bytes), data packets (2), parity per group (1)
	fecMaxGroup = 192 // data packets per Reed-Solomon group, at most 255 with parity: larger frames interleave groups
	fecMaxFrame = 8192
	fecOn       = 0.003            // parity goes out once this share of packets is lost
	fecOff      = 0.001            // and stops after the loss stayed below this for fecHold
	fecHold     = 10 * time.Second // most loss comes and goes, a little parity costs less than a stall
	fecMinShare = 0.10             // of a frame's data packets, as parity
	fecMaxShare = 0.50
	fecKeep     = 3 * time.Second // frames the PC that views keeps to rebuild packets of, past frameWait
	fecFrames   = 256
)

// fecShareFor is the share of parity for the loss the link shows: enough that a frame loses more
// packets than its parity covers seldom, about one frame in a thousand of 10 packets at 2 % loss.
func fecShareFor(loss float64) float64 {
	return min(max(3*loss+0.05, fecMinShare), fecMaxShare)
}

// fecLayout is how a frame of k data packets with m parity packets per group is laid out: g groups,
// data packet i in group i%g, parity packet r (after all the data) in group r%g.
type fecLayout struct{ k, m, g int }

func newFecLayout(k, m int) fecLayout {
	return fecLayout{k: k, m: m, g: (k + fecMaxGroup - 1) / fecMaxGroup}
}

func (l fecLayout) parity() int { return l.g * l.m }

// size is how many data packets group grp holds.
func (l fecLayout) size(grp int) int {
	n := l.k / l.g
	if grp < l.k%l.g {
		n++
	}
	return n
}

// parityFor is how many parity packets go with each group of a frame of k data packets.
func parityFor(k int, share float64) int {
	if share <= 0 || k <= 0 {
		return 0
	}
	l := newFecLayout(k, 0)
	most := l.size(0)
	return min(max(1, int(math.Ceil(float64(most)*share))), 255-most)
}

func putFecExt(b []byte, index, k, m int) {
	binary.BigEndian.PutUint16(b, uint16(index))
	binary.BigEndian.PutUint16(b[2:], uint16(k))
	b[4] = byte(m)
}

// rsCodecs keeps a Reed-Solomon codec for each shape of group, they take a moment to make.
type rsCodecs struct {
	mu    sync.Mutex
	codec map[[2]int]reedsolomon.Encoder
}

func (c *rsCodecs) get(k, m int) (reedsolomon.Encoder, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if e, ok := c.codec[[2]int{k, m}]; ok {
		return e, nil
	}
	e, err := reedsolomon.New(k, m)
	if err != nil {
		return nil, err
	}
	if c.codec == nil || len(c.codec) >= 128 {
		c.codec = map[[2]int]reedsolomon.Encoder{}
	}
	c.codec[[2]int{k, m}] = e
	return e, nil
}

// fecShard is a data packet as a shard of its group: its length, its payload, zeros to size.
func fecShard(payload []byte, size int) []byte {
	s := make([]byte, size)
	binary.BigEndian.PutUint16(s, uint16(len(payload)))
	copy(s[2:], payload)
	return s
}

// ============================ The PC that shares ============================

// fecFactory adds parity to the screen's packets, the outermost interceptor: it numbers the
// packets itself, so the parity fits in between, and everything after it, the packets kept to
// send again, the reports and the pacer, sees the packets as they go out.
type fecFactory struct {
	share   func() float64    // the share of parity to send now, 0 for none
	started func(*fecSender) // tells the peer the sender, to read what parity costs
}

func (f *fecFactory) NewInterceptor(string) (interceptor.Interceptor, error) {
	return &fecInterceptor{factory: f}, nil
}

type fecInterceptor struct {
	interceptor.NoOp
	factory *fecFactory
}

// extensionID is the ID the SDP gave the header extension uri, 0 for none.
func extensionID(info *interceptor.StreamInfo, uri string) uint8 {
	for _, e := range info.RTPHeaderExtensions {
		if e.URI == uri && e.ID > 0 && e.ID < 15 {
			return uint8(e.ID)
		}
	}
	return 0
}

func (f *fecInterceptor) BindLocalStream(info *interceptor.StreamInfo, w interceptor.RTPWriter) interceptor.RTPWriter {
	id := extensionID(info, fecURI)
	if id == 0 || !strings.HasPrefix(strings.ToLower(info.MimeType), "video/") {
		return w
	}
	s := &fecSender{w: w, ext: id, share: f.factory.share}
	if f.factory.started != nil {
		f.factory.started(s)
	}
	return interceptor.RTPWriterFunc(s.write)
}

// fecSender holds each frame's packets until its marker, numbers them, and sends the frame's
// parity after them. The packetizer writes a whole frame at once, so holding costs no time.
type fecSender struct {
	mu      sync.Mutex
	w       interceptor.RTPWriter
	ext     uint8
	share   func() float64
	started bool
	seq     uint16
	frame   []fecOut
	plain   bool   // the frame of plainTS outgrew fecMaxFrame: the rest goes without parity
	plainTS uint32
	codecs  rsCodecs

	statMu       sync.Mutex
	data, parity int // payload bytes sent since overhead last looked
}

type fecOut struct {
	header  rtp.Header
	payload []byte
	attrs   interceptor.Attributes
}

func (s *fecSender) write(h *rtp.Header, payload []byte, attrs interceptor.Attributes) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.started {
		s.started, s.seq = true, h.SequenceNumber
	}
	var err error
	if len(s.frame) > 0 && h.Timestamp != s.frame[0].header.Timestamp {
		err = s.flush(true) // a frame without its marker goes as it is
	}
	if s.plain && h.Timestamp != s.plainTS {
		s.plain = false
	}
	s.frame = append(s.frame, fecOut{header: h.Clone(), payload: append([]byte(nil), payload...), attrs: attrs})
	switch {
	case s.plain:
		// The rest of a frame too large to protect: parity in its middle would be holes in it.
		err = s.flush(false)
	case len(s.frame) >= fecMaxFrame && !h.Marker:
		s.plain, s.plainTS = true, h.Timestamp
		err = s.flush(false)
	case h.Marker:
		if e := s.flush(true); err == nil {
			err = e
		}
	}
	if h.Marker {
		s.plain = false
	}
	return h.MarshalSize() + len(payload), err
}

// flush sends the frame held, numbered, then its parity unless it goes without. Callers hold mu.
func (s *fecSender) flush(protect bool) error {
	frame := s.frame
	s.frame = s.frame[:0]
	k := len(frame)
	if k == 0 {
		return nil
	}
	m := 0
	if protect {
		m = parityFor(k, s.share())
	}
	var ext [fecExtLen]byte
	var first error
	data := 0
	for i := range frame {
		p := &frame[i]
		p.header.SequenceNumber = s.seq
		s.seq++
		if m > 0 {
			putFecExt(ext[:], i, k, m)
			if err := p.header.SetExtension(s.ext, append([]byte(nil), ext[:]...)); err != nil && first == nil {
				first = err
			}
		}
		if _, err := s.w.Write(&p.header, p.payload, p.attrs); err != nil && first == nil {
			first = err
		}
		data += len(p.payload)
	}
	parity := 0
	if m > 0 {
		shards, err := s.parityOf(frame, newFecLayout(k, m))
		if err != nil && first == nil {
			first = err
		}
		last := frame[k-1].header
		for r, shard := range shards {
			h := last.Clone()
			h.Marker = false
			h.SequenceNumber = s.seq
			s.seq++
			putFecExt(ext[:], k+r, k, m)
			if err := h.SetExtension(s.ext, append([]byte(nil), ext[:]...)); err != nil && first == nil {
				first = err
			}
			if _, err := s.w.Write(&h, shard, interceptor.Attributes{}); err != nil && first == nil {
				first = err
			}
			parity += len(shard)
		}
	}
	s.statMu.Lock()
	s.data += data
	s.parity += parity
	s.statMu.Unlock()
	return first
}

// parityOf works out the parity packets of a frame, in the order they go out.
func (s *fecSender) parityOf(frame []fecOut, l fecLayout) ([][]byte, error) {
	out := make([][]byte, l.parity())
	for grp := 0; grp < l.g; grp++ {
		kg := l.size(grp)
		size := 0
		for j := 0; j < kg; j++ {
			size = max(size, len(frame[grp+j*l.g].payload)+2)
		}
		shards := make([][]byte, kg+l.m)
		for j := 0; j < kg; j++ {
			shards[j] = fecShard(frame[grp+j*l.g].payload, size)
		}
		for q := 0; q < l.m; q++ {
			shards[kg+q] = make([]byte, size)
		}
		enc, err := s.codecs.get(kg, l.m)
		if err != nil {
			return nil, err
		}
		if err := enc.Encode(shards); err != nil {
			return nil, err
		}
		for q := 0; q < l.m; q++ {
			out[grp+q*l.g] = shards[kg+q]
		}
	}
	return out, nil
}

// overhead is the parity sent per byte of the screen since the last call, -1 when nothing went.
func (s *fecSender) overhead() float64 {
	s.statMu.Lock()
	defer s.statMu.Unlock()
	data, parity := s.data, s.parity
	s.data, s.parity = 0, 0
	if data == 0 {
		return -1
	}
	return float64(parity) / float64(data)
}

// ============================ The PC that views ============================

// fecReceiver sits in front of the frame assembler: it hands on the screen's packets, keeps the
// parity, and rebuilds lost packets as soon as a group has enough of its packets in.
type fecReceiver struct {
	ext       uint8
	frames    map[uint16]*fecFrame // by the sequence number of the frame's first packet
	order     []uint16
	codecs    rsCodecs
	notMedia  func(from uint16, n int) // these hold parity: never wait for them, never ask for them
	recovered func(seq uint16)         // this one was rebuilt
	rebuilt   int
}

type fecFrame struct {
	l      fecLayout
	base   uint16
	ts     uint32
	pt     uint8
	ssrc   uint32
	data   [][]byte // by index, nil while missing
	parity [][]byte // by parity index
	done   []bool   // by group: nothing missing, or rebuilt
	at     time.Time
}

func newFecReceiver(ext uint8, notMedia func(uint16, int), recovered func(uint16)) *fecReceiver {
	return &fecReceiver{ext: ext, frames: map[uint16]*fecFrame{}, notMedia: notMedia, recovered: recovered}
}

// push takes a packet off the link and returns the packets for the assembler: this one unless
// it holds parity or came twice, and any it let rebuild.
func (r *fecReceiver) push(p *rtp.Packet, now time.Time) []*rtp.Packet {
	if r.ext == 0 {
		return []*rtp.Packet{p}
	}
	b := p.Header.GetExtension(r.ext)
	if len(b) != fecExtLen {
		return []*rtp.Packet{p}
	}
	index, k, m := int(binary.BigEndian.Uint16(b)), int(binary.BigEndian.Uint16(b[2:])), int(b[4])
	if k == 0 || m == 0 || k > fecMaxFrame {
		return []*rtp.Packet{p}
	}
	l := newFecLayout(k, m)
	if index >= k+l.parity() {
		return nil // not a packet of this frame: never media
	}
	base := p.SequenceNumber - uint16(index)
	f := r.frames[base]
	if f == nil || f.l != l || f.ts != p.Timestamp {
		f = &fecFrame{l: l, base: base, ts: p.Timestamp, pt: p.PayloadType, ssrc: p.SSRC,
			data: make([][]byte, k), parity: make([][]byte, l.parity()), done: make([]bool, l.g), at: now}
		r.add(f, now)
		if r.notMedia != nil {
			r.notMedia(base+uint16(k), l.parity())
		}
	}
	var out []*rtp.Packet
	grp := index % l.g
	if index < k {
		if f.data[index] != nil {
			return nil // sent again after it was rebuilt or came
		}
		f.data[index] = append([]byte(nil), p.Payload...)
		out = append(out, p)
	} else {
		ri := index - k
		if f.parity[ri] == nil {
			f.parity[ri] = append([]byte(nil), p.Payload...)
		}
		grp = ri % l.g
	}
	return append(out, r.rebuild(f, grp)...)
}

// add keeps a new frame, and lets go of those past fecKeep or beyond the newest fecFrames.
func (r *fecReceiver) add(f *fecFrame, now time.Time) {
	if r.frames[f.base] == nil {
		r.order = append(r.order, f.base)
	}
	r.frames[f.base] = f
	for len(r.order) > 1 {
		if first := r.frames[r.order[0]]; first != nil && len(r.order) <= fecFrames && now.Sub(first.at) <= fecKeep {
			break
		}
		delete(r.frames, r.order[0])
		r.order = r.order[1:]
	}
}

// rebuild recovers the missing data packets of group grp once enough of its packets came.
func (r *fecReceiver) rebuild(f *fecFrame, grp int) []*rtp.Packet {
	if f.done[grp] {
		return nil
	}
	l := f.l
	kg := l.size(grp)
	have, size := 0, 0
	for j := 0; j < kg; j++ {
		if f.data[grp+j*l.g] != nil {
			have++
		}
	}
	if have == kg {
		f.done[grp] = true
		return nil
	}
	for q := 0; q < l.m; q++ {
		if s := f.parity[grp+q*l.g]; s != nil {
			have++
			size = len(s)
		}
	}
	if have < kg || size < 2 {
		return nil
	}
	shards := make([][]byte, kg+l.m)
	for j := 0; j < kg; j++ {
		if d := f.data[grp+j*l.g]; d != nil {
			if len(d)+2 > size {
				f.done[grp] = true // does not fit its own group: not parity of ours
				return nil
			}
			shards[j] = fecShard(d, size)
		}
	}
	for q := 0; q < l.m; q++ {
		if s := f.parity[grp+q*l.g]; len(s) == size {
			shards[kg+q] = s
		}
	}
	f.done[grp] = true
	dec, err := r.codecs.get(kg, l.m)
	if err != nil || dec.ReconstructData(shards) != nil {
		return nil
	}
	var out []*rtp.Packet
	for j := 0; j < kg; j++ {
		index := grp + j*l.g
		if f.data[index] != nil {
			continue
		}
		n := int(binary.BigEndian.Uint16(shards[j]))
		if n+2 > size {
			continue
		}
		payload := shards[j][2 : 2+n]
		f.data[index] = payload
		seq := f.base + uint16(index)
		out = append(out, &rtp.Packet{
			Header:  rtp.Header{Version: 2, PayloadType: f.pt, SSRC: f.ssrc, SequenceNumber: seq, Timestamp: f.ts, Marker: index == l.k-1},
			Payload: payload,
		})
		r.rebuilt++
		if r.recovered != nil {
			r.recovered(seq)
		}
	}
	return out
}
