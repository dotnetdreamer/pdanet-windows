package main

import (
	"crypto/hkdf"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"math/big"

	"filippo.io/nistec"
)

// SPAKE2 over P-256 with SHA-256, HKDF and HMAC, as in RFC 9382. The two PCs prove they hold
// the same short password without sending it: a relay, or anyone who watches, learns nothing
// it could test guesses against, and someone who guesses gets one try per attempt.
//
// The PC that views is A, the PC that shares is B.

var (
	// The fixed points M and N for P-256 from RFC 9382 section 6, nobody knows their discrete logs.
	spakeM = mustPoint("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f")
	spakeN = mustPoint("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49")

	p256Order, _ = new(big.Int).SetString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", 16)

	errBadShare = errors.New("spake2: bad share")
)

func mustPoint(h string) *nistec.P256Point {
	b, err := hex.DecodeString(h)
	if err != nil {
		panic(err)
	}
	p, err := nistec.NewP256Point().SetBytes(b)
	if err != nil {
		panic(err)
	}
	return p
}

type spake2 struct {
	isA   bool
	w     *big.Int
	x     *big.Int // x for A, y for B
	share []byte   // pA for A, pB for B, uncompressed
}

// pakeKeys is what a finished exchange gives both sides.
type pakeKeys struct {
	ke       []byte // the shared secret, the keys for the sealed messages come from it
	kcA, kcB []byte // confirmation keys
	confirmA []byte // cA, MAC(KcA, TT), A proves it holds the password
	confirmB []byte // cB, MAC(KcB, TT), B proves it holds the password
}

// newSpake2 starts an exchange. x is random unless a test passes one.
func newSpake2(isA bool, w, x *big.Int) (*spake2, error) {
	if x == nil {
		var err error
		if x, err = rand.Int(rand.Reader, p256Order); err != nil {
			return nil, err
		}
	}
	X, err := nistec.NewP256Point().ScalarBaseMult(scalar32(x))
	if err != nil {
		return nil, err
	}
	base := spakeN
	if isA {
		base = spakeM
	}
	wBase, err := nistec.NewP256Point().ScalarMult(base, scalar32(w))
	if err != nil {
		return nil, err
	}
	share := nistec.NewP256Point().Add(wBase, X)
	return &spake2{isA: isA, w: w, x: x, share: share.Bytes()}, nil
}

// finish takes the other side's share and derives the keys. idA and idB name the two sides,
// aad binds the exchange to its use.
func (s *spake2) finish(idA, idB string, peerShare, aad []byte) (*pakeKeys, error) {
	peer, err := nistec.NewP256Point().SetBytes(peerShare)
	if err != nil || len(peerShare) != 65 {
		return nil, errBadShare
	}
	// A: K = x * (pB - w*N), B: K = y * (pA - w*M). The cofactor of P-256 is 1.
	other := spakeM
	if s.isA {
		other = spakeN
	}
	negW := new(big.Int).Sub(p256Order, s.w)
	negW.Mod(negW, p256Order)
	minus, err := nistec.NewP256Point().ScalarMult(other, scalar32(negW))
	if err != nil {
		return nil, err
	}
	k, err := nistec.NewP256Point().ScalarMult(nistec.NewP256Point().Add(peer, minus), scalar32(s.x))
	if err != nil {
		return nil, err
	}
	kBytes := k.Bytes()
	if len(kBytes) == 1 {
		return nil, errBadShare // the point at infinity
	}

	pA, pB := s.share, peerShare
	if !s.isA {
		pA, pB = peerShare, s.share
	}
	tt := transcript([]byte(idA), []byte(idB), pA, pB, kBytes, scalar32(s.w))
	sum := sha256.Sum256(tt)
	ke, ka := sum[:16], sum[16:]
	kc, err := hkdf.Key(sha256.New, ka, nil, "ConfirmationKeys"+string(aad), 32)
	if err != nil {
		return nil, err
	}
	keys := &pakeKeys{ke: append([]byte(nil), ke...), kcA: kc[:16], kcB: kc[16:]}
	keys.confirmA = mac(keys.kcA, tt)
	keys.confirmB = mac(keys.kcB, tt)
	return keys, nil
}

// transcript is TT from RFC 9382: each part with its length as 8 bytes little endian.
func transcript(parts ...[]byte) []byte {
	n := 0
	for _, p := range parts {
		n += 8 + len(p)
	}
	out := make([]byte, 0, n)
	for _, p := range parts {
		out = binary.LittleEndian.AppendUint64(out, uint64(len(p)))
		out = append(out, p...)
	}
	return out
}

func mac(key, data []byte) []byte {
	m := hmac.New(sha256.New, key)
	m.Write(data)
	return m.Sum(nil)
}

// scalar32 is v as 32 bytes big endian, the length P-256 scalars and w take.
func scalar32(v *big.Int) []byte {
	out := make([]byte, 32)
	return v.FillBytes(out)
}
