package main

import (
	"bytes"
	"io"
	"net"
	"testing"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtp"
	"github.com/pion/rtp/codecs"
	"github.com/pion/webrtc/v4"
)

func parameterFrame(seq uint16, ts uint32, size int) ([]*rtp.Packet, []byte) {
	_, nal := testFrame(seq, ts, size)
	data := append([]byte{0, 0, 0, 1, 0x67, 0x42, 0, 0x1f, 0, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80, 0, 0, 0, 1}, nal...)
	payloader := &codecs.H264Payloader{}
	payloads := payloader.Payload(1200, data)
	packets := make([]*rtp.Packet, len(payloads))
	for i, payload := range payloads {
		packets[i] = &rtp.Packet{Header: rtp.Header{Version: 2, SequenceNumber: seq + uint16(i), Timestamp: ts, Marker: i == len(payloads)-1}, Payload: payload}
	}
	return packets, data
}

func TestH264FramesEmitAtMarker(t *testing.T) {
	for _, size := range []int{40, 3000, 100_000} {
		b := &h264Frames{}
		now := time.Now()
		packets, data := parameterFrame(10, 9000, size)
		for i, p := range packets {
			b.Push(p, now)
			s := b.Pop(now, repairWait(20*time.Millisecond))
			if i < len(packets)-1 {
				if s != nil {
					t.Fatal("incomplete frame was emitted")
				}
				continue
			}
			if s == nil || !bytes.Equal(s.Data, data) {
				t.Fatalf("size %d: final marker did not emit the complete frame without another packet", size)
			}
			if s.PacketTimestamp != 9000 || s.PrevDroppedPackets != 0 {
				t.Fatalf("unexpected metadata: %+v", s)
			}
		}
		if b.Pop(now, time.Second) != nil {
			t.Fatal("frame was emitted twice")
		}
	}
}

func TestH264FramesReorderRepairAndWrap(t *testing.T) {
	for _, seq := range []uint16{10, 65534} {
		b := &h264Frames{synced: true}
		now := time.Now()
		packets, nal := testFrame(seq, 9000, 6000)
		// The first fragment is reordered; one middle fragment is retransmitted last.
		for _, i := range []int{1, 0, 3, 4, 5} {
			b.Push(packets[i], now)
			if s := b.Pop(now, 150*time.Millisecond); s != nil {
				t.Fatal("frame with a missing middle fragment was emitted")
			}
		}
		b.Push(packets[2], now.Add(80*time.Millisecond))
		s := b.Pop(now.Add(80*time.Millisecond), 150*time.Millisecond)
		if s == nil || !bytes.Equal(s.Data, append([]byte{0, 0, 0, 1}, nal...)) || s.PrevDroppedPackets != 0 {
			t.Fatalf("sequence %d: repaired frame did not reconstruct exactly", seq)
		}
		b.Push(packets[2], now.Add(100*time.Millisecond))
		if b.Pop(now.Add(time.Second), 150*time.Millisecond) != nil || b.head != b.tail {
			t.Fatal("late duplicate reentered the frame queue")
		}
		next, _ := testFrame(seq+uint16(len(packets)), 12000, 40)
		b.Push(next[0], now.Add(110*time.Millisecond))
		if s := b.Pop(now.Add(110*time.Millisecond), 150*time.Millisecond); s == nil || s.PrevDroppedPackets != 0 {
			t.Fatal("next frame after a repair was held or marked lost")
		}
	}
}

func TestH264FramesInitialMarkerCanArriveBeforeStart(t *testing.T) {
	b := &h264Frames{}
	now := time.Now()
	packets, data := parameterFrame(65534, 9000, 6000)
	for i := len(packets) - 1; i >= 0; i-- {
		b.Push(packets[i], now)
		s := b.Pop(now, 150*time.Millisecond)
		if i > 0 {
			if s != nil || b.dropped > 0 {
				t.Fatal("initial marker-first reorder was mistaken for a damaged frame")
			}
		} else if s == nil || !bytes.Equal(s.Data, data) || s.PrevDroppedPackets != 0 {
			t.Fatal("initial frame did not reconstruct after its start arrived last")
		}
	}
}

func TestH264FramesInitialSingleNalMarkerWaitsForParameterSets(t *testing.T) {
	b := &h264Frames{}
	now := time.Now()
	for i, payload := range [][]byte{{0x65, 1}, {0x68, 1}, {0x67, 1}} {
		b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: uint16(12 - i), Timestamp: 1, Marker: i == 0}, Payload: payload}, now)
		s := b.Pop(now, 150*time.Millisecond)
		if i < 2 && s != nil {
			t.Fatal("initial single-NAL marker was emitted before its reordered decoder headers")
		}
		if i == 2 && (s == nil || !hasParameterSets(s.Data) || s.PrevDroppedPackets != 0) {
			t.Fatal("initial frame with reordered SPS/PPS did not reconstruct")
		}
	}
}

func TestH264FramesOldBacklogSharesOneRepairDeadline(t *testing.T) {
	b := &h264Frames{synced: true}
	now := time.Now()
	for frame := uint16(0); frame < 2; frame++ {
		packets, _ := testFrame(1+frame*3, 9000+uint32(frame)*3000, 3000)
		b.Push(packets[0], now)
		b.Push(packets[2], now)
	}
	good, _ := testFrame(7, 15000, 40)
	b.Push(good[0], now)
	s := b.Pop(now.Add(150*time.Millisecond), 150*time.Millisecond)
	if s == nil || s.PacketTimestamp != 15000 || s.PrevDroppedPackets == 0 {
		t.Fatal("already-old damaged frames each started another repair deadline")
	}
}

func TestH264FramesDropWholeDamagedFrameAtDeadline(t *testing.T) {
	b := &h264Frames{synced: true}
	now := time.Now()
	packets, _ := testFrame(10, 9000, 6000)
	for i, p := range packets {
		if i != 2 {
			b.Push(p, now)
		}
	}
	next, nal := testFrame(10+uint16(len(packets)), 12000, 40)
	b.Push(next[0], now.Add(100*time.Millisecond))
	if b.Pop(now.Add(149*time.Millisecond), 150*time.Millisecond) != nil {
		t.Fatal("repair grace ended too soon")
	}
	s := b.Pop(now.Add(150*time.Millisecond), 150*time.Millisecond)
	if s == nil || s.PacketTimestamp != 12000 || !bytes.HasSuffix(s.Data, nal) || s.PrevDroppedPackets == 0 {
		t.Fatal("deadline did not discard damaged frame and flag the following complete one")
	}
	// The expired fragment must never modify the next frame's depacketizer.
	b.Push(packets[2], now.Add(170*time.Millisecond))
	last, lastNal := testFrame(next[0].SequenceNumber+1, 15000, 3000)
	for _, p := range last {
		b.Push(p, now.Add(180*time.Millisecond))
	}
	s = b.Pop(now.Add(180*time.Millisecond), 150*time.Millisecond)
	if s == nil || !bytes.HasSuffix(s.Data, lastNal) || s.PrevDroppedPackets != 0 {
		t.Fatal("expired retransmission damaged a subsequent frame")
	}
}

func TestH264FramesDoNotEmitPartialIdrSlices(t *testing.T) {
	b := &h264Frames{synced: true}
	now := time.Now()
	// The marker packet itself is an IDR NAL; it is still only part of this frame.
	b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: 10, Timestamp: 1}, Payload: []byte{0x65, 1}}, now)
	b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: 12, Timestamp: 1, Marker: true}, Payload: []byte{0x65, 3}}, now)
	good, _ := testFrame(13, 2, 40)
	b.Push(good[0], now)
	s := b.Pop(now.Add(150*time.Millisecond), 150*time.Millisecond)
	if s == nil || s.PacketTimestamp != 2 || s.PrevDroppedPackets == 0 {
		t.Fatal("a damaged IDR frame was emitted instead of the following complete frame")
	}
}

func TestH264FramesRejectMalformedFuAndBoundBuffer(t *testing.T) {
	for _, payload := range [][]byte{{}, {28}, {28, 0x45, 1}, {28, 0x85, 1}, {28, 0xc5, 1}, {24, 0, 5, 1}} {
		b := &h264Frames{}
		now := time.Now()
		b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: 1, Timestamp: 1, Marker: true}, Payload: payload}, now)
		if b.Pop(now.Add(time.Second), time.Second) != nil {
			t.Fatalf("malformed payload emitted: %x", payload)
		}
		if b.dropped == 0 {
			t.Fatal("malformed frame was not marked lost")
		}
	}
	b := &h264Frames{}
	now := time.Now()
	for seq := uint16(1); seq < 10_000; seq++ {
		b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: seq, Timestamp: 1}, Payload: []byte{0x61, 1}}, now)
		if int(uint16(b.tail-b.head)) > len(b.packets) {
			t.Fatal("packet window exceeded its bound")
		}
	}
	if b.Pop(now.Add(time.Second), 150*time.Millisecond) != nil || b.head != b.tail || b.dropped == 0 {
		t.Fatal("unterminated frame was not discarded within the bounded window")
	}
}

type idleFrameReader struct {
	packets  chan *rtp.Packet
	timedOut chan struct{}
	deadline time.Time
}

func (r *idleFrameReader) SetReadDeadline(deadline time.Time) error {
	r.deadline = deadline
	return nil
}

func (r *idleFrameReader) ReadRTP() (*rtp.Packet, interceptor.Attributes, error) {
	var timeout <-chan time.Time
	if !r.deadline.IsZero() {
		timer := time.NewTimer(time.Until(r.deadline))
		defer timer.Stop()
		timeout = timer.C
	}
	select {
	case p, ok := <-r.packets:
		if !ok {
			return nil, nil, io.EOF
		}
		return p, nil, nil
	case <-timeout:
		select {
		case r.timedOut <- struct{}{}:
		default:
		}
		return nil, nil, &net.DNSError{Err: "deadline", IsTimeout: true}
	}
}

func TestReceiveExpiresLossEvenWhenDesktopRests(t *testing.T) {
	r := &idleFrameReader{packets: make(chan *rtp.Packet, 4), timedOut: make(chan struct{}, 1)}
	p := &peer{video: make(chan videoOut, videoQueue)}
	p.rtt.Store(int64(10 * time.Millisecond))
	done := make(chan struct{})
	defer func() {
		close(r.packets)
		<-done
	}()
	go func() { p.receiveFrames(r); close(done) }()
	packets, _ := testFrame(1, 9000, 3000)
	r.packets <- packets[0]
	r.packets <- packets[len(packets)-1]
	select {
	case <-r.timedOut:
	case <-time.After(time.Second):
		t.Fatal("missing packet did not expire without a subsequent frame")
	}
	good, _ := parameterFrame(1+uint16(len(packets)), 12000, 40)
	for _, packet := range good {
		r.packets <- packet
	}
	select {
	case v := <-p.video:
		if v.ts != 12000 || v.flags != frameKey|frameAfterLoss {
			t.Fatal("receiver did not continue with a clean flagged keyframe after the idle timeout")
		}
	case <-time.After(time.Second):
		t.Fatal("receiver stopped reading after the repair deadline")
	}
}

func TestReceiveRetriesThrottledKeyframeWhenDesktopRests(t *testing.T) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	r := &idleFrameReader{packets: make(chan *rtp.Packet, 4), timedOut: make(chan struct{}, 8)}
	p := &peer{pc: pc, video: make(chan videoOut, videoQueue)}
	defer p.keyframeReceived()
	p.ssrc.Store(7)
	p.rtt.Store(int64(10 * time.Millisecond))
	done := make(chan struct{})
	defer func() { close(r.packets); <-done }()
	go func() { p.receiveFrames(r); close(done) }()
	packets, _ := testFrame(1, 9000, 3000)
	r.packets <- packets[0]
	r.packets <- packets[len(packets)-1]
	select {
	case <-r.timedOut:
	case <-time.After(time.Second):
		t.Fatal("repair deadline did not expire")
	}
	p.mu.Lock()
	initial := p.lastKeyReq
	p.mu.Unlock()
	deadline := time.Now().Add(time.Second)
	for {
		p.mu.Lock()
		latest := p.lastKeyReq
		p.mu.Unlock()
		if latest.After(initial) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("throttled keyframe request was never retried without further RTP")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestH264FramesWindowOverflowCannotEmitPartialKeyframe(t *testing.T) {
	b := &h264Frames{synced: true}
	now := time.Now()
	for seq := uint16(1); seq <= 4100; seq++ {
		b.Push(&rtp.Packet{Header: rtp.Header{SequenceNumber: seq, Timestamp: 1, Marker: seq == 4100}, Payload: []byte{0x65, 1}}, now)
		if s := b.Pop(now, 150*time.Millisecond); s != nil {
			t.Fatal("a keyframe whose leading packets exceeded the reorder window was emitted partially")
		}
	}
	good, _ := testFrame(4101, 2, 40)
	b.Push(good[0], now)
	if s := b.Pop(now, 150*time.Millisecond); s == nil || s.PacketTimestamp != 2 || s.PrevDroppedPackets == 0 {
		t.Fatal("window overflow did not recover at the next complete frame with loss marked")
	}
}

func TestRepairWaitFollowsRttWithinBounds(t *testing.T) {
	for _, tc := range []struct{ rtt, want time.Duration }{
		{0, 470 * time.Millisecond}, {10 * time.Millisecond, 150 * time.Millisecond},
		{200 * time.Millisecond, 320 * time.Millisecond}, {time.Second, 750 * time.Millisecond},
	} {
		if got := repairWait(tc.rtt); got != tc.want {
			t.Fatalf("RTT %v: repair wait %v, want %v", tc.rtt, got, tc.want)
		}
	}
}
