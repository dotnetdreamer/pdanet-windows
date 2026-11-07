package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

type startupState struct {
	connection webrtc.PeerConnectionState
	dtls       webrtc.DTLSTransportState
}

// Capture transport state at the instant the app is told to start capture. Checking it
// later would hide the race: DTLS can finish between the announcement and the assertion.
type startupSink struct {
	peer      *peer
	connected chan startupState
	video     chan []byte
}

func (s *startupSink) Write(b []byte) (int, error) {
	if len(b) < 5 || int(binary.LittleEndian.Uint32(b))+4 != len(b) {
		return 0, fmt.Errorf("unexpected startup IPC write of %d bytes", len(b))
	}
	switch b[4] {
	case kindJSON:
		var e event
		if err := json.Unmarshal(b[5:], &e); err != nil {
			return 0, err
		}
		if e.Ev == "connected" {
			s.connected <- startupState{s.peer.pc.ConnectionState(), s.peer.pc.SCTP().Transport().State()}
		}
	case kindVideo:
		s.video <- append([]byte(nil), b[5:]...)
	}
	return len(b), nil
}

func newStartupPeer(t *testing.T, host bool) (*peer, *startupSink) {
	t.Helper()
	sink := &startupSink{connected: make(chan startupState, 4), video: make(chan []byte, 4)}
	p, err := newPeer(peerConfig{host: host, link: newIPC(bytes.NewReader(nil), sink)})
	if err != nil {
		t.Fatal(err)
	}
	sink.peer = p
	t.Cleanup(p.close)
	return p, sink
}

func TestICEConnectedDoesNotStartCaptureBeforeDTLS(t *testing.T) {
	p, sink := newStartupPeer(t, true)
	p.onICEState(webrtc.ICEConnectionStateConnected)
	if p.isConnected() {
		t.Fatal("ICE connectivity alone started capture before DTLS/SRTP became writable")
	}
	select {
	case state := <-sink.connected:
		t.Fatalf("announced live at peer state %s, DTLS %s", state.connection, state.dtls)
	default:
	}
}

func TestConnectedAnnouncementWaitsForDTLSAndDeliversOpeningFrame(t *testing.T) {
	host, hostSink := newStartupPeer(t, true)
	view, viewSink := newStartupPeer(t, false)
	offer, err := view.offer()
	if err != nil {
		t.Fatal(err)
	}
	answer, err := host.answer(offer)
	if err != nil {
		t.Fatal(err)
	}
	if err = view.accept(answer); err != nil {
		t.Fatal(err)
	}
	select {
	case state := <-hostSink.connected:
		if state.connection != webrtc.PeerConnectionStateConnected || state.dtls != webrtc.DTLSTransportStateConnected {
			t.Fatalf("capture started at peer state %s, DTLS %s", state.connection, state.dtls)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("host did not become ready for capture")
	}

	// Exactly one opening access unit, without retrying it or first waiting for a data
	// channel round trip. A silent pre-DTLS drop otherwise needs a later keyframe to heal.
	frame := append([]byte{0, 0, 0, 1, 0x67, 0x42, 0, 0x1f, 0, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80, 0, 0, 0, 1, 0x65},
		bytes.Repeat([]byte{0x88, 0x84, 0x21}, 2000)...)
	b := make([]byte, 5, len(frame)+5)
	binary.LittleEndian.PutUint32(b, 33333)
	b[4] = 1
	host.writeVideo(append(b, frame...))
	select {
	case received := <-viewSink.video:
		if len(received) < 5 || received[0]&frameKey == 0 || !bytes.Equal(received[5:], frame) {
			t.Fatalf("opening keyframe arrived changed: %d bytes", len(received))
		}
	case <-time.After(2 * time.Second):
		t.Fatal("the single opening keyframe was lost before the media transport became ready")
	}
}

// An ICE restart keeps DTLS and passes through connecting without a disconnect. The app must
// hear "connected" again, with the new path, or a viewer that said "reconnecting" stays so.
func TestICERestartAnnouncesConnectedAgain(t *testing.T) {
	host, hostSink := newStartupPeer(t, true)
	view, viewSink := newStartupPeer(t, false)
	offer, err := view.offer()
	if err != nil {
		t.Fatal(err)
	}
	answer, err := host.answer(offer)
	if err != nil {
		t.Fatal(err)
	}
	if err = view.accept(answer); err != nil {
		t.Fatal(err)
	}
	wait := func(side string, sink *startupSink) {
		t.Helper()
		select {
		case <-sink.connected:
		case <-time.After(10 * time.Second):
			t.Fatalf("the %s did not announce connected", side)
		}
	}
	wait("host", hostSink)
	wait("viewer", viewSink)

	restart, err := view.restartOffer()
	if err != nil {
		t.Fatal(err)
	}
	answer, err = host.restartAnswer(restart)
	if err != nil {
		t.Fatal(err)
	}
	if err = view.accept(answer); err != nil {
		t.Fatal(err)
	}
	wait("host after the ICE restart", hostSink)
	wait("viewer after the ICE restart", viewSink)
	if !host.isConnected() || !view.isConnected() {
		t.Fatal("a peer is not connected after the ICE restart")
	}
}
