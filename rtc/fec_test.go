package main

import (
	"bytes"
	"encoding/binary"
	"math/rand/v2"
	"slices"
	"testing"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
)

const testFecExt = 9

// sent keeps what the parity writer hands on, as the pacer would get it.
type sent struct{ pkts []*rtp.Packet }

func (s *sent) Write(h *rtp.Header, payload []byte, _ interceptor.Attributes) (int, error) {
	s.pkts = append(s.pkts, &rtp.Packet{Header: h.Clone(), Payload: append([]byte(nil), payload...)})
	return len(payload), nil
}

func fecWriter(share float64, out interceptor.RTPWriter, ext bool) interceptor.RTPWriter {
	info := &interceptor.StreamInfo{MimeType: "video/H264"}
	if ext {
		info.RTPHeaderExtensions = []interceptor.RTPHeaderExtension{{URI: fecURI, ID: testFecExt}}
	}
	f := &fecInterceptor{factory: &fecFactory{share: func() float64 { return share }}}
	return f.BindLocalStream(info, out)
}

// sendFrames writes frames of these sizes as the packetizer does, from sequence number 100 on.
func sendFrames(t *testing.T, w interceptor.RTPWriter, sizes ...int) [][]byte {
	t.Helper()
	var frames [][]byte
	seq := uint16(100)
	for i, size := range sizes {
		pkts, data := parameterFrame(seq, uint32(9000*(i+1)), size)
		for _, p := range pkts {
			if _, err := w.Write(&p.Header, p.Payload, nil); err != nil {
				t.Fatal(err)
			}
		}
		seq += uint16(len(pkts))
		frames = append(frames, data)
	}
	return frames
}

func fecIndex(p *rtp.Packet) (index, k, m int, ok bool) {
	b := p.Header.GetExtension(testFecExt)
	if len(b) != fecExtLen {
		return 0, 0, 0, false
	}
	return int(binary.BigEndian.Uint16(b)), int(binary.BigEndian.Uint16(b[2:])), int(b[4]), true
}

// receiveAll takes the packets as the PC that views does and returns the frames it emitted at
// once, without any time passing for a repair, and what it counted as lost.
func receiveAll(pkts []*rtp.Packet) ([][]byte, int, *fecReceiver) {
	sb := &h264Frames{}
	fr := newFecReceiver(testFecExt, sb.notMedia, nil)
	now := time.Now()
	var frames [][]byte
	dropped := 0
	for _, p := range pkts {
		for _, q := range fr.push(p, now) {
			sb.Push(q, now)
		}
		for s := sb.Pop(now, time.Second); s != nil; s = sb.Pop(now, time.Second) {
			frames = append(frames, s.Data)
			dropped += int(s.PrevDroppedPackets)
		}
	}
	return frames, dropped, fr
}

// The packets go out numbered on from the first, the marker only on each frame's last data
// packet, every packet of a frame with its place in it, the parity after the data.
func TestFecNumbersAndMarksThePackets(t *testing.T) {
	out := &sent{}
	sendFrames(t, fecWriter(0.2, out, true), 3000, 40, 30_000)
	want := uint16(100)
	frame, k := 0, 0
	for _, p := range out.pkts {
		if p.SequenceNumber != want {
			t.Fatalf("sequence number %d, want %d", p.SequenceNumber, want)
		}
		want++
		index, kk, m, ok := fecIndex(p)
		if !ok {
			t.Fatalf("packet %d has no parity extension", p.SequenceNumber)
		}
		if index == 0 {
			frame++
			k = kk
			if m != parityFor(kk, 0.2) {
				t.Fatalf("frame %d: %d parity per group, want %d", frame, m, parityFor(kk, 0.2))
			}
		}
		if p.Marker != (index == k-1) {
			t.Fatalf("frame %d packet %d of %d: marker %v", frame, index, k, p.Marker)
		}
	}
	if frame != 3 {
		t.Fatalf("%d frames, want 3", frame)
	}
}

// Without loss there is no parity, and no extension either: the packets go as they came.
// So does every packet for a PdaNet that does not offer the extension.
func TestFecNoParityNoChange(t *testing.T) {
	for _, tc := range []struct {
		share float64
		ext   bool
	}{{0, true}, {0.5, false}} {
		out := &sent{}
		sendFrames(t, fecWriter(tc.share, out, tc.ext), 3000)
		pkts, _ := parameterFrame(100, 9000, 3000)
		if len(out.pkts) != len(pkts) {
			t.Fatalf("%+v: %d packets, want %d", tc, len(out.pkts), len(pkts))
		}
		for i, p := range out.pkts {
			if p.Extension || p.SequenceNumber != pkts[i].SequenceNumber || p.Marker != pkts[i].Marker || !bytes.Equal(p.Payload, pkts[i].Payload) {
				t.Fatalf("%+v: packet %d changed", tc, i)
			}
		}
	}
}

// Any loss within what a group's parity covers is rebuilt at once, with no round trip: from a
// frame of one packet to a keyframe of several interleaved groups.
func TestFecRebuildsWhatItsParityCovers(t *testing.T) {
	r := rand.New(rand.NewPCG(7, 11))
	for _, size := range []int{40, 3000, 30_000, 300_000, 1_000_000} {
		for round := 0; round < 20; round++ {
			out := &sent{}
			frames := sendFrames(t, fecWriter(0.15, out, true), size, 3000)
			// Lose as many packets of each group as it has parity, data or parity alike.
			byGroup := map[[2]int][]int{} // frame start, group -> indices into out.pkts
			start := 0
			for i, p := range out.pkts {
				index, k, m, _ := fecIndex(p)
				if index == 0 {
					start = i
				}
				l := newFecLayout(k, m)
				grp := index % l.g
				if index >= k {
					grp = (index - k) % l.g
				}
				byGroup[[2]int{start, grp}] = append(byGroup[[2]int{start, grp}], i)
			}
			lost := map[int]bool{}
			for _, members := range byGroup {
				_, _, m, _ := fecIndex(out.pkts[members[0]])
				for _, i := range r.Perm(len(members))[:m] {
					lost[members[i]] = true
				}
			}
			var arrived []*rtp.Packet
			lostData := 0
			for i, p := range out.pkts {
				if !lost[i] {
					arrived = append(arrived, p)
				} else if index, k, _, _ := fecIndex(p); index < k {
					lostData++
				}
			}
			got, dropped, fr := receiveAll(arrived)
			if len(got) != 2 || dropped != 0 || !bytes.Equal(got[0], frames[0]) || !bytes.Equal(got[1], frames[1]) || fr.rebuilt != lostData {
				t.Fatalf("size %d round %d: %d frames, %d dropped, rebuilt %d of %d data packets lost", size, round, len(got), dropped, fr.rebuilt, lostData)
			}
		}
	}
}

// Lost parity holds nothing up: the frame before it is whole, and so is the one after it.
func TestFecLostParityHoldsNothing(t *testing.T) {
	out := &sent{}
	frames := sendFrames(t, fecWriter(0.3, out, true), 3000, 30_000, 40)
	var arrived []*rtp.Packet
	for _, p := range out.pkts {
		if index, k, _, _ := fecIndex(p); index < k {
			arrived = append(arrived, p)
		}
	}
	got, dropped, _ := receiveAll(arrived)
	if len(got) != 3 || dropped != 0 {
		t.Fatalf("%d frames, %d dropped; want all 3 at once, none lost", len(got), dropped)
	}
	for i := range frames {
		if !bytes.Equal(got[i], frames[i]) {
			t.Fatalf("frame %d differs", i)
		}
	}
}

// More loss than the parity covers is left to the resend, which still completes the frame,
// and a packet sent again after it was rebuilt is not handed on twice.
func TestFecBeyondItsParityWaitsForTheResend(t *testing.T) {
	out := &sent{}
	frames := sendFrames(t, fecWriter(0.1, out, true), 30_000)
	_, k, m, _ := fecIndex(out.pkts[0])
	sb := &h264Frames{}
	fr := newFecReceiver(testFecExt, sb.notMedia, nil)
	now := time.Now()
	held := map[int]bool{}
	for i := 0; i <= m; i++ {
		held[i*2+1] = true // one more data packet lost than the parity covers
	}
	for i, p := range out.pkts {
		if held[i] {
			continue
		}
		for _, q := range fr.push(p, now) {
			sb.Push(q, now)
		}
	}
	if s := sb.Pop(now, time.Second); s != nil {
		t.Fatal("a frame with too much lost came out")
	}
	for i := range held {
		for _, q := range fr.push(out.pkts[i], now) {
			sb.Push(q, now)
		}
	}
	s := sb.Pop(now, time.Second)
	if s == nil || !bytes.Equal(s.Data, frames[0]) || s.PrevDroppedPackets != 0 {
		t.Fatalf("the resent packets did not complete the frame of %d packets", k)
	}
	if again := fr.push(out.pkts[1], now); len(again) != 0 {
		t.Fatal("a packet sent again came through twice")
	}
}

// Parity comes and goes with the loss, frame by frame: the numbers stay in order across, frames
// without parity pass as before, and the frames with it still rebuild what they lose.
func TestFecOnAndOffMidStream(t *testing.T) {
	share := 0.0
	out := &sent{}
	w := (&fecInterceptor{factory: &fecFactory{share: func() float64 { return share }}}).BindLocalStream(
		&interceptor.StreamInfo{MimeType: "video/H264", RTPHeaderExtensions: []interceptor.RTPHeaderExtension{{URI: fecURI, ID: testFecExt}}}, out)
	var frames [][]byte
	seq := uint16(65530) // across the wrap too
	for i, s := range []float64{0, 0.3, 0.3, 0, 0.2, 0} {
		share = s
		pkts, data := parameterFrame(seq, uint32(9000*(i+1)), 6000)
		for _, p := range pkts {
			if _, err := w.Write(&p.Header, p.Payload, nil); err != nil {
				t.Fatal(err)
			}
		}
		seq += uint16(len(pkts))
		frames = append(frames, data)
	}
	var arrived []*rtp.Packet
	want := uint16(65530)
	for _, p := range out.pkts {
		if p.SequenceNumber != want {
			t.Fatalf("sequence number %d, want %d", p.SequenceNumber, want)
		}
		want++
		// Lose the first data packet of each frame with parity, and all its parity but one.
		if index, k, _, ok := fecIndex(p); ok && (index == 0 || index > k) {
			continue
		}
		arrived = append(arrived, p)
	}
	got, dropped, fr := receiveAll(arrived)
	if len(got) != len(frames) || dropped != 0 || fr.rebuilt != 3 {
		t.Fatalf("%d frames of %d, %d dropped, %d rebuilt; want all, none, 3", len(got), len(frames), dropped, fr.rebuilt)
	}
	for i := range frames {
		if !bytes.Equal(got[i], frames[i]) {
			t.Fatalf("frame %d differs", i)
		}
	}
}

// A frame of more than fecMaxFrame packets goes without parity, all of it: parity in its middle
// would be holes in it. The frames around it keep theirs.
func TestFecFrameTooLargeGoesWithoutParity(t *testing.T) {
	out := &sent{}
	sendFrames(t, fecWriter(0.2, out, true), 3000, 1200*(fecMaxFrame+100), 3000)
	want := uint16(100)
	var big, protected int
	for _, p := range out.pkts {
		if p.SequenceNumber != want {
			t.Fatalf("sequence number %d, want %d", p.SequenceNumber, want)
		}
		want++
		if p.Timestamp == 18000 {
			if _, _, _, ok := fecIndex(p); ok {
				t.Fatal("a packet of the frame too large carries the parity extension")
			}
			big++
		} else if _, _, _, ok := fecIndex(p); ok {
			protected++
		}
	}
	if big <= fecMaxFrame || protected <= 4 {
		t.Fatalf("%d packets in the large frame, %d protected around it", big, protected)
	}
}

// The viewer never asks for parity, lost or not, nor for a packet the parity rebuilt.
func TestNackSkipsParityAndRebuilt(t *testing.T) {
	g, st := newTestNackGen(100 * time.Millisecond)
	now := time.Now()
	g.arrived(st, 100, now)
	g.notMedia(7, 105, 3)
	g.arrived(st, 110, now)
	g.recovered(7, 102)
	var missing []uint16
	for s := range st.missing {
		missing = append(missing, s)
	}
	slices.Sort(missing)
	if want := []uint16{101, 103, 104, 108, 109}; !slices.Equal(missing, want) {
		t.Fatalf("missing %v, want %v", missing, want)
	}
}

// A parity mark goes once the stream is past it. The numbers come round after 65536 packets, and
// a mark kept that long made a lost packet of the screen with the same number look like parity:
// never asked for, its frame waited until the viewer gave it up.
func TestNackForgetsParityOnceItIsPast(t *testing.T) {
	g, st := newTestNackGen(100 * time.Millisecond)
	now := time.Now()
	g.arrived(st, 100, now)
	g.notMedia(7, 101, 1)
	g.arrived(st, 101, now)                     // the parity came
	for seq := uint16(102); seq != 101; seq++ { // once round, every packet in
		g.arrived(st, seq, now)
	}
	g.arrived(st, 102, now) // 101 again, now a packet of the screen, lost
	if st.missing[101] == nil {
		t.Fatal("a lost packet whose number held parity 65536 packets before was not asked for")
	}
	g.notMedia(7, 90, 3) // parity the stream already passed: nothing to mark
	if st.parity[90%len(st.parity)] != 0 {
		t.Fatal("parity the stream had passed was marked")
	}
}

// The share of parity follows the loss, a packet at least for each group.
func TestFecParityFollowsLoss(t *testing.T) {
	for _, tc := range []struct {
		k     int
		share float64
		want  int
	}{
		{1, fecShareFor(0.02), 1}, {10, fecShareFor(0.02), 2}, {30, fecShareFor(0.02), 4},
		{99, fecShareFor(0.02), 11}, {400, fecShareFor(0.02), 15}, {192, fecMaxShare, 63}, {10, 0, 0},
	} {
		if got := parityFor(tc.k, tc.share); got != tc.want {
			t.Errorf("%d packets at share %.2f: %d parity, want %d", tc.k, tc.share, got, tc.want)
		}
	}
}
