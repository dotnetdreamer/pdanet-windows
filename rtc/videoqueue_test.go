package main

import (
	"bytes"
	"encoding/binary"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

func TestVideoQueueBoundsAndMarksDiscontinuity(t *testing.T) {
	p := &peer{video: make(chan videoOut, videoQueue)}
	for range videoQueue {
		p.queueVideo(videoOut{data: []byte{1}})
	}
	p.queueVideo(videoOut{data: []byte{2}})
	if !p.dropped || len(p.video) != videoQueue || p.videoBytes.Load() != videoQueue {
		t.Fatal("queue overflow was not bounded and recorded")
	}
	for len(p.video) > 0 {
		v := <-p.video
		p.videoBytes.Add(-int64(len(v.data)))
	}
	p.queueVideo(videoOut{flags: frameKey, data: []byte{3}})
	if v := <-p.video; v.flags != frameKey|frameAfterLoss || p.dropped {
		t.Fatal("first frame following overflow did not report the missing dependency")
	}
}

func TestVideoWriterRetriesThrottledRecoveryOnIdleQueue(t *testing.T) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	p := &peer{pc: pc, video: make(chan videoOut, videoQueue), closed: make(chan struct{})}
	p.ssrc.Store(7)
	initial := time.Now()
	p.lastKeyReq = initial // a recent request, before the local queue stalls
	p.video <- videoOut{flags: frameKey, data: []byte{1}, at: initial.Add(-2 * videoQueueWait)}
	p.videoBytes.Store(1)
	done := make(chan struct{})
	defer func() { close(p.closed); p.keyframeReceived(); <-done }()
	go func() { p.videoWriter(); close(done) }()
	deadline := time.Now().Add(time.Second)
	for {
		p.mu.Lock()
		latest := p.lastKeyReq
		p.mu.Unlock()
		if latest.After(initial) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("stale sole frame lost its throttled recovery request when the local queue became idle")
		}
		time.Sleep(5 * time.Millisecond)
	}
	p.keyframeReceived()
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.keyPending || p.keyRetry != nil {
		t.Fatal("a received keyframe did not cancel periodic recovery requests")
	}
}

func TestOldQueuedKeyframeCannotCancelNewerRecovery(t *testing.T) {
	now := time.Now()
	p := &peer{keyPending: true, lastKeyReq: now}
	p.keyframeReceivedAt(now.Add(-time.Millisecond))
	if !p.keyPending {
		t.Fatal("a queued old keyframe cancelled recovery for a newer missing frame")
	}
	p.keyframeReceivedAt(now.Add(time.Millisecond))
	if p.keyPending {
		t.Fatal("a fresh keyframe did not clear recovery")
	}
}

func TestVideoQueueByteBudgetAllowsOneLargeKeyframe(t *testing.T) {
	p := &peer{video: make(chan videoOut, videoQueue)}
	p.queueVideo(videoOut{flags: frameKey, data: make([]byte, videoQueueBytes+1)})
	p.queueVideo(videoOut{data: []byte{1}})
	if len(p.video) != 1 || !p.dropped || p.videoBytes.Load() != videoQueueBytes+1 {
		t.Fatal("byte budget should allow a single large frame and reject a growing backlog")
	}
}

type videoSink struct{ writes chan []byte }

func (s *videoSink) Write(b []byte) (int, error) {
	s.writes <- append([]byte(nil), b...)
	return len(b), nil
}

func TestVideoWriterDropsStaleDependenciesUntilKeyframe(t *testing.T) {
	sink := &videoSink{writes: make(chan []byte, 4)}
	p := &peer{video: make(chan videoOut, videoQueue), closed: make(chan struct{}), link: newIPC(bytes.NewReader(nil), sink)}
	defer close(p.closed)
	now := time.Now()
	for _, v := range []videoOut{
		{flags: frameKey, ts: 1, data: []byte{1}, at: now.Add(-2 * videoQueueWait)},
		{ts: 2, data: []byte{2}, at: now},
		{flags: frameKey, ts: 3, data: []byte{3}, at: now},
		{ts: 4, data: []byte{4}, at: now},
	} {
		p.video <- v
		p.videoBytes.Add(int64(len(v.data)))
	}
	go p.videoWriter()
	for i, ts := range []uint32{3, 4} {
		select {
		case b := <-sink.writes:
			if len(b) != 11 || b[4] != kindVideo || binary.LittleEndian.Uint32(b[6:]) != ts {
				t.Fatalf("unexpected output: %x", b)
			}
			want := byte(0)
			if i == 0 {
				want = frameKey | frameAfterLoss
			}
			if b[5] != want {
				t.Fatalf("timestamp %d: flags %d, want %d", ts, b[5], want)
			}
		case <-time.After(time.Second):
			t.Fatal("writer did not recover at the fresh keyframe")
		}
	}
	if p.videoBytes.Load() != 0 {
		t.Fatal("written and discarded frames retained the byte budget")
	}
}
