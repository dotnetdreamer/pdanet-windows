package main

import (
	"time"

	"github.com/pion/rtp"
	"github.com/pion/rtp/codecs"
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
	dropped            uint16
}

// repairWait leaves time for one retransmission, with a short deadline on fast links and
// a hard limit on how long remote input can appear frozen on a slow one.
func repairWait(rtt time.Duration) time.Duration {
	if rtt <= 0 {
		rtt = nackRTTGuess
	}
	return min(max(rtt*3/2+20*time.Millisecond, 150*time.Millisecond), 750*time.Millisecond)
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
	if lost {
		b.committed = true
		if b.dropped < 65535 {
			b.dropped++
		}
	}
}

func (b *h264Frames) updateSince() {
	b.since = time.Time{}
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
		b.advance(true)
	}
}

func (b *h264Frames) Push(p *rtp.Packet, now time.Time) {
	if !b.started {
		b.head, b.tail, b.started = p.SequenceNumber, p.SequenceNumber, true
	}
	d := int(int16(p.SequenceNumber - b.head))
	if d < 0 {
		// Before the first complete frame, its first packet can arrive after its fragments.
		// Once a frame was consumed, old packets are duplicates or expired retransmissions.
		if b.committed || int(uint16(b.tail-p.SequenceNumber)) > len(b.packets) {
			return
		}
		b.head = p.SequenceNumber
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
	}
	b.packets[slot] = p
}

func (b *h264Frames) Pop(now time.Time, wait time.Duration) *media.Sample {
	for b.head != b.tail {
		first := b.packet(b.head)
		if b.skip && first != nil {
			if first.Timestamp == b.skipTimestamp {
				b.discardFrame()
				b.updateSince()
				continue
			}
			b.skip = false
		}
		end, complete := b.head, false
		if first != nil {
			for end != b.tail {
				p := b.packet(end)
				if p == nil || p.Timestamp != first.Timestamp {
					break
				}
				end++
				if p.Marker {
					complete = true
					break
				}
			}
		}
		if !complete {
			if now.Sub(b.since) < wait {
				return nil
			}
			b.discardFrame()
			b.updateSince()
			continue
		}

		// A fresh depacketizer per frame prevents an incomplete FU-A from leaking into
		// the next frame after loss. Every packet through the marker must be present.
		dep := &codecs.H264Packet{}
		valid := dep.IsPartitionHead(first.Payload)
		if !valid && !b.committed && now.Sub(b.since) < wait {
			// The first packet observed can be the last fragment of the very first
			// frame. Leave its sequence interval open for the reordered start.
			return nil
		}
		fuActive := false
		var data []byte
		for seq := b.head; seq != end; seq++ {
			if !valid {
				break
			}
			payload := b.packet(seq).Payload
			if len(payload) >= 2 && payload[0]&0x1f == 28 {
				start, finish := payload[1]&0x80 != 0, payload[1]&0x40 != 0
				if (start && (fuActive || finish)) || (!start && !fuActive) {
					valid = false
					break
				}
				fuActive = !finish
			} else if fuActive {
				valid = false
				break
			}
			part, err := dep.Unmarshal(payload)
			if err != nil || len(data)+len(part) > maxFrame-5 {
				valid = false
				break
			}
			data = append(data, part...)
		}
		valid = valid && !fuActive
		if valid && !b.synced && !hasParameterSets(data) {
			// A single-NAL IDR marker can precede its SPS/PPS packets at startup.
			// Without a known preceding boundary, only a full decoder configuration
			// establishes a safe first picture; keep accepting reordered headers.
			if now.Sub(b.since) < wait {
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
