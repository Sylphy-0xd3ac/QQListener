"""Create the bounded QQLDIFF1 delta used by the managed PMHQ installer."""
import argparse
import hashlib
from pathlib import Path
import struct

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--original', type=Path, required=True)
parser.add_argument('--patched', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
original, patched = args.original.read_bytes(), args.patched.read_bytes()
if hashlib.sha256(original).hexdigest() != '345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4':
    raise SystemExit('Expected the official PMHQ v9.0.6 Windows x64 DLL')
if hashlib.sha256(patched).hexdigest() != '2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b':
    raise SystemExit('Expected the reviewed local-pipe.1 DLL')
ranges = []
for index, value in enumerate(patched):
    if index < len(original) and original[index] == value:
        continue
    if ranges and index - ranges[-1][1] <= 8:
        ranges[-1][1] = index + 1
    else:
        ranges.append([index, index + 1])
delta = bytearray(struct.pack('<8sIII', b'QQLDIFF1', len(original), len(patched), len(ranges)))
restored = bytearray(original[:len(patched)])
restored.extend(bytes(len(patched) - len(restored)))
for start, end in ranges:
    delta.extend(struct.pack('<II', start, end - start))
    delta.extend(patched[start:end])
    restored[start:end] = patched[start:end]
assert restored == patched
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_bytes(delta)
print(f'{len(delta)} bytes; {len(ranges)} ranges; SHA-256 {hashlib.sha256(delta).hexdigest()}')
