# Screen sharing performance audit

Audited on 6 November 2026. The goal is a responsive remote desktop: bounded delay,
immediate display of completed frames, clean recovery after packet loss, and sharp text
after motion stops. TeamViewer parity has **not** been established by these checks.

## Findings and changes

| Finding | Effect | Change |
| --- | --- | --- |
| The first keyframe was encoded before any capture succeeded. | A black opening picture, or an uninitialized Media Foundation input. | Capture a real desktop image before encoding the opening keyframe. Take the still-screen GDI snapshot on the first duplication timeout rather than waiting 200 ms. |
| Pion's playback sample builder waited for the next frame to start. | An extra frame interval of latency; an isolated final update could remain held. | Assemble H.264 access units at the RTP marker, after verifying packet continuity and fragment boundaries. Keep sender seal frames for older peers. |
| The RTC receive queue allowed 256 frames; the decoder queue allowed 120. | Seconds of stale pictures could build up on a busy viewer. | Bound the helper queue by count, bytes and age; bound the decoder queue to 8 frames, 16 MiB of payload and 100 ms of local waiting. Resume predicted video only from a clean keyframe after dropping a dependency. |
| A shared dropped-frame flag could be consumed by an older queued keyframe. | The decoder could continue through the actual gap with invalid references. | Attach the discontinuity to the first retained frame after the dropped dependency. Preserve a usable recent keyframe and its suffix when they fit. |
| Idle quality credit permitted a burst of motion frames faster than the link could carry them. | The simulated 1 Mbit/s link developed roughly 240–250 ms of motion delay on this desktop despite the 60–100 ms per-frame limits. | Track serialization backlog separately from the saved quality budget, including corrections for actual encoded sizes. |
| Dirty rectangles accumulated while the encoder waited for bandwidth, including on the GPU path. | Unbounded capture metadata and avoidable allocations during congestion. | Skip this list on the GPU path; collapse CPU metadata to one full-frame rectangle at the conversion threshold. |

Loss repair waits follow measured RTT, bounded to 150–750 ms (470 ms while RTT is
unknown), replacing the former two-second frame wait. This allows retransmission
on phone connections without giving fast links the same long recovery delay.

## Verification

Run these from the repository root, with .NET 9 and the Go version in `rtc/go.mod`:

```powershell
Push-Location rtc
go test ./...
go vet ./...
go test -run '^$' -bench '^BenchmarkH264Frames$' -benchtime=300ms
Pop-Location
dotnet build pdanet-windows/PdaNetPortable.csproj -c Debug
dotnet pdanet-windows/bin/Debug/net9.0-windows/PdaNetPortable.dll --selftest-share
dotnet pdanet-windows/bin/Debug/net9.0-windows/PdaNetPortable.dll --selftest-share quality
```

The app embeds a prebuilt RTC helper. After editing Go code, rebuild it with
`rtc/build.bat` before building the app so end-to-end tests exercise the new helper.

The deterministic checks cover queue overload, age, payload limits, prediction-chain
resets, disposal, bounded capture metadata, and sender serialization pacing. RTP
checks cover immediate final-frame delivery, packet reordering and retransmission,
sequence wrap, incomplete IDR frames, malformed fragmentation, and bounded storage.
The Windows self-test also exercises actual Desktop Duplication, GPU conversion,
Direct3D 12 and Media Foundation encoding, decoding, rotation, and a full LAN session.
Its viewer now subscribes before capture starts so the opening keyframe is exercised.

Validation passed: Go unit tests and vet, Go LAN/easy-access/TURN-over-TCP self-tests,
the .NET build with zero warnings, and all eight Windows sharing checks using the
rebuilt embedded helper. The packaged portable executable also passed all eight
checks, showing its first 1920×1080 LAN picture in 163 ms from startup/request
(the Debug run measured 220 ms). Go race-detector testing was unavailable because this
machine has no CGO C compiler.

On the audit PC (Intel UHD Graphics, 1920×1080), the initial baseline measured about
7 ms for a Direct3D 12 keyframe encode, 0.18 ms for GPU conversion and 18 ms for a
first decode. These are individual local samples, not steady-state frame times or
an input-to-display latency measurement.

One updated quality run on the same PC produced the following smooth-scrolling
results. These are averages over the one-second motion segment, with no real network:

| Simulated link | Preset | Motion frames in 1 s | Mean simulated motion delay | Motion Y PSNR |
| --- | --- | ---: | ---: | ---: |
| 1 Mbit/s | Balanced | 13 | 67 ms | 45.6 dB |
| 1 Mbit/s | Smooth | 21 | 41 ms | 44.7 dB |
| 2.5 Mbit/s | Balanced | 29 | 19 ms | 51.1 dB |
| 2.5 Mbit/s | Smooth | 55 | 12 ms | 48.7 dB |
| 6 Mbit/s | Balanced | 31 | 9 ms | 54.8 dB |
| 6 Mbit/s | Smooth | 60 | 7 ms | 53.1 dB |

The earlier audit run had different desktop content and reported 250/241 ms for
Balanced/Smooth at 1 Mbit/s. Treat that as evidence motivating the pacing regression
test, rather than a controlled percentage improvement. The deterministic test
fails with the original planner and passes with the serialization fix.

The marker assembler's microbenchmark, including a `Pop` attempt after every RTP
packet, measured about 0.15 ms per 100 KB synthetic access unit and 1.65 ms per
1 MB access unit on this PC. It reuses one receiver as a session does. This checks
assembly overhead only; it excludes encryption, network and decoder work.

The quality command encodes and decodes real frames over a **simulated fixed-rate
link**. It uses the current desktop, so different runs can contain different content.
Its latency includes serialization and receiver frame release, but excludes capture,
codec time, network propagation, jitter, packet loss and bandwidth-estimator behavior.
The historical rows compare the former fixed-bitrate encoder and playback receiver
with the current planner and marker receiver; they do not isolate one change.

## Remaining acceptance work

- Measure input-to-display latency (median and p95), delivered FPS, text readability,
  CPU/GPU load and memory on two physical PCs. Use typing, scrolling, window dragging,
  video and idle desktop workloads. Run TeamViewer on the same machines and network
  with the same workload to make a valid comparison.
- Test direct LAN, WAN, TURN, phone USB/WiFi, packet loss, jitter and changing bandwidth.
  Include 1, 2.5 and 6 Mbit/s; high RTT phone paths; 1080p and 4K; and Intel, NVIDIA,
  AMD and software-codec combinations. Confirm recovery also works when motion stops
  immediately after a lost packet.
- Feed decoder capacity back to the sender. The new queue limits keep local backlog
  bounded, but a viewer that cannot decode the offered frame rate can still require
  repeated keyframes. Queue limits do not replace sender adaptation.
- Profile presentation. The viewer still copies decoded NV12 pixels through CPU
  conversion and a WPF bitmap. A measured GPU presentation path may be the next
  substantial CPU improvement, especially at 4K.
- Cursor polling still shares the host capture/encode thread. A stalled encoder or
  IPC write can delay pointer telemetry; test this separately from video motion.

Keyframe backoff remains in place to avoid saturating lossy links with repeated
large IDR frames. A 100 ms decoder queue budget bounds queued work, not total
network latency or the time required to obtain a replacement keyframe.
