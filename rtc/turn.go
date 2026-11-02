package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/pion/logging"
	"github.com/pion/stun/v4"
	"github.com/pion/turn/v5"
)

// TURN relays carry the screen when the two PCs cannot reach each other directly, such as on a
// company network that lets no UDP out, or between two networks that both change ports. WebRTC
// still prefers a direct path and only takes a relay when nothing else works. A relay passes on
// DTLS packets it cannot read: it sees that two PCs talk, not what they say.
//
// PdaNet gives the helper TURN servers with their credentials, a Cloudflare Realtime TURN key, or
// both. The helper trades the key for credentials that expire after a day, so the key itself
// never leaves this PC. Over TLS on port 443 a relay looks like any secure website, which gets it
// through most company networks.

// iceServer is a TURN server and its credentials, as WebRTC takes them.
type iceServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

// turnKey is a Cloudflare Realtime TURN key: its ID and its API token.
type turnKey struct {
	ID    string `json:"id"`
	Token string `json:"token"`
}

const (
	cloudflareTURN = "https://rtc.live.cloudflare.com/v1/turn/keys/%s/credentials/generate-ice-servers"
	turnTTL        = 24 * 60 * 60 // seconds the Cloudflare credentials hold, longer than any session
	turnFetchWait  = 8 * time.Second
)

var turnKeyID = regexp.MustCompile(`^[A-Za-z0-9_-]{8,128}$`)

// cleanTURN keeps the TURN URLs WebRTC can use, and logs the others.
func cleanTURN(list []iceServer) []iceServer {
	var out []iceServer
	for _, s := range list {
		var urls []string
		for _, u := range s.URLs {
			u = strings.TrimSpace(u)
			uri, err := stun.ParseURI(u)
			switch {
			case err != nil:
				log.Printf("relay %s: %v", u, err)
			case uri.Scheme != stun.SchemeTypeTURN && uri.Scheme != stun.SchemeTypeTURNS:
				log.Printf("relay %s: not a turn: or turns: address", u)
			case s.Username == "" || s.Credential == "":
				log.Printf("relay %s: needs a username and a password", u)
			default:
				urls = append(urls, u)
			}
		}
		if len(urls) > 0 {
			out = append(out, iceServer{URLs: urls, Username: s.Username, Credential: s.Credential})
		}
	}
	return out
}

// cloudflareServers trades a Cloudflare TURN key for TURN servers with credentials that expire
// after a day.
func cloudflareServers(ctx context.Context, d dialer, k turnKey) ([]iceServer, error) {
	if !turnKeyID.MatchString(k.ID) || k.Token == "" {
		return nil, errors.New("the Cloudflare key ID or API token is missing")
	}
	body, _ := json.Marshal(map[string]int{"ttl": turnTTL})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, fmt.Sprintf(cloudflareTURN, url.PathEscape(k.ID)), bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+k.Token)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "PdaNet/"+version)
	res, err := d.httpClient().Do(req)
	if err != nil {
		return nil, fmt.Errorf("could not reach Cloudflare: %w", err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(res.Body, 64<<10))
	switch {
	case res.StatusCode == http.StatusUnauthorized || res.StatusCode == http.StatusForbidden || res.StatusCode == http.StatusNotFound:
		return nil, errors.New("Cloudflare did not accept the key ID and API token")
	case res.StatusCode/100 != 2:
		return nil, fmt.Errorf("Cloudflare answered %s", res.Status)
	}
	var out struct {
		ICEServers []iceServer `json:"iceServers"`
	}
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, fmt.Errorf("Cloudflare sent an answer PdaNet cannot read: %w", err)
	}
	for i := range out.ICEServers {
		// Cloudflare's other port, 53, is blocked by many networks, and without trickle ICE it
		// would only make the PC wait. The STUN servers come from PdaNet's own list.
		out.ICEServers[i].URLs = slices.DeleteFunc(out.ICEServers[i].URLs, func(u string) bool {
			uri, err := stun.ParseURI(u)
			return err != nil || uri.Port == 53 || (uri.Scheme != stun.SchemeTypeTURN && uri.Scheme != stun.SchemeTypeTURNS)
		})
	}
	list := cleanTURN(out.ICEServers)
	if len(list) == 0 {
		return nil, errors.New("Cloudflare sent no TURN servers")
	}
	return list, nil
}

// turnURLs lists the relay addresses, without credentials, for the log.
func turnURLs(list []iceServer) string {
	var urls []string
	for _, s := range list {
		urls = append(urls, s.URLs...)
	}
	return strings.Join(urls, " ")
}

// turnWay names how a TURN URL reaches its server, such as "TLS port 443".
func turnWay(uri *stun.URI) string {
	switch {
	case uri.Scheme == stun.SchemeTypeTURNS && uri.Proto == stun.ProtoTypeUDP:
		return fmt.Sprintf("DTLS port %d", uri.Port)
	case uri.Scheme == stun.SchemeTypeTURNS:
		return fmt.Sprintf("TLS port %d", uri.Port)
	case uri.Proto == stun.ProtoTypeTCP:
		return fmt.Sprintf("TCP port %d", uri.Port)
	}
	return fmt.Sprintf("UDP port %d", uri.Port)
}

// ============================ Testing the relays ============================

// relayResult is one line of what "relaytest" prints.
type relayResult struct {
	Ev     string `json:"ev"` // "key", "server", "session", "failed" or "done"
	URL    string `json:"url,omitempty"`
	Host   string `json:"host,omitempty"`
	Way    string `json:"way,omitempty"`
	OK     bool   `json:"ok"`
	Ms     int64  `json:"ms,omitempty"`
	Detail string `json:"detail,omitempty"`
}

// runRelayTest checks the relays PdaNet passes on stdin, the way a session would use them: it
// gets a relay address from each TURN URL, then runs a whole session between two helpers in this
// process that may only use the relays. It prints one JSON line per result, for PdaNet's Test
// button, and keeps the credentials off the command line.
func runRelayTest(in io.Reader, out io.Writer) int {
	enc := json.NewEncoder(out)
	var opt options
	if err := json.NewDecoder(io.LimitReader(in, 1<<20)).Decode(&opt); err != nil {
		_ = enc.Encode(relayResult{Ev: "failed", Detail: "bad input: " + err.Error()})
		return 2
	}
	servers := cleanTURN(opt.Turn)
	if opt.TurnKey != nil {
		var d dialer
		if opt.Route.Proxy != "" {
			if u, err := url.Parse("http://" + opt.Route.Proxy); err == nil {
				d.proxy = u
			}
		}
		ctx, cancel := context.WithTimeout(context.Background(), turnFetchWait)
		list, err := cloudflareServers(ctx, d, *opt.TurnKey)
		cancel()
		switch {
		case err != nil && len(servers) == 0:
			_ = enc.Encode(relayResult{Ev: "failed", Detail: err.Error()})
			return 1
		case err != nil:
			_ = enc.Encode(relayResult{Ev: "key", Detail: err.Error()})
		}
		servers = append(servers, list...)
	}
	if len(servers) == 0 {
		_ = enc.Encode(relayResult{Ev: "failed", Detail: "no relay server is set up"})
		return 1
	}

	var mu sync.Mutex
	var wg sync.WaitGroup
	reached := 0
	for _, s := range servers {
		for _, u := range s.URLs {
			wg.Add(1)
			go func() {
				defer wg.Done()
				r := relayResult{Ev: "server", URL: u}
				if uri, err := stun.ParseURI(u); err == nil {
					r.Host, r.Way = uri.Host, turnWay(uri)
				}
				start := time.Now()
				err := allocate(u, s.Username, s.Credential)
				r.OK, r.Ms = err == nil, time.Since(start).Milliseconds()
				if err != nil {
					r.Detail = err.Error()
				}
				mu.Lock()
				if r.OK {
					reached++
				}
				_ = enc.Encode(r)
				mu.Unlock()
			}()
		}
	}
	wg.Wait()
	if reached == 0 {
		_ = enc.Encode(relayResult{Ev: "done", OK: false})
		return 1
	}

	start := time.Now()
	path, err := loopback(route{Mode: "direct"}, route{Mode: "direct"},
		options{Stun: []string{"none"}, Turn: servers, RelayOnly: true})
	r := relayResult{Ev: "session", OK: err == nil && path == "relay", Ms: time.Since(start).Milliseconds()}
	switch {
	case err != nil:
		r.Detail = err.Error()
	case path != "relay":
		r.Detail = "the session did not go through the relay, it went " + path
	}
	_ = enc.Encode(r)
	_ = enc.Encode(relayResult{Ev: "done", OK: r.OK})
	if !r.OK {
		return 1
	}
	return 0
}

// allocate gets one relay address from a TURN URL, the way WebRTC does, and gives it back.
func allocate(rawURL, user, pass string) error {
	uri, err := stun.ParseURI(rawURL)
	if err != nil {
		return err
	}
	addr := net.JoinHostPort(uri.Host, fmt.Sprint(uri.Port))
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()

	lf := logging.NewDefaultLoggerFactory()
	lf.DefaultLogLevel = logging.LogLevelDisabled
	cfg := &turn.ClientConfig{Username: user, Password: pass, LoggerFactory: lf}
	nd := &net.Dialer{Timeout: 6 * time.Second}
	switch {
	case uri.Scheme == stun.SchemeTypeTURNS && uri.Proto == stun.ProtoTypeUDP:
		return errors.New("DTLS is not tested")
	case uri.Scheme == stun.SchemeTypeTURNS:
		c, err := (&tls.Dialer{NetDialer: nd, Config: &tls.Config{ServerName: uri.Host, MinVersion: tls.VersionTLS12}}).DialContext(ctx, "tcp", addr)
		if err != nil {
			return fmt.Errorf("could not connect: %w", err)
		}
		cfg.Conn = turn.NewSTUNConn(c)
	case uri.Proto == stun.ProtoTypeTCP:
		c, err := nd.DialContext(ctx, "tcp", addr)
		if err != nil {
			return fmt.Errorf("could not connect: %w", err)
		}
		cfg.Conn = turn.NewSTUNConn(c)
	default:
		c, err := net.ListenPacket("udp4", "0.0.0.0:0")
		if err != nil {
			return err
		}
		cfg.Conn, cfg.STUNServerAddr, cfg.TURNServerAddr = c, addr, addr
	}
	defer cfg.Conn.Close()
	client, err := turn.NewClient(cfg)
	if err != nil {
		return err
	}
	defer client.Close()
	if err := client.Listen(); err != nil {
		return err
	}
	relay, err := client.AllocateWithContext(ctx)
	if err != nil {
		switch msg := err.Error(); {
		case errors.Is(err, context.DeadlineExceeded) || strings.Contains(msg, "retransmissions failed"):
			return errors.New("no answer, or the username and password were not accepted")
		case strings.Contains(msg, "error 401"):
			return errors.New("the username and password were not accepted")
		case strings.Contains(msg, "error 403"):
			return errors.New("the relay refused this PC")
		}
		return err
	}
	return relay.Close()
}
