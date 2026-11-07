# Screen sharing performance audit

Audited on 6 and 7 November 2026. The goal is a responsive remote desktop: bounded delay,
immediate display of completed frames, clean recovery after packet loss, and sharp text
after motion stops. TeamViewer parity has **not** been established by these checks.

## Third pass, 7 November evening: the v3 follow-up, loss, the estimate, GPU display

### Audit of the follow-up (v3)

| Claim | Verdict |
| --- | --- |
| Go tests/vet and all 11 sharing checks pass | Reproduced. |
| Protection against false bandwidth samples | The ratchet it reproduced is real, but the fix starves the meter. It drops a burst when a quarter of its paced packets arrive in the same 250 µs tick, and Wi-Fi and relays deliver that way. Its own live relay run accepted 0 of 26 bursts: GCC alone, 548 kbit/s delivered. Here, two live runs of v3 delivered 633 and 847 kbit/s against 1.1–1.7 Mbit/s for the code below. The emulator does not batch arrivals, so it hides this. |
| Paced traffic measurable at low bitrates | **Correct, kept.** At a 200 kbit/s target the pacer spaces packets 19 ms apart, and a 4 ms burst gap made every packet its own burst. Nothing could be measured, so once the floor was lost it never came back: the 200 kbit/s trap in the morning's logs. The one-packet idle allowance is kept as well: equal or slightly better in the lab, and no queue spike that GCC reads as overuse. Its cost: at a 200–500 kbit/s target a 12 KB frame waits 52–167 ms in the pacer before it reaches the network. |
| Complete-row colour test; lab metric windows; keyframe requests over the real data channel | Correct, kept. Every comparison below uses that corrected lab. |
| (Second pass) "The real two-PC logs never showed burst readings above 4 Mbit/s" | **Wrong.** The 6 November 23:24–23:32 session with the laptop, through the relay, read 16–64 Mbit/s bursts. The target ran to 15–30 Mbit/s while about 2 Mbit/s went through. That build predated the bunching guard (`e3e3e0a`, 7 November 03:14), which is why the morning sessions showed at most 4 Mbit/s. The guard hid the problem; it did not fix the measurement. |

### Changes in this pass

| Change | Why |
| --- | --- |
| The meter times a burst from when its first packet *could* have arrived (when it left, plus the link's lowest delay) to when its last packet did. Bunching no longer rejects bursts. | Arrival times only ever come late: Wi-Fi batches, relays and tunnels hold packets, a busy PC reads them late. Timed from the first arrival, lateness made bursts look up to 30× faster than the link. Timed from the sender's clock, it can only make them look slower. |
| …and no faster than the burst's second half shows, timed by its own arrivals. | A token bucket lets a burst's first bytes through at once. In the lab (3 KB bucket), 8-packet frames read 3.1–3.9 Mbit/s on 3 Mbit/s, so the floor sat at the link's own rate and every halving overflowed: 11–15 fps and 7.7–9.2 s frozen on the "varying" path. With the check: 26.6–27.1 fps, 0 s frozen. Each timing covers what fools the other. |
| Sanity bound: the estimate at most doubles every 500 ms (`growTime`), and falls at once. | The 8-burst ratchet went from 2 to 88 Mbit/s in 120 ms. Now 120 ms allow at most 1.2×, and those bursts are too short to time anyway. A real 30 Mbit/s link is reached in about 2 s. |
| Forward error correction (`rtc/fec.go`): Reed-Solomon parity after each frame (`klauspost/reedsolomon`), rebuilt on the viewer as soon as a group has enough packets, with no round trip. | Under 2 % random loss each lost packet held every later frame for a round trip, and lost keyframes froze the picture for seconds. |
| FEC policy: on at ≥ 0.3 % loss, off after 10 s below 0.1 %. Parity share 3 × loss + 5 % (10–50 %), within the bitrate: the encoder's target is reduced by what the parity costs. | Most paths, including this relay most of the time, lose nothing, so no parity goes out. Logged as "parity: …" on the sharing PC and "N packets rebuilt from parity" on the viewer. |
| Every packet of a protected frame carries a header extension (`urn:x-pdanet:fec-01`): its place in the frame, and the frame's data and parity counts. It is negotiated in SDP. | The viewer knows which sequence numbers hold parity even when they are lost, so the assembler never waits for them and the NACK generator never asks for them. NACKs also stop for rebuilt packets. With an older PdaNet on either side, the extension is not negotiated and nothing changes. |
| Resend buffer 4096 → 8192 packets. | Parity adds up to 50 % more packets; `frameWait` (2 s) must still fit. |
| A frame of more than `fecMaxFrame` (8192) packets goes entirely without parity. | Splitting it would put parity in the middle of a frame, which the assembler would wait on as holes. |
| Viewer GPU presenter (`SwapChainPresenter`, `GpuPictureHost`): a D3D11 flip-model swap chain in a mouse-transparent child window, fed by its own thread straight from the decoder. NV12 → BGRA runs in a shader, byte-identical to `ColorConvert`, placed where WPF lays the picture out (fit, actual size, scroll, rotation, DPI). | No more UI-thread conversion, 8 MB bitmap re-upload, dispatcher hop or WPF render pass per picture. WPF still draws whenever something of its own covers the picture: connecting or reconnecting overlays, the close question, watch-only (banner and remote pointer), the fullscreen strip, minimized windows. It also draws after a GPU failure and with `PDANET_VIEWER_GPU=0`. |
| Fullscreen no longer keeps the window's 40 px caption and resize border. | Pre-existing bug, found while testing the presenter. In fullscreen, clicks in the top 40 px of the other PC's screen moved the window instead of reaching it, and the pointer could never bring the strip down. |

### Measurements

Emulated paths, corrected lab (`TestNetLab`), 40 s each after 5 s of warmup, one profile at a
time, two rounds for v2 and v3. Each cell is delivered rate, motion fps and frozen time.

| Path | v2 (second pass) | v3 (follow-up) | This pass |
| --- | --- | --- | --- |
| 3 Mbit/s, 190 ms RTT | 1654–1777k, 27.5–27.6, 0 s | 1389–1716k, 26.5–27.4, 0 s | 1540k, 27.7, 0 s |
| 1.2 Mbit/s, 280 ms RTT | 499–512k, 27.3–27.4, 0 s | 575–593k, 27.3, 0 s | 565k, 27.3, 0 s |
| **4 Mbit/s, 2 % random loss** | 1376–2162k, 12.7–22.7, **3.4–10.4 s** | 1786–1812k, 21.0–24.0, **2.0–4.2 s** | 1589k, 26.8, **0 s** (p50 86 ms vs 150–164) |
| 20 Mbit/s, 24 ms RTT | 8162–8475k, 22.7–23.0, 3.5–4.2 s | 7131–7626k, 28.4–28.7, 0 s | 8613k, 28.6, 0 s |
| 3 Mbit/s, 80 ms jitter | 1655–1782k, 27.1–27.2, 0 s | 1569–1636k, 27.0–27.3, 0 s | 1727k, 26.6, 0 s |
| 3 Mbit/s, halved 4 s in every 8 | 932–942k, 26.0–27.1, 0 s | 944–972k, 26.9–27.1, 0 s | 1009k, 26.6, 0 s |
| **400 ms RTT, 1 % loss** | 1590–1774k, 17.3–23.9, **1.3–8.1 s** | 1492–1607k, 18.9–20.8, **4.9–5.5 s** | 1476k, 25.7, **1.2 s** (p50 235 ms vs 426–526) |

The same code with the 16-packet idle allowance gave matching results (2 % loss: 27.0 fps,
0 s; 400 ms: 25.7 fps, 0.6 s), except one overflow episode at 20 Mbit/s (3.7 s); at that rate
both allowances run the same code. With parity switched off, the new meter alone gave 19.0 fps
and 6.0 s frozen at 2 % loss. The gain under loss is the parity.

`TestRelayScreenLab`: the real TURN relay, both peers on this PC, 45 s each, alternating:

| | Delivered | Motion fps | Frozen |
| --- | --- | --- | --- |
| v2 | 2021 / 1682 kbit/s | 20.5 / 28.0 | 5.6 / 0 s |
| v3 | 633 / 847 kbit/s | 28.0 / 28.3 | 0 / 0 s |
| This pass | 1701 / 1111 kbit/s | 21.7 / 28.5 | 4.4 / 0 s |

The freezes in the first round came from bursts of near-total loss on the loop (51.9 % and
24.4 % of the packets in 2 s windows); no parity covers that. GCC rated the path at 5.6
Mbit/s right after. v3 avoided them only by sending a third to half as much.

### Verification

- Go: vet, and all tests including the new FEC, meter and NACK tests. FEC rebuilds every lost data packet whenever a group loses no more than its parity, from a 1-packet frame to a 1 MB keyframe across 5 groups; lost parity never holds a frame up.
- Builds: Debug and Release with 0 warnings.
- Self-tests: all 12 sharing checks pass in Debug and in the packaged Release. New: the GPU presenter, with all 16.7 M Y/U/V combinations byte-identical to `ColorConvert` and placement equal to WPF's layout.
- Relay check: the packaged capture-to-decode relay check passed, first 1920×1080 picture 333–477 ms after asking.
- GUI (two instances on this PC, real input):
  - The GPU path engages ("the GPU shows the pictures, a Direct3D 11 swap chain") and hands back to WPF for the close question, the fullscreen strip and minimize.
  - Move, click, capture and wheel reach the input layer through the GPU window.
  - In fullscreen the top edge drops the strip, its buttons work, and a drag at y = 20 no longer moves the window.
  - After fullscreen, the title bar, edge resize, maximize and restore behave as before.
- App-wide: the MCP smoke suite passes 146 checks with 0 failures; its 4 relay checks skip while another PdaNet holds the agent pipe.
- Final code, re-run on a quiet PC: 20 Mbit/s path 28.7 fps, 0 s frozen, p50 39 ms; 3 Mbit/s relay-like path 27.7 fps, 0 s frozen.
- Build: `dist/screen-sharing-v4/PdaNetPortable.exe`, SHA256 `4EF458EE1C30F8A8A038282FB51243992C24768EF6A700A41FA3B014C8A9A540`. Use it on both PCs: parity needs both ends to be this version.

### Still open

- No two-PC measurement and no TeamViewer comparison; every live number above loops through one PC.
- The GPU presenter's latency saving follows by construction, not from a photon measurement. Watch-only sessions always use WPF.
- A burst sent faster than a shaper's burst allowance still reads above the sustained rate. The queue and loss gates catch it, and the doubling bound slows the return.
- Loss bursts of 25–50 % (seen on the relay loop) still freeze for seconds. A long-term reference instead of a fresh keyframe would shorten the recovery.

## Second pass, 7 November afternoon: what the relay sessions showed

The relay sessions logged on the sharing PC (`dist/data/pdanet.log`, this PC to the
laptop through the Ludo relay, after the coturn cap was gone) had a 180–490 ms round trip
and almost no loss. Yet the encoder target sat at the 200 kbit/s floor for minutes. The
picture went at 1–14 frames a second with 1–5 keyframes every 10 s. The morning's changes
did not touch any of this. Two separate faults combined:

1. **The bitrate collapsed.** The link meter read each burst's delay above the 10 s minimum
   as a standing queue. Jitter alone crossed its 60 ms limit every second or two ("a queue
   builds up … lost 0.0 %"), so the floor was switched off and its measurements forgotten.
   GCC then cut to 0.85 × the rate received over the last 500 ms, which for a screen that
   pauses between frames is mostly pause. It climbed back by about 8 % a second.
2. **The planner held motion at about 10 frames a second.** While motion lasted, each frame
   kept spending the credit saved during rest (up to `MotionMax`, 0.1 s of the link). The
   wait to drain each frame earned the same credit back, so it never ran out. The result
   was sharp frames (QP 16–29 in the log) at 10–14 fps where the link carried 30 at the
   soft quantizer.

### Audit of the morning's claims

| Claim | Verdict |
| --- | --- |
| Opening keyframe lost before DTLS | Correct: Pion's `srtpWriterFuture` returns `(0, nil)` before `srtpReady`. **But the fix regressed reconnects**: after an ICE restart (connected → connecting → connected, no disconnect) "connected" was never announced again, so a viewer that showed "reconnecting" could stay so. Fixed; `TestICERestartAnnouncesConnectedAgain` fails on the morning code. |
| Intact slow keyframes discarded | Correct, and the fix is right for intact frames. A related case remained: a lost packet *inside* a frame still arriving. Its resend queues behind the rest of the frame at the slowest hop, but the deadline ran from the packet before the hole. Extended, see below. |
| Cursor polling off the capture thread | Correct (stress-tested 3000 start/stop/dispose cycles). `HostPipeline.Dispose` can now take up to 6 s on its background thread. |
| Ludo bandwidth cap removed | Accurate, but it was removed on 6 Nov (ludo `dceb78ed`), not in the morning's change set. |
| 1080p conversion 3.83 → 0.38 ms; 4K 16.86 → 2.64 ms | Reproduced in the packaged Release: 3.58 → 0.39 ms and 14.6 → 2.9 ms. Bit-exact on AVX2 and on SSE4.1 (AVX2 disabled), no out-of-bounds access with guard pages. The self-test only runs the path the CPU picks. Separately, the row split for the parallel loops (pre-existing) **left the bottom rows unconverted** whenever h/bands rounds down: 1200 rows on 18+ logical CPUs gave a 12-row black strip on the viewer, and 1050-row screens lost 2 rows on the host's CPU path. Both fixed. |
| 1 MB assembly 1.71 → 0.18 ms, 1.01 MB | Reproduced: 0.178 ms, 1.01 MB, 2 allocations. |
| All 11 checks pass; relay pipeline passes | Reproduced, Debug and packaged. |
| 59.8 fps, 67 / 161 ms through the relay | Measured, but not a screen-sharing result. At the final ~600 kbit/s target each synthetic 60 fps frame was ~1.1 KB, a single packet. |

### Changes in this pass

| Change | Why |
| --- | --- |
| The paced portion is limited to 1.5 × the measured link, and nearer the link the more is waiting (the paced-tail budget aims to add ~30 ms to the network queue). The initial unpaced token allowance remains separate. | 2.5 × the floor (0.8 of the link) meant bursts at twice the link. A large keyframe overflowed a router's buffer; its repairs came too late, and the next keyframe did the same. On a clean 20 Mbit/s path this cost 53 keyframe requests and 3.6 s frozen in 35 s. |
| A queue counts as standing only if every burst over the last 400 ms met it (at least 3 bursts over at least 200 ms), or if 3 in a row each met over 120 ms. | Jitter delays some bursts and not the next; a real queue delays all. Single packets were their own "bursts", so 3 in a row was not enough. |
| Loss repair waits while packets of the same frame are still arriving; duplicates and later frames still cannot extend it; 2 s hard limit. | With 2 % loss, keyframes gave up their own repairs: 13–22 s frozen of 35. |
| FramePlanner: a motion's later frames (≤ 250 ms apart) take their per-frame share; only the first after a rest spends saved credit. | ~10 fps → ~30 fps for the same link. New self-test fails at 15 fps without it. |
| Viewer decode queue 8 → 64 frames and 100 → 250 ms; helper queue 16 → 64 and 150 → 250 ms. | A resend releases every frame held behind it at once. At 30 fps and a 400 ms round trip, 8 frames turned each repair into a keyframe request (measured below). |
| ColorConvert row split rounded up; `scan` reset before startup holds in the assembler. | Bugs above. |

### Measurements

`TestNetLab` (`rtc/netlab_test.go`) runs the real peers, pacer, meter, GCC, NACK and frame
assembly over Pion's virtual network: a bottleneck with a buffer towards the viewer, delay
both ways, and in-order delay jitter. The vnet router's own jitter blocks senders, so it is
not used. The workload models FramePlanner (bursty keyframe, saved credit, motion, refines)
and the viewer models ViewerFrameQueue. These historical runs were 40 s, one at a
time. Target, motion FPS and frozen-time measurements excluded the first 5 s;
payload rate and latency percentiles included the whole trial. The historical
freeze calculation could also count idle portions of a gap. The follow-up below
corrects the metric definitions for future runs; these numbers are retained as
original observations rather than recomputed results.

| Path | Morning code: delivered, motion fps, p50/p95, frozen | This pass |
| --- | --- | --- |
| relay 3 Mbit/s, 190 ms RTT | 1556 kbit/s, 9.6, 179/201 ms, 0 s | 1609 kbit/s, **27.5**, **131**/183 ms, 0 s |
| slow 1.2 Mbit/s, 280 ms | 482, 9.4, 194/253, 0 | 533, **27.3**, 164/179, 0 |
| lossy 4 Mbit/s, 2 % loss | 1386, 6.3, 212/340, 13.1 s | **1903**, **18.7**, 141/369, **7.7 s** |
| fast 20 Mbit/s, 24 ms | 3032, 5.4, 45/92, 3.7 s | **8828**, **28.6**, 40/44, **0 s** |
| jittery 3 Mbit/s, +0–80 ms | 1367, 9.4, 205/261, 0 | 1427, **27.2**, 175/288, 0 |
| capacity halving every 8 s | 781, 9.5, 137/400, 0 | 922, **27.3**, 117/422, 0 |

At 400 ms round trip and 1 % loss, with 30 fps motion, the 8-frame viewer queue gave 3–4 fps
and 20–26 s frozen of 35 (8 queue resets). The 64-frame queue gave 16–23 fps and 2.5 s frozen
(longest 0.7 s).

`TestRelayScreenLab` runs the same workload through the real TURN relay, both peers on this
PC and forced onto it (relay to relay, UDP). It still uses synthetic frames and
a simulated decoder; these historical runs also simulated viewer keyframe
requests with a 60 ms timer instead of the real control channel. They are not
actual screen-sharing FPS or input-to-display measurements. Two alternating runs each:

| | Delivered | Motion fps | p50 / p95 | Keyframes (PLI asks) | Frozen (longest) |
| --- | --- | --- | --- | --- | --- |
| Morning code | 1489 / 912 kbit/s | 2.5 / 8.7 | 85/402, 72/214 ms | 13 (32) / 4 (2) | 11.8 s (3.5) / 1.6 s (1.6) |
| This pass | 1559 / 2469 kbit/s | 22.6 / 21.9 | 65/164, 65/226 ms | 8 (7) / 6 (11) | 3.7 s (1.7) / 1.9 s (1.9) |

```powershell
Push-Location rtc
$env:PDANET_NETLAB = 'all'               # or relay,slow,lossy,fast,jittery,varying,far
go test -run '^TestNetLab$' -count=1 -v
$env:PDANET_LAB_FROM = 'C:\path\to\relay-endpoint.txt'   # file with the https credential address
go test -run '^TestRelayScreenLab$' -count=1 -v
Pop-Location
```

Verification: Go vet and tests (new: pacer, meter, repair, ICE restart), the Debug build
with 0 warnings, all 11 sharing checks in Debug and in the packaged Release, and the real
capture-to-decode relay check (first 1920×1080 picture 258 ms after asking). The build is
`dist/screen-sharing-v2/PdaNetPortable.exe`, SHA256
`3D389337CEBF8554CA27708CF53B7BD3DA5B18EF34F19E3435EED87B753799FC`; use it on both PCs.

### Still open

- No two-PC measurement and no TeamViewer comparison yet; the relay lab loops back through one PC.
- On that loopback the meter's bursts read 90–140 Mbit/s (arrivals bunch on one PC). Each sample could double the estimate, so the target ran to the 30 Mbit/s ceiling and oscillated. ~~The real two-PC logs never showed burst readings above 4 Mbit/s.~~ *Wrong, see the third pass: the 6 November 23:24 session with the laptop read 16–64 Mbit/s.* The follow-up below adds a measurement-validity check for this case.
- Random 2 % loss still freezes for seconds at a time: forward error correction or a long-term reference instead of a fresh keyframe would be the next step.
- Display still goes through a CPU conversion and a WPF bitmap. GPU presentation needs profiling; no latency saving has been measured yet.

## Follow-up verification, 7 November

The actual-session log confirms that the morning's component work did not solve
the central relay problem: repeated 200 kbit/s targets and 1–14 fps despite mostly
zero reported loss. The second pass's reconnect, jitter, planner, pacing and
queue fixes are retained. Its real ICE-restart regression passes, as do the
startup and repaired-frame tests. The earlier 59.8 synthetic FPS result remains
a transport observation, not proof of screen-sharing performance.

The remaining burst-estimate failure was reproduced with eight short batches:
24 packets sent 100 microseconds apart and reported in four-packet arrival
batches could raise the estimated capacity to 88.32 Mbit/s in about 120 ms.
The old batching guard counted no suspicious boundaries because each send gap
was below 500 microseconds. The meter now requires at least three actual paced
boundaries, excludes the initial unpaced token allowance from its rate sample,
and judges arrival bunching against those paced boundaries. It rejects this
false sample without imposing a fixed bandwidth cap. Tests still measure a
genuine approximately 20 Mbit/s link with the real pacer's two-millisecond
packet clusters, and verify that a 24 Mbit/s paced tail is not inflated to
36 Mbit/s by its initial free allowance.

Live trials then exposed a separate low-rate learning trap in both the previous
and stricter meters: the fixed four-millisecond burst gap split normally paced
packets into separate bursts, and the minimum 19.2 KB idle allowance consumed
most of a small keyframe before it could supply pacing evidence. A diagnostic
trial found 342 size-qualified bursts but no accepted capacity sample; 290
failed the pacing test. The burst gap now allows a full packet's serialization
time at the current encoder target, and the minimum idle allowance is one MTU
while retaining the rate-based 25 ms credit. This preserves measurable paced
tails at low rates instead of weakening the false-sample rejection.

The color self-test now checks complete rows against independent black/white
expected values in both conversion directions, with poisoned buffers and
padding guards. Comparing SIMD with scalar alone could miss identical skipped
rows because both paths share the partitioning. Restoring the old arithmetic
fails at row 1048 of 1050 on the host and row 1188 of 1200 on the viewer.
The corrected conversion passes with 1, 4, 16, 18 and 32 reported processors.

The model-based network lab now sends viewer keyframe requests over the real
reliable data channel. Future payload and latency statistics use the same
post-warmup window as motion FPS. Frozen time now counts only continuous motion
segments with a gap of at least 500 ms; idle rest periods cannot inflate it,
and a gap beginning during rest no longer hides a later motion freeze. Seven
offline/control checks protect these definitions. Historical tables above
retain their original metric definitions and must not be treated as a paired
comparison with the corrected harness.

Final candidate checks with the corrected model harness, one 40 s observation
per path (first 5 s excluded), produced:

| Path | Delivered payload | Motion FPS | p50 / p95 | Motion frozen |
| --- | ---: | ---: | ---: | ---: |
| Emulated 20 Mbit/s, 24 ms RTT | 8.826 Mbit/s | 28.7 | 40 / 45 ms | 0 s |
| Emulated 3 Mbit/s, 190 ms RTT | 1.644 Mbit/s | 27.6 | 130 / 148 ms | 0 s |
| Actual TURN relay, both peers here | 548 kbit/s | 26.7 | 107 / 309 ms | 0.9 s |

The final live trial's target averaged 901 kbit/s, with a minimum of 420 kbit/s.
It produced no valid capacity estimate: 26 size-qualified samples were rejected
for pacing (1), short tails (6), or arrival bunching (19). Thus the reproduced
90–140 Mbit/s ratchet was absent, but bandwidth discovery on this loopback path
is still unresolved. This single run does not prove a throughput improvement
over v2, and none of these model FPS values measures actual image quality or
capture-to-display performance. Final raw logs are
`.tmp/review-v3-netlab-final-{fast,relay}.log` and
`.tmp/review-v3-live-final-diag.log`. Earlier guard-only comparisons used the
same corrected harness but preceded the final low-rate pacing changes, so they
are engineering traces rather than final-candidate acceptance numbers.

The follow-up candidate is `dist/screen-sharing-v3/PdaNetPortable.exe`, SHA256
`8F9F27D8E892B6ACB6A444CFBEFE34C221BCFFF77D48BA9EFD9BA68125D814A9`.
Debug and packaged Release passed all 11 sharing checks, including the new row
coverage checks. The packaged real capture-to-decode relay check passed at
1920×1080: first picture in 584 ms from capture start, then one requested IDR
and three subsequent pictures. Connection establishment before capture took
4.37 s. Logs are `.tmp/review-v3-{debug,release,relay}.txt`. These remain local
component/codec checks, not a two-PC input-to-display result.

## Relay-focused follow-up, 7 November

The adjacent `../ludo` repository supplies the TURN service. Its former
`max-bps=64000` setting imposed a 512 kbit/s per-direction limit intended for game
messages. Ludo commit `dceb78ed` already removed it on 6 November; the earlier
attribution of its removal to this performance pass was incorrect. Read-only
inspection also confirmed that the running primary was uncapped. No production
restart or credential change was necessary.

The follow-up addresses measured local costs and two relay delivery faults:

| Change | Mechanism and evidence |
| --- | --- |
| Announce a ready session after DTLS completes. | ICE connectivity alone does not make SRTP writable. Pion's `srtpWriterFuture` can return `(0, nil)` before `srtpReady`, silently losing the opening keyframe. Wait for the peer connection's connected state before starting capture; keep ICE loss/recovery handling. |
| Separate paced frame delivery from stalled loss repair. | The old 150–750 ms repair timer started at a frame's first packet, so an intact keyframe paced over 330 ms was discarded by the 150 ms timer. Renew the deadline only when the contiguous prefix advances, using actual packet arrival times. Later packets beyond a hole and duplicate retransmissions cannot extend it; unfinished frames retain a two-second hard limit. *(Revised in the second pass below: later packets of the same frame now extend it, which stops a keyframe from giving up its own repairs.)* |
| Vectorize the viewer's NV12-to-BGRA conversion. | The window previously ran scalar fixed-point pixel math on its UI thread. AVX2/SSE4.1 paths retain the same rounding, clipping and fallback. All 16,777,216 possible Y/U/V combinations and 272 size, alignment, pitch and tail cases match the scalar implementation. |
| Assemble H.264 into one sized buffer. | The general depacketizer repeatedly grew a fragment buffer and copied it into a NAL, then into the access unit. Validate and size once, then copy each payload directly into the output. Cache the marker scan position instead of rescanning all previous fragments on every packet. |
| Poll the pointer independently of capture/encoding. | A dedicated owner polls at 16 ms intervals, coalesces screen changes and resets cursor shapes when capture reopens. A 250 ms latest-position refresh repairs a lost final unreliable datagram. A blocked encoder no longer sets pointer polling cadence. The shared helper pipe can still briefly serialize a cursor message behind a video write. |

Local component measurements on the Intel Core i7-13620H audit PC:

| Work | Before | After |
| --- | ---: | ---: |
| 1920×1080 NV12 to BGRA, packaged Release median | 3.83 ms | 0.38 ms |
| 3840×2160 NV12 to BGRA, packaged Release median | 16.86 ms | 2.64 ms |
| 100 KB H.264 assembly, Pop after each packet | 133 µs, 825 KB allocated | 21.6 µs, 107 KB allocated |
| 1 MB H.264 assembly, Pop after each packet | 1.71 ms, 8.26 MB allocated | 0.18 ms, 1.01 MB allocated |

The color comparison times the scalar and selected SIMD implementation on the
same pixels in one process. SSE4.1 and scalar fallback were also exercised with
runtime intrinsics disabled. Assembly now uses two allocations per access unit,
including its sample object, rather than 22/31 in the 100 KB/1 MB cases. Its tests
cover random reordering and duplicate retransmissions across multiple frames,
sequence wrap, exact payload ownership, malformed STAP-A/FU-A and size limits.
Packetization follows [RFC 6184](https://www.rfc-editor.org/rfc/rfc6184.html),
including valid empty FU payloads. These component gains do not translate
directly into equal end-to-end latency gains.

The startup regression was reproduced against the original code: capture could
start while both peer and DTLS states were still `connecting`. The replacement
test records transport state at the instant of the announcement and sends
exactly one opening access unit, without waiting for a data-channel round trip
or retrying a lost keyframe. The paced-frame regression likewise fails with the
old deadline and passes with the new one. Further tests preserve missing-first
and missing-middle expiry, idle expiry, duplicate handling, the hard lifetime
bound and one shared deadline for already-old backlog.

Two opt-in relay checks now complement the fixed-rate quality simulation:

```powershell
# A file containing the HTTPS credential endpoint; never put credentials in argv.
$env:PDANET_LAB_FROM = 'C:\path\to\relay-endpoint.txt'
$env:PDANET_LAB_SECONDS = '15'
$env:PDANET_LAB_TRANSPORT = 'udp' # or tcp / tls
$env:PDANET_LAB_FPS = '60'
$env:PDANET_LAB_BPS = '2500000' # omit to follow congestion control
Push-Location rtc
go test -run '^TestRelayLab$' -count=1 -v
Pop-Location

# Actual desktop capture -> hardware encode -> TURN -> decode, with both peers
# forced through TURN. Uses saved relay Settings if PDANET_LAB_FROM is absent.
dotnet pdanet-windows/bin/Debug/net9.0-windows/PdaNetPortable.dll --selftest-share relay
```

The Go lab measures synthetic H.264-shaped frames through the actual encrypted
transport, pacing, retransmission, assembly and IPC. It reports first-picture
time, delivered FPS, latency percentiles and lab queue drops, including a
steady-state window after five seconds. Both peers run on this PC, so their
timestamps share a monotonic clock, and both must report the relay path. It
excludes capture, codec and display. The Windows relay check includes the real
capture and codecs, but does not create or time a viewer window. Neither is an
input-to-display comparison against TeamViewer.

The live relay trials are retained in
[the dated CSV](screen-sharing-relay-results-2026-11-07.csv). The fixed-rate
workloads deliberately bypass congestion-control reductions in their synthetic
sender. They expose overload rather than describe the real planner's offered
rate: several 2.5/6 Mbit/s trials still froze for seconds as network RTT, loss
and queueing rose. Results varied between trials; they do not establish a
general before/after FPS gain. The deterministic regressions establish the
specific startup and premature-expiry fixes. The adaptive workload follows the
helper's requested bitrate and is reported separately.

The final 30-second adaptive UDP trial delivered 59.8 synthetic frames/s after
five seconds of warmup, at 67 ms median / 161 ms p95 send-to-receive delay. Its
first picture arrived 180 ms after media readiness; the longest whole-trial gap
was 753 ms. Accepted payload averaged 735 kbit/s and the final encoder budget
was 587 kbit/s. This demonstrates delivery at the rate chosen on this path,
not sustained 6 Mbit/s capacity, image quality or full capture-to-display FPS.

Final verification passed Go tests, vet and helper self-tests (including a
TURN-over-TCP session), the Debug build with zero warnings/errors, and all
11 sharing checks in both Debug and the packaged Release executable. The
packaged helper's SHA256 matches the helper rebuilt from the final Go sources.
The real relay pipeline check also passed in both builds: the packaged run
decoded its first 1920×1080 picture in 454 ms from capture start, then received
two requested IDRs and decoded subsequent pictures. Connection establishment
before capture took 3.52 s. These timings include the current desktop and this
network on one run, not a controlled comparison with the earlier LAN timings.

The ready-to-run self-contained build is `dist/screen-sharing/PdaNetPortable.exe`,
SHA256 `9648DDE0864E40399B51B7B20B80D913CDD68CF7841B4DBDC721F87A117DD3A9`.
The exact final Windows outputs are retained locally in
`.tmp/share-performance-{debug,debug-relay,release,release-relay}-final.txt`.

## Findings and changes

| Finding | Effect | Change |
| --- | --- | --- |
| The first keyframe was encoded before any capture succeeded. | A black opening picture, or an uninitialized Media Foundation input. | Capture a real desktop image before encoding the opening keyframe. Take the still-screen GDI snapshot on the first duplication timeout rather than waiting 200 ms. |
| Pion's playback sample builder waited for the next frame to start. | An extra frame interval of latency; an isolated final update could remain held. | Assemble H.264 access units at the RTP marker, after verifying packet continuity and fragment boundaries. Keep sender seal frames for older peers. |
| The RTC receive queue allowed 256 frames; the decoder queue allowed 120. | Seconds of stale pictures could build up on a busy viewer. | Bound the helper queue by count, bytes and age; bound the decoder queue to 8 frames, 16 MiB of payload and 100 ms of local waiting (raised to 64 frames and 250 ms in the second pass below, so a repaired run of frames is decoded instead of discarded). Resume predicted video only from a clean keyframe after dropping a dependency. |
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
- Measure cursor telemetry separately from video motion. Polling now has its own
  thread, but a stalled shared IPC write can still delay delivery.

Keyframe backoff remains in place to avoid saturating lossy links with repeated
large IDR frames. A 100 ms decoder queue budget bounds queued work, not total
network latency or the time required to obtain a replacement keyframe.
