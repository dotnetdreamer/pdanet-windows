// Command pdanet-rtc carries PdaNet screen sharing over WebRTC.
//
// PdaNet starts one helper for each screen sharing session and talks to it over stdin and
// stdout, see ipc.go. The helper finds the other PC by the pairing code (session.go), links
// the two PCs straight to each other with WebRTC (peer.go), and carries the screen one way
// and the remote control both ways. It listens on nothing until PdaNet asks, and it exits
// when PdaNet closes stdin, so a session that ends leaves nothing running.
package main

import (
	"fmt"
	"log"
	"os"
	"runtime/debug"
)

// version goes to the other PC with the offer and the answer, so a later PdaNet can tell
// what it talks to.
const version = "1.0.0"

func main() {
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "selftest":
			os.Exit(runSelftest(os.Args[2:]))
		case "probe":
			os.Exit(runProbe(os.Args[2:]))
		case "relaytest":
			os.Exit(runRelayTest(os.Stdin, os.Stdout))
		case "version", "--version":
			fmt.Println(version)
			return
		}
	}

	log.SetOutput(os.Stderr)
	log.SetFlags(log.Ltime | log.Lmicroseconds)

	// A session holds little: packets pass through and are gone. A tight heap keeps the
	// helper small next to PdaNet.
	debug.SetGCPercent(50)
	debug.SetMemoryLimit(48 << 20)

	os.Exit(runSession(os.Stdin, os.Stdout))
}
