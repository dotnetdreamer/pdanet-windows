package main

import (
	"fmt"
	"testing"
	"time"
)

// Include Pop after every packet, as receiveFrames does, so fragmented keyframes
// expose the receiver's actual assembly work rather than only its final copy.
func BenchmarkH264Frames(b *testing.B) {
	for _, size := range []int{40, 100_000, 1_000_000} {
		b.Run(fmt.Sprint(size), func(b *testing.B) {
			packets, data := parameterFrame(10, 9000, size)
			now := time.Now()
			frames := &h264Frames{}
			sequence := uint16(10)
			b.ReportAllocs()
			b.SetBytes(int64(len(data)))
			b.ResetTimer()
			for b.Loop() {
				for _, packet := range packets {
					packet.SequenceNumber = sequence
					sequence++
					frames.Push(packet, now)
					frames.Pop(now, 150*time.Millisecond)
				}
			}
		})
	}
}
