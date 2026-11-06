package main

import (
	"bytes"
	"testing"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/rtp/codecs"
	"github.com/pion/webrtc/v4/pkg/media/samplebuilder"
)

// testFrame packetizes one H.264 frame of size bytes (an IDR slice) as the PC that shares does:
// fragments of at most 1200 bytes, the marker on the last packet, sequence numbers from seq on.
func testFrame(seq uint16, ts uint32, size int) ([]*rtp.Packet, []byte) {
	nal := make([]byte, size)
	nal[0] = 0x65
	for i := 1; i < size; i++ {
		nal[i] = byte(i*7 + 1)
	}
	var p codecs.H264Payloader
	payloads := p.Payload(1200, append([]byte{0, 0, 0, 1}, nal...))
	pkts := make([]*rtp.Packet, len(payloads))
	for i, pl := range payloads {
		pkts[i] = &rtp.Packet{
			Header:  rtp.Header{Version: 2, PayloadType: 102, SequenceNumber: seq + uint16(i), Timestamp: ts, SSRC: 1, Marker: i == len(payloads)-1},
			Payload: pl,
		}
	}
	return pkts, nal
}

// The PC that views builds frames as receive does. Pion's builder holds a whole frame, its
// marker packet in, until the first packet of the next frame comes; that packet hands it out,
// with nothing counted as lost. This is why the PC that shares follows the last frame before a
// rest with a small seal frame (FramePlanner): without it, the last and sharpest frame would
// only show at the next change of the screen.
func TestBuilderHoldsTheLastFrame(t *testing.T) {
	sb := samplebuilder.New(2048, &codecs.H264Packet{}, 90000, samplebuilder.WithMaxTimeDelay(400*time.Millisecond))
	last, nal := testFrame(10, 9000, 3000)
	for _, p := range last {
		sb.Push(p)
	}
	if s := sb.Pop(); s != nil {
		t.Fatalf("the builder handed out a frame without a packet after it: the seal frame would not be needed")
	}
	seal, _ := testFrame(10+uint16(len(last)), 12000, 40)
	sb.Push(seal[0])
	s := sb.Pop()
	if s == nil || !bytes.Contains(s.Data, nal[:64]) {
		t.Fatalf("the first packet of the next frame did not hand out the frame before it")
	}
	if s.PrevDroppedPackets != 0 {
		t.Fatalf("the frame says %d packets were dropped before it", s.PrevDroppedPackets)
	}
	if more := sb.Pop(); more != nil {
		t.Fatalf("the seal frame came out too, held frames are not expected to")
	}
}
