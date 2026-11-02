package main

import (
	"context"
	"errors"
	"io"
	"log"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"
)

// mqttRelay meets the other PC on a public MQTT broker, MQTT 3.1.1 over a WebSocket with QoS 0.
// It needs only CONNECT, SUBSCRIBE, PUBLISH and PING, so it is written out here.
type mqttRelay struct {
	rv     *rendezvous
	url    string
	d      dialer
	topic  string
	out    chan []byte
	isUp   atomic.Bool
	ctx    context.Context
	cancel context.CancelFunc
}

func newMQTTRelay(rv *rendezvous, url string, d dialer) relay {
	r := &mqttRelay{rv: rv, url: url, d: d, topic: "pdanet/share/v1/" + rv.topic, out: make(chan []byte, 16)}
	r.ctx, r.cancel = context.WithCancel(context.Background())
	go r.run()
	return r
}

func (r *mqttRelay) name() string { return r.url }
func (r *mqttRelay) up() bool     { return r.isUp.Load() }
func (r *mqttRelay) close()       { r.cancel() }

func (r *mqttRelay) publish(msg []byte) {
	if !r.isUp.Load() {
		return
	}
	select {
	case r.out <- msg:
	default:
	}
}

func (r *mqttRelay) run() {
	for try := 0; r.ctx.Err() == nil; try++ {
		err := r.session()
		r.isUp.Store(false)
		if r.ctx.Err() != nil {
			return
		}
		log.Printf("relay %s: %v", r.url, err)
		if !backoff(r.ctx, try) {
			return
		}
	}
}

func (r *mqttRelay) session() error {
	c, err := r.d.websocket(r.ctx, r.url, "mqtt")
	if err != nil {
		return err
	}
	defer c.Close()
	stop := context.AfterFunc(r.ctx, func() { _ = c.Close() })
	defer stop()
	in := &wsStream{c: c}

	// CONNECT: protocol "MQTT" level 4, clean session, keep alive 30 s, a random client id.
	connect := mqttString("MQTT")
	connect = append(connect, 4, 0x02, 0, 30)
	connect = append(connect, mqttString("pdn"+randHex(8))...)
	if err := r.send(c, 0x10, connect); err != nil {
		return err
	}
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	if typ, body, err := readMQTT(in); err != nil || typ>>4 != 2 || len(body) < 2 || body[1] != 0 {
		return errors.New("broker refused the connection")
	}

	sub := []byte{0, 1}
	sub = append(sub, mqttString(r.topic)...)
	sub = append(sub, 0)
	if err := r.send(c, 0x82, sub); err != nil {
		return err
	}
	r.isUp.Store(true)
	r.rv.relayUp()

	done := make(chan struct{})
	defer close(done)
	go func() {
		ping := time.NewTicker(20 * time.Second)
		defer ping.Stop()
		for {
			var err error
			select {
			case msg := <-r.out:
				body := append(mqttString(r.topic), msg...)
				err = r.send(c, 0x30, body)
			case <-ping.C:
				err = r.send(c, 0xc0, nil)
			case <-done:
				return
			}
			if err != nil {
				_ = c.Close()
				return
			}
		}
	}()

	for {
		_ = c.SetReadDeadline(time.Now().Add(70 * time.Second))
		typ, body, err := readMQTT(in)
		if err != nil {
			return err
		}
		if typ>>4 != 3 || len(body) < 2 {
			continue // SUBACK, PINGRESP
		}
		n := int(body[0])<<8 | int(body[1])
		rest := body[2:]
		if n > len(rest) {
			continue
		}
		rest = rest[n:]
		if typ&0x06 != 0 { // QoS 1 or 2 carry a packet id
			if len(rest) < 2 {
				continue
			}
			rest = rest[2:]
		}
		r.rv.deliver(rest)
	}
}

func (r *mqttRelay) send(c *websocket.Conn, header byte, body []byte) error {
	_ = c.SetWriteDeadline(time.Now().Add(10 * time.Second))
	return c.WriteMessage(websocket.BinaryMessage, mqttPacket(header, body))
}

func mqttString(s string) []byte {
	return append([]byte{byte(len(s) >> 8), byte(len(s))}, s...)
}

func mqttPacket(header byte, body []byte) []byte {
	out := []byte{header}
	n := len(body)
	for {
		b := byte(n % 128)
		n /= 128
		if n > 0 {
			b |= 0x80
		}
		out = append(out, b)
		if n == 0 {
			break
		}
	}
	return append(out, body...)
}

func readMQTT(r io.Reader) (byte, []byte, error) {
	var b [1]byte
	if _, err := io.ReadFull(r, b[:]); err != nil {
		return 0, nil, err
	}
	typ := b[0]
	n, mult := 0, 1
	for i := 0; ; i++ {
		if i == 4 {
			return 0, nil, errors.New("bad length")
		}
		if _, err := io.ReadFull(r, b[:]); err != nil {
			return 0, nil, err
		}
		n += int(b[0]&127) * mult
		mult *= 128
		if b[0]&128 == 0 {
			break
		}
	}
	if n > 1<<20 {
		return 0, nil, errors.New("packet too big")
	}
	body := make([]byte, n)
	_, err := io.ReadFull(r, body)
	return typ, body, err
}

// wsStream reads the binary WebSocket messages as one stream, since an MQTT packet may span
// messages.
type wsStream struct {
	c *websocket.Conn
	r io.Reader
}

func (s *wsStream) Read(p []byte) (int, error) {
	for {
		if s.r == nil {
			_, r, err := s.c.NextReader()
			if err != nil {
				return 0, err
			}
			s.r = r
		}
		n, err := s.r.Read(p)
		if err == io.EOF {
			s.r = nil
			if n > 0 {
				return n, nil
			}
			continue
		}
		return n, err
	}
}
