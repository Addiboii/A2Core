"""Minimal pure-Python AES-128-CBC decryption (FIPS-197), for tests only; the standard
library has no AES. Slow and not constant-time: never use it for real secrets."""


def _xtime(a):
    return ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else a << 1


def _mul(a, b):
    result = 0
    while b:
        if b & 1:
            result ^= a
        a, b = _xtime(a), b >> 1
    return result


def _sbox():
    box = [0] * 256
    p = q = 1
    while True:
        p = p ^ ((p << 1) & 0xFF) ^ (0x1B if p & 0x80 else 0)
        q ^= q << 1
        q ^= q << 2
        q ^= q << 4
        q &= 0xFF
        if q & 0x80:
            q ^= 0x09
        x = q ^ (q << 1 | q >> 7) ^ (q << 2 | q >> 6) ^ (q << 3 | q >> 5) ^ (q << 4 | q >> 4)
        box[p] = (x ^ 0x63) & 0xFF
        if p == 1:
            break
    box[0] = 0x63
    return box


SBOX = _sbox()
INV_SBOX = [0] * 256
for _i, _v in enumerate(SBOX):
    INV_SBOX[_v] = _i


def _expand(key):
    words = [list(key[i:i + 4]) for i in range(0, 16, 4)]
    rcon = 1
    for i in range(4, 44):
        word = list(words[i - 1])
        if i % 4 == 0:
            word = [SBOX[b] for b in word[1:] + word[:1]]
            word[0] ^= rcon
            rcon = _xtime(rcon)
        words.append([a ^ b for a, b in zip(words[i - 4], word)])
    return [sum(words[r * 4:r * 4 + 4], []) for r in range(11)]


def _decrypt_block(block, rounds):
    state = [b ^ k for b, k in zip(block, rounds[10])]
    for r in range(9, -1, -1):
        # Inverse shift rows (column-major state), inverse S-box, round key, inverse mix columns.
        state = [state[((c - row) % 4) * 4 + row] for c in range(4) for row in range(4)]
        state = [INV_SBOX[b] for b in state]
        state = [b ^ k for b, k in zip(state, rounds[r])]
        if r:
            mixed = []
            for c in range(4):
                a = state[c * 4:c * 4 + 4]
                mixed += [_mul(a[0], 14) ^ _mul(a[1], 11) ^ _mul(a[2], 13) ^ _mul(a[3], 9),
                          _mul(a[0], 9) ^ _mul(a[1], 14) ^ _mul(a[2], 11) ^ _mul(a[3], 13),
                          _mul(a[0], 13) ^ _mul(a[1], 9) ^ _mul(a[2], 14) ^ _mul(a[3], 11),
                          _mul(a[0], 11) ^ _mul(a[1], 13) ^ _mul(a[2], 9) ^ _mul(a[3], 14)]
            state = mixed
    return bytes(state)


def cbc_decrypt(key, iv, data):
    if len(key) != 16 or len(data) % 16:
        raise ValueError('AES-128-CBC needs a 16-byte key and whole blocks')
    rounds = _expand(key)
    out, previous = bytearray(), iv
    for i in range(0, len(data), 16):
        block = data[i:i + 16]
        out += bytes(a ^ b for a, b in zip(_decrypt_block(block, rounds), previous))
        previous = block
    pad = out[-1]
    if not 1 <= pad <= 16 or out[-pad:] != bytes([pad]) * pad:
        raise ValueError('Bad PKCS7 padding')
    return bytes(out[:-pad])
