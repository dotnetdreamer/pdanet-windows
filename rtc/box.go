package main

import (
	"bytes"
	"compress/flate"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hkdf"
	"crypto/rand"
	"crypto/sha256"
	"crypto/sha512"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"io"
	"math/big"
	"strings"
)

// A pairing code is ten digits. The first four name the meeting place on the relays and are
// public. The last six are the password, which only SPAKE2 sees, so a relay or a listener
// cannot test guesses, and a stranger who guesses gets one try per attempt. The PC that
// shares makes a new code after a few wrong tries.
type pairCode string

const codeDigits = 10

func newCode() pairCode {
	var b strings.Builder
	ten := big.NewInt(10)
	for b.Len() < codeDigits {
		d, err := rand.Int(rand.Reader, ten)
		if err != nil {
			panic(err)
		}
		b.WriteByte(byte('0' + d.Int64()))
	}
	return pairCode(b.String())
}

// parseCode keeps the digits, so "482 619 3075" and "482-619-3075" both work.
func parseCode(s string) (pairCode, error) {
	var b strings.Builder
	for _, r := range s {
		if r >= '0' && r <= '9' {
			b.WriteRune(r)
		}
	}
	if b.Len() != codeDigits {
		return "", errors.New("a code has ten digits")
	}
	return pairCode(b.String()), nil
}

func (c pairCode) room() string { return string(c[:4]) }

// topic names the meeting place on every relay. Codes from other apps never share it.
func topicFor(room string) string {
	sum := sha256.Sum256([]byte("pdanet-share-v1 room " + room))
	return hex.EncodeToString(sum[:12])
}

// w turns the password into the SPAKE2 scalar: SHA-512 of the room and the password, reduced
// mod the group order. 512 bits make the reduction bias negligible. RFC 9382 suggests a
// memory hard function to slow down guessing against a stored w; here w is never stored and
// every guess needs a live exchange with the PC that shares, so a plain hash is enough.
func (c pairCode) w() *big.Int {
	sum := sha512.Sum512([]byte("pdanet-share-v1 password " + c.room() + " " + string(c[4:])))
	v := new(big.Int).SetBytes(sum[:])
	return v.Mod(v, p256Order)
}

// pakeAAD binds an exchange to this app, this version and this meeting place.
func pakeAAD(room string) []byte { return []byte("pdanet-share-v1 " + room) }

func viewerID(id string) string { return "pdanet viewer " + id }
func hostID(id string) string   { return "pdanet host " + id }

// box seals the messages that follow the pairing with a key from SPAKE2: AES-256-GCM, with
// the kind and both ends in the associated data, so a message cannot be replayed as another
// kind or sent back to its sender.
type box struct{ aead cipher.AEAD }

func newBox(ke []byte) (*box, error) {
	key, err := hkdf.Key(sha256.New, ke, nil, "pdanet-share-v1 box", 32)
	if err != nil {
		return nil, err
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	return &box{aead: aead}, nil
}

func boxAAD(kind, from, to string) []byte {
	return []byte("pdanet-share-v1|" + kind + "|" + from + "|" + to)
}

// seal compresses, encrypts and encodes plain. SDP compresses to about half, which keeps the
// offer under the 4 KB some relays allow.
func (b *box) seal(kind, from, to string, plain []byte) string {
	var z bytes.Buffer
	w, _ := flate.NewWriter(&z, flate.BestCompression)
	_, _ = w.Write(plain)
	_ = w.Close()

	nonce := make([]byte, b.aead.NonceSize(), b.aead.NonceSize()+z.Len()+b.aead.Overhead())
	if _, err := rand.Read(nonce); err != nil {
		panic(err)
	}
	out := b.aead.Seal(nonce, nonce, z.Bytes(), boxAAD(kind, from, to))
	return base64.RawStdEncoding.EncodeToString(out)
}

func (b *box) open(kind, from, to, sealed string) ([]byte, error) {
	raw, err := base64.RawStdEncoding.DecodeString(sealed)
	ns := b.aead.NonceSize()
	if err != nil || len(raw) < ns+b.aead.Overhead() {
		return nil, errors.New("box: malformed")
	}
	z, err := b.aead.Open(nil, raw[:ns], raw[ns:], boxAAD(kind, from, to))
	if err != nil {
		return nil, err
	}
	// The sender proved it holds the key, still never inflate without a limit.
	r := flate.NewReader(bytes.NewReader(z))
	defer r.Close()
	return io.ReadAll(io.LimitReader(r, 1<<20))
}

func b64(b []byte) string { return base64.RawStdEncoding.EncodeToString(b) }

func unb64(s string) []byte {
	b, err := base64.RawStdEncoding.DecodeString(s)
	if err != nil {
		return nil
	}
	return b
}

func randHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
