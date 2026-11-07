package main

import (
	"encoding/binary"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4/pkg/media"
)

// h264Frames reorders packets and emits a complete access unit at its marker. Unlike a
// playback sample builder it does not need the following frame to compute a duration:
// the viewer uses the RTP timestamp, and must show the last change on an idle desktop.
type h264Frames struct {
	packets            [4096]*rtp.Packet
	arrivals           [4096]time.Time
	head, tail         uint16 // the pending sequence interval, including holes
	started, committed bool
	synced             bool // a complete frame supplied both decoder parameter sets
	skip               bool
	skipTimestamp      uint32
	since              time.Time
	progress           time.Time // latest arrival extending this frame's contiguous prefix
	seen               time.Time // latest arrival of a packet of this frame, past a hole too
	dropped            uint16
	scan               uint16 // first packet not yet checked for this head's marker
	parity             [4096]int32 // sequence number + 1 of slots that hold parity, see fecReceiver
}

// notMedia marks n sequence numbers from from on as parity: the frame before them is whole
// without them, and their loss loses nothing.
func (b *h264Frames) notMedia(from uint16, n int) {
	for i := 0; i < n && i < len(b.parity); i++ {
		s := from + uint16(i)
		b.parity[int(s)%len(b.parity)] = int32(s) + 1
	}
}

func (b *h264Frames) isParity(seq uint16) bool {
	return b.parity[int(seq)%len(b.parity)] == int32(seq)+1
}

// repairWait leaves time for one retransmission, with a short deadline on fast links and
// a hard limit on how long remote input can appear frozen on a slow one.
func repairWait(rtt time.Duration) time.Duration {
	if rtt <= 0 {
		rtt = nackRTTGuess
	}
	return min(max(rtt*3/2+20*time.Millisecond, 150*time.Millisecond), 750*time.Millisecond)
}

// Loss repair starts at the frame's latest new packet, not the first fragment of a
// frame that is still being paced onto the link. While packets of the frame still
// arrive past a hole, the link is still busy with it, and the packet asked for again
// waits behind them in the slowest hop's queue: timed from the packet before the hole,
// a keyframe that filled that queue gave up its own repairs, and the keyframe asked
// for next did the same (2 % loss on 4 Mbit/s in the network lab froze the picture
// for 16 to 22 s of 35, 2026-11-07). Duplicates and later frames cannot prolong the
// wait, and even continuous progress has a hard age limit, so an unterminated frame
// cannot hold the desktop forever.
func (b *h264Frames) deadline(wait time.Duration) time.Time {
	if b.head == b.tail {
		return time.Time{}
	}
	from := b.since
	if b.progress.After(from) {
		from = b.progress
	}
	if b.seen.After(from) {
		from = b.seen
	}
	deadline := from.Add(wait)
	if limit := b.since.Add(frameWait); deadline.After(limit) {
		deadline = limit
	}
	return deadline
}

func (b *h264Frames) packet(seq uint16) *rtp.Packet {
	p := b.packets[int(seq)%len(b.packets)]
	if p != nil && p.SequenceNumber == seq {
		return p
	}
	return nil
}

func (b *h264Frames) advance(lost bool) {
	b.packets[int(b.head)%len(b.packets)] = nil
	b.arrivals[int(b.head)%len(b.packets)] = time.Time{}
	b.head++
	b.scan = b.head
	b.progress, b.seen = time.Time{}, time.Time{}
	if lost {
		b.committed = true
		if b.dropped < 65535 {
			b.dropped++
		}
	}
}

func (b *h264Frames) updateSince() {
	b.since = time.Time{}
	b.progress, b.seen = time.Time{}, time.Time{}
	for seq := b.head; seq != b.tail; seq++ {
		if b.packet(seq) == nil {
			continue
		}
		at := b.arrivals[int(seq)%len(b.packets)]
		if b.since.IsZero() || at.Before(b.since) {
			b.since = at
		}
	}
}

// Once repair expires, discard the entire damaged access unit. A later slice can
// also be an IDR NAL, but cannot safely stand in for the missing part of a keyframe.
func (b *h264Frames) discardFrame() {
	var timestamp uint32
	known := false
	for b.head != b.tail {
		if p := b.packet(b.head); p != nil {
			if known && p.Timestamp != timestamp {
				return
			}
			timestamp, known = p.Timestamp, true
			b.skip, b.skipTimestamp = true, timestamp
		}
		b.advance(!b.isParity(b.head))
	}
}

func (b *h264Frames) Push(p *rtp.Packet, now time.Time) {
	if !b.started {
		b.head, b.tail, b.started = p.SequenceNumber, p.SequenceNumber, true
		b.scan = b.head
	}
	d := int(int16(p.SequenceNumber - b.head))
	if d < 0 {
		// Before the first complete frame, its first packet can arrive after its fragments.
		// Once a frame was consumed, old packets are duplicates or expired retransmissions.
		if b.committed || int(uint16(b.tail-p.SequenceNumber)) > len(b.packets) {
			return
		}
		b.head = p.SequenceNumber
		b.scan = b.head
	} else if d >= len(b.packets) {
		// Never retain more than one retransmission window, even if no marker arrives.
		for seq := b.head; seq != b.tail; seq++ {
			if old := b.packet(seq); old != nil {
				b.skip, b.skipTimestamp = true, old.Timestamp
				break
			}
		}
		for d >= len(b.packets) {
			if b.head == b.tail {
				b.tail++
			}
			b.advance(true)
			d--
		}
	}
	if b.head == b.tail {
		b.since = now
	}
	if int16(p.SequenceNumber-b.tail) >= 0 {
		b.tail = p.SequenceNumber + 1
	}
	slot := int(p.SequenceNumber) % len(b.packets)
	if b.packet(p.SequenceNumber) == nil {
		b.arrivals[slot] = now
	} else {
		// A replacement must be checked again, even if a caller supplies a changed duplicate.
		b.scan = b.head
	}
	b.packets[slot] = p
}

func (b *h264Frames) Pop(now time.Time, wait time.Duration) *media.Sample {
	for b.head != b.tail {
		if b.isParity(b.head) && b.packet(b.head) == nil {
			// Parity after the frame before, kept or lost: nothing of the screen is missing.
			b.parity[int(b.head)%len(b.parity)] = 0
			b.advance(false)
			continue
		}
		first := b.packet(b.head)
		if b.skip && first != nil {
			if first.Timestamp == b.skipTimestamp {
				b.discardFrame()
				b.updateSince()
				continue
			}
			b.skip = false
		}
		end, complete := b.scan, false
		if first != nil {
			for end != b.tail {
				p := b.packet(end)
				if p == nil || p.Timestamp != first.Timestamp {
					break
				}
				if at := b.arrivals[int(end)%len(b.packets)]; at.After(b.progress) {
					b.progress = at
				}
				end++
				if p.Marker {
					complete = true
					break
				}
			}
		}
		b.scan = end
		if !complete {
			b.seen = b.frameSeen(first, end)
			if now.Before(b.deadline(wait)) {
				return nil
			}
			b.discardFrame()
			b.updateSince()
			continue
		}

		// Fragment state belongs to this access unit, so a lost FU-A cannot leak into
		// the next frame. Every packet through the marker must be present.
		valid := h264PartitionHead(first.Payload)
		if !valid && !b.committed && now.Sub(b.since) < wait {
			// The first packet observed can be the last fragment of the very first
			// frame. Leave its sequence interval open for the reordered start, and
			// look at the whole frame again next time.
			b.scan = b.head
			return nil
		}
		var data []byte
		if valid {
			data, valid = b.assemble(end)
		}
		if valid && !b.synced && !hasParameterSets(data) {
			// A single-NAL IDR marker can precede its SPS/PPS packets at startup.
			// Without a known preceding boundary, only a full decoder configuration
			// establishes a safe first picture; keep accepting reordered headers.
			if now.Sub(b.since) < wait {
				b.scan = b.head
				return nil
			}
			valid = false
		}
		for b.head != end {
			b.advance(!valid)
		}
		b.committed = true
		b.updateSince()
		if valid && len(data) > 0 {
			b.synced = true
			s := &media.Sample{Data: data, PacketTimestamp: first.Timestamp, PrevDroppedPackets: b.dropped}
			b.dropped = 0
			return s
		}
	}
	return nil
}

// frameSeen is the latest arrival of a packet of the frame at head from seq from on, past
// holes: of first's frame, or without first of the frame of the next packet there is.
func (b *h264Frames) frameSeen(first *rtp.Packet, from uint16) time.Time {
	var last time.Time
	ts, known := uint32(0), first != nil
	if known {
		ts = first.Timestamp
	}
	for seq := from; seq != b.tail; seq++ {
		p := b.packet(seq)
		if p == nil {
			continue
		}
		if !known {
			ts, known = p.Timestamp, true
		}
		if p.Timestamp != ts {
			break
		}
		if at := b.arrivals[int(seq)%len(b.packets)]; at.After(last) {
			last = at
		}
	}
	return last
}

func h264PartitionHead(payload []byte) bool {
	return len(payload) >= 2 && (payload[0]&0x1f != 28 || payload[1]&0x80 != 0)
}

// Validate and size the access unit before allocating it. Pion's general-purpose
// depacketizer grows a fragment buffer, copies that into a NAL, then copies the NAL
// into the frame. Large desktop keyframes need just one allocation and one payload
// copy here. Packet storage and the returned frame keep independent ownership.
func (b *h264Frames) assemble(end uint16) ([]byte, bool) {
	a := annexBFrame{}
	for seq := b.head; seq != end; seq++ {
		if !a.add(b.packet(seq).Payload) {
			return nil, false
		}
	}
	if a.fragment || a.size == 0 {
		return nil, false
	}
	a = annexBFrame{data: make([]byte, 0, a.size)}
	for seq := b.head; seq != end; seq++ {
		a.add(b.packet(seq).Payload)
	}
	return a.data, true
}

// RFC 6184 packetization-mode=1: single NAL, STAP-A and FU-A. Empty FU
// payloads are valid. A fragmented NAL must keep its type and NRI throughout.
type annexBFrame struct {
	data     []byte // nil while counting, preallocated while copying
	size     int
	fragment bool
	header   byte
}

func (a *annexBFrame) append(p []byte) bool {
	if len(p) > maxFrame-5-a.size {
		return false
	}
	a.size += len(p)
	if a.data != nil {
		a.data = append(a.data, p...)
	}
	return true
}

func (a *annexBFrame) nal(p []byte) bool {
	if len(p) == 0 || p[0]&0x1f == 0 || p[0]&0x1f >= 24 {
		return false
	}
	return a.append([]byte{0, 0, 0, 1}) && a.append(p)
}

func (a *annexBFrame) add(p []byte) bool {
	if len(p) == 0 {
		return false
	}
	switch typ := p[0] & 0x1f; {
	case typ > 0 && typ < 24:
		return !a.fragment && a.nal(p)
	case typ == 24:
		if a.fragment || len(p) < 4 {
			return false
		}
		for rest := p[1:]; len(rest) > 0; {
			if len(rest) < 2 {
				return false
			}
			n := int(binary.BigEndian.Uint16(rest))
			rest = rest[2:]
			if n > len(rest) || !a.nal(rest[:n]) {
				return false
			}
			rest = rest[n:]
		}
		return true
	case typ == 28:
		if len(p) < 2 {
			return false
		}
		start, finish := p[1]&0x80 != 0, p[1]&0x40 != 0
		header := p[0]&0xe0 | p[1]&0x1f
		if header&0x1f == 0 || header&0x1f >= 24 || start && finish {
			return false
		}
		if start {
			if a.fragment || !a.nal([]byte{header}) {
				return false
			}
			a.fragment, a.header = true, header
		} else if !a.fragment || header != a.header {
			return false
		}
		if finish {
			a.fragment = false
		}
		return a.append(p[2:])
	}
	return false
}

func hasParameterSets(data []byte) bool {
	var sps, pps bool
	for i := 0; i+3 < len(data); i++ {
		if data[i] == 0 && data[i+1] == 0 && data[i+2] == 1 {
			switch data[i+3] & 0x1f {
			case 7:
				sps = true
			case 8:
				pps = true
			}
			i += 2
		}
	}
	return sps && pps
}
