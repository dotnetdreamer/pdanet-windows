package main

import (
	"bytes"
	"testing"
	"time"

	"github.com/pion/rtp"
)

// A paced frame can take longer to serialize than one loss-repair interval without
// losing any packet. Advancing contiguous fragments must not be mistaken for loss.
func TestH264FramesSlowIntactFrameSurvivesRepairInterval(t *testing.T) {
	frames := &h264Frames{}
	packets, want := parameterFrame(10, 9000, 12_000)
	start := time.Now()
	wait := 150 * time.Millisecond
	for i, packet := range packets {
		now := start.Add(time.Duration(i) * 30 * time.Millisecond)
		frames.Push(packet, now)
		sample := frames.Pop(now, wait)
		if i < len(packets)-1 && sample != nil {
			t.Fatal("incomplete paced frame was emitted")
		}
		if i == len(packets)-1 && (sample == nil || sample.PrevDroppedPackets != 0 || !bytes.Equal(sample.Data, want)) {
			t.Fatalf("intact in-order frame spanning %v was discarded by a %v loss-repair interval", now.Sub(start), wait)
		}
	}
}

// A missing middle fragment waits while the rest of its frame still arrives: the packet asked
// for again comes behind those. Once the frame's last packet is in, one repair interval is left.
func TestH264FramesMissingMiddleWaitsForTheRestOfItsFrame(t *testing.T) {
	frames := &h264Frames{}
	packets, _ := parameterFrame(10, 9000, 30_000)
	start := time.Now()
	wait := 150 * time.Millisecond
	var last time.Time
	for i, packet := range packets {
		if i == 2 {
			continue
		}
		last = start.Add(time.Duration(i) * 30 * time.Millisecond)
		frames.Push(packet, last)
		if frames.Pop(last, wait) != nil {
			t.Fatal("frame with a missing middle fragment was emitted")
		}
		if frames.dropped != 0 {
			t.Fatalf("repair gave up at %v while the rest of the frame still arrived", last.Sub(start))
		}
	}
	if frames.Pop(last.Add(wait-time.Millisecond), wait); frames.dropped != 0 {
		t.Fatal("repair gave up before one interval after the frame's last packet")
	}
	if frames.Pop(last.Add(wait), wait); frames.dropped == 0 {
		t.Fatal("repair waited beyond one interval after the frame's last packet")
	}
}

// Packets of the next frame do not prolong the wait for a hole in this one: one repair interval
// after this frame's last packet it is given up, and the next frame goes, marked after a loss.
func TestH264FramesLaterFrameDoesNotExtendRepair(t *testing.T) {
	frames := &h264Frames{synced: true}
	packets, _ := parameterFrame(4, 9000, 6000)
	next, want := testFrame(packets[len(packets)-1].SequenceNumber+1, 12000, 6000)
	start := time.Now()
	wait := 150 * time.Millisecond
	for i, packet := range packets {
		if i == 1 {
			continue
		}
		frames.Push(packet, start)
		frames.Pop(start, wait)
	}
	for i, packet := range next {
		at := start.Add(time.Duration(i+1) * 20 * time.Millisecond)
		if !at.Before(start.Add(wait)) {
			t.Fatal("the next frame is too long for this test")
		}
		frames.Push(packet, at)
		if frames.Pop(at, wait) != nil || frames.dropped != 0 {
			t.Fatal("a frame went past the hole before its repair interval")
		}
	}
	if s := frames.Pop(start.Add(wait-time.Millisecond), wait); s != nil || frames.dropped != 0 {
		t.Fatal("the hole was given up early")
	}
	s := frames.Pop(start.Add(wait), wait)
	if s == nil || s.PrevDroppedPackets == 0 || !bytes.Equal(s.Data, append([]byte{0, 0, 0, 1}, want...)) {
		t.Fatal("packets of the next frame prolonged the repair of this one")
	}
}

func TestH264FramesMissingFirstStillExpires(t *testing.T) {
	frames := &h264Frames{synced: true}
	packets, _ := testFrame(10, 9000, 12_000)
	start := time.Now()
	wait := 150 * time.Millisecond
	for i, packet := range packets {
		if i == 0 {
			continue
		}
		now := start.Add(time.Duration(i-1) * 30 * time.Millisecond)
		frames.Push(packet, now)
		if frames.Pop(now, wait) != nil {
			t.Fatal("frame with no FU start was emitted")
		}
	}
	frames.Pop(start.Add(time.Second), wait)
	if frames.head != frames.tail || frames.dropped == 0 {
		t.Fatal("missing first fragment survived bounded repair without another frame")
	}
}

func TestH264FramesDuplicateDoesNotExtendProgress(t *testing.T) {
	frames := &h264Frames{}
	packets, _ := parameterFrame(10, 9000, 12_000)
	start := time.Now()
	wait := 150 * time.Millisecond
	for _, packet := range packets[:2] {
		frames.Push(packet, start)
		frames.Pop(start, wait)
	}
	frames.Push(packets[1].Clone(), start.Add(149*time.Millisecond))
	frames.Pop(start.Add(150*time.Millisecond), wait)
	if frames.head != frames.tail || frames.dropped == 0 {
		t.Fatal("duplicate packet extended an incomplete frame's repair deadline")
	}
}

func TestH264FramesContinuousProgressHasHardLifetimeBound(t *testing.T) {
	frames := &h264Frames{}
	packets, _ := parameterFrame(10, 9000, 50_000)
	start := time.Now()
	wait := 150 * time.Millisecond
	for i, packet := range packets {
		now := start.Add(time.Duration(i) * 100 * time.Millisecond)
		frames.Push(packet, now)
		if frames.Pop(now, wait) != nil {
			t.Fatal("frame exceeded its hard assembly lifetime")
		}
		if now.Sub(start) < frameWait && frames.dropped != 0 {
			t.Fatal("contiguous progress expired before the hard lifetime bound")
		}
		if now.Sub(start) >= frameWait && frames.dropped == 0 {
			t.Fatal("continuous traffic kept an unfinished frame alive beyond its hard bound")
		}
	}
}

func TestReceiveUpdatesDeadlineWhileIntactFrameMakesProgress(t *testing.T) {
	r := &idleFrameReader{packets: make(chan *rtp.Packet, 4), timedOut: make(chan struct{}, 8)}
	p := &peer{video: make(chan videoOut, videoQueue)}
	p.rtt.Store(int64(10 * time.Millisecond))
	done := make(chan struct{})
	defer func() { close(r.packets); <-done }()
	go func() { p.receiveFrames(r); close(done) }()
	packets, want := parameterFrame(10, 9000, 12_000)
	for i, packet := range packets {
		if i > 0 {
			time.Sleep(25 * time.Millisecond)
		}
		r.packets <- packet
	}
	select {
	case frame := <-p.video:
		if frame.flags != frameKey || !bytes.Equal(frame.data, want) {
			t.Fatal("slow intact frame arrived corrupted or was falsely flagged as lost")
		}
	case <-time.After(time.Second):
		t.Fatal("receive deadline discarded a progressing intact frame")
	}
}
