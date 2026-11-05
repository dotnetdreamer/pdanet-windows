package main

import (
	"bufio"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"sync"
)

// PdaNet and the helper exchange frames over stdin and stdout:
//
//	u32 little endian length of what follows | u8 kind | payload
//
// kindJSON frames are control messages: PdaNet sends {"op": ...}, the helper sends {"ev": ...}.
// kindVideo frames carry the screen. On the PC that shares, PdaNet sends u32 duration in
// microseconds, u8 flags and H.264 Annex B. On the PC that views, the helper sends u8 flags,
// u32 RTP timestamp and H.264 Annex B. kindCtl and kindLive frames are remote control
// messages for the reliable and the unreliable data channel, passed through untouched.
const (
	kindJSON  byte = 1
	kindVideo byte = 2
	kindCtl   byte = 3
	kindLive  byte = 4
)

// Flags of a received video frame.
const (
	frameKey       byte = 1 // the frame starts a new picture sequence (IDR)
	frameAfterLoss byte = 2 // frames before this one were lost
)

const maxFrame = 32 << 20

type ipc struct {
	r   *bufio.Reader
	w   *bufio.Writer
	wmu sync.Mutex
	buf []byte // reused for incoming frames, the reader handles one frame at a time
}

func newIPC(r io.Reader, w io.Writer) *ipc {
	return &ipc{r: bufio.NewReaderSize(r, 64<<10), w: bufio.NewWriterSize(w, 64<<10)}
}

// read returns the next frame. The payload stays valid only until the next read.
func (p *ipc) read() (byte, []byte, error) {
	var hdr [5]byte
	if _, err := io.ReadFull(p.r, hdr[:]); err != nil {
		return 0, nil, err
	}
	n := int(binary.LittleEndian.Uint32(hdr[:4]))
	if n < 1 || n > maxFrame {
		return 0, nil, fmt.Errorf("bad frame length %d", n)
	}
	// One big keyframe should not pin a big buffer for the rest of the session.
	if cap(p.buf) > 8<<20 && n < 1<<20 {
		p.buf = nil
	}
	if cap(p.buf) < n-1 {
		p.buf = make([]byte, n-1, n-1+n/4)
	}
	payload := p.buf[:n-1]
	if _, err := io.ReadFull(p.r, payload); err != nil {
		return 0, nil, err
	}
	return hdr[4], payload, nil
}

func (p *ipc) write(kind byte, parts ...[]byte) error {
	n := 1
	for _, b := range parts {
		n += len(b)
	}
	var hdr [5]byte
	binary.LittleEndian.PutUint32(hdr[:4], uint32(n))
	hdr[4] = kind

	p.wmu.Lock()
	defer p.wmu.Unlock()
	if _, err := p.w.Write(hdr[:]); err != nil {
		return err
	}
	for _, b := range parts {
		if _, err := p.w.Write(b); err != nil {
			return err
		}
	}
	return p.w.Flush()
}

// event is a control message to PdaNet. Fields that do not apply stay out of the JSON.
type event struct {
	Ev     string `json:"ev"`
	Code   string `json:"code,omitempty"`
	Reason string `json:"reason,omitempty"`
	Detail string `json:"detail,omitempty"`
	Peer   string `json:"peer,omitempty"`
	Path   string `json:"path,omitempty"`
	Local  string `json:"local,omitempty"`
	Remote string `json:"remote,omitempty"`
	Up     int    `json:"up,omitempty"`
	Total  int    `json:"total,omitempty"`
	Left   int    `json:"left,omitempty"`
	Wait   int    `json:"wait,omitempty"`  // seconds an easy access code takes no tries
	Tries  *tries `json:"tries,omitempty"` // how the easy access code stands, for PdaNet to keep
	Bps    int    `json:"bps,omitempty"`

	// Stats, sent about once a second while connected.
	RTT    float64 `json:"rtt,omitempty"`    // milliseconds
	Send   int     `json:"send,omitempty"`   // kbit/s
	Recv   int     `json:"recv,omitempty"`   // kbit/s
	Target int     `json:"target,omitempty"` // kbit/s the link can take, on the PC that shares
	Loss   float64 `json:"loss,omitempty"`   // percent
}

func (p *ipc) event(e event) {
	if b, err := json.Marshal(e); err == nil {
		_ = p.write(kindJSON, b)
	}
}
