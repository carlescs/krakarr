"""Inserts a minimal HDR10+ (SMPTE ST 2094-40) prefix SEI before the first slice of an Annex B HEVC stream.

Usage: python3 inject-hdr10plus.py in.hevc out.hevc (see README.md)."""
import sys
data = open(sys.argv[1], 'rb').read()
i = 0
while True:
    i = data.find(b'\x00\x00\x01', i)
    if i < 0: sys.exit('no vcl')
    if ((data[i + 3] >> 1) & 0x3F) < 32: break
    i += 3
start = i - 1 if i > 0 and data[i - 1] == 0 else i
# SMPTE ST 2094-40 metadata, one window, no optional parts.
bits = ''
def put(v, n):
    global bits
    bits += format(v, '0%db' % n)
put(1, 2); put(1000, 27); put(0, 1)
for _ in range(3): put(1000, 17)
put(500, 17); put(1, 4); put(50, 7); put(400, 17); put(0, 10)
put(0, 1); put(0, 1); put(0, 1)
bits += '0' * (-len(bits) % 8)
meta = bytes(int(bits[k:k + 8], 2) for k in range(0, len(bits), 8))
payload = bytes([0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01]) + meta
rbsp = bytes([0x04, len(payload)]) + payload + b'\x80'
out, zeros = bytearray(), 0
for b in rbsp:
    if zeros >= 2 and b <= 3:
        out.append(3); zeros = 0
    out.append(b); zeros = zeros + 1 if b == 0 else 0
sei = b'\x00\x00\x00\x01\x4E\x01' + bytes(out)
open(sys.argv[2], 'wb').write(data[:start] + sei + data[start:])
