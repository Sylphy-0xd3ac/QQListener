"""Link a freestanding payload into the original DLL without moving original code."""
from pathlib import Path
import hashlib
import json
import struct
import sys
import argparse

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'analysis-deps'))
import pefile

ORIGINAL_HASH = '345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--original', type=Path, default=HERE.parent / 'windows/pmhq.dll')
parser.add_argument('--payload', type=Path, default=HERE/'payload.dll')
parser.add_argument('--output', type=Path, default=HERE/'pmhq.dll')
args = parser.parse_args()
original_path = args.original
original_data = original_path.read_bytes()
assert hashlib.sha256(original_data).hexdigest() == ORIGINAL_HASH
original = pefile.PE(data=original_data)
helper = pefile.PE(str(args.payload))
assert original.FILE_HEADER.Machine == helper.FILE_HEADER.Machine == 0x8664
for directory in (9, 10, 13):
    assert not helper.OPTIONAL_HEADER.DATA_DIRECTORY[directory].Size, f'Unsupported payload directory {directory}'
# Keep the original DLL's TLS directory and callbacks at their original RVAs.
# The payload is freestanding and adds no second TLS index/template.
assert not helper.OPTIONAL_HEADER.DATA_DIRECTORY[3].Size % 12

def align(n, a): return (n + a - 1) // a * a
shift = align(original.OPTIONAL_HEADER.SizeOfImage, 0x1000)
meta_rva = shift + align(helper.OPTIONAL_HEADER.SizeOfImage, 0x1000)
mapped = bytearray(helper.get_memory_mapped_image())
mapped.extend(bytes(helper.OPTIONAL_HEADER.SizeOfImage - len(mapped)))
exports = {e.name.decode(): e.address for e in helper.DIRECTORY_ENTRY_EXPORT.symbols}
orig_exports = original.DIRECTORY_ENTRY_EXPORT
orig_dllmain = next(e.address for e in orig_exports.symbols if e.name == b'DllMain')
struct.pack_into('<8sIIII', mapped, exports['PMHQLocalSignConfig'], b'QLSG906\0', original.OPTIONAL_HEADER.AddressOfEntryPoint, orig_dllmain, shift, 0)

# Fix absolute payload addresses to their location in the combined preferred
# image, and preserve relocations for a later ASLR move by Windows/the mapper.
relocations = bytearray(original.get_data(original.OPTIONAL_HEADER.DATA_DIRECTORY[5].VirtualAddress, original.OPTIONAL_HEADER.DATA_DIRECTORY[5].Size))
delta = original.OPTIONAL_HEADER.ImageBase + shift - helper.OPTIONAL_HEADER.ImageBase
for block in getattr(helper, 'DIRECTORY_ENTRY_BASERELOC', []):
    entries = []
    for entry in block.entries:
        assert entry.type in (0, 10), f'Unsupported relocation {entry.type}'
        if entry.type == 10:
            value, = struct.unpack_from('<Q', mapped, entry.rva)
            struct.pack_into('<Q', mapped, entry.rva, value + delta)
        entries.append((entry.type << 12) | (entry.rva - block.struct.VirtualAddress))
    if len(entries) % 2: entries.append(0)
    relocations.extend(struct.pack('<II', block.struct.VirtualAddress + shift, 8 + len(entries) * 2))
    relocations.extend(struct.pack('<' + 'H' * len(entries), *entries))

imports = bytearray()
for item in original.DIRECTORY_ENTRY_IMPORT:
    imports.extend(original_data[item.struct.get_file_offset():item.struct.get_file_offset() + 20])
for item in helper.DIRECTORY_ENTRY_IMPORT:
    desc = item.struct
    assert desc.OriginalFirstThunk and not desc.TimeDateStamp and not desc.ForwarderChain
    for thunk in (desc.OriginalFirstThunk, desc.FirstThunk):
        position = thunk
        while True:
            value, = struct.unpack_from('<Q', mapped, position)
            if not value: break
            if not value & (1 << 63): struct.pack_into('<Q', mapped, position, value + shift)
            position += 8
    imports.extend(struct.pack('<IIIII', desc.OriginalFirstThunk + shift, 0, 0, desc.Name + shift, desc.FirstThunk + shift))
imports.extend(bytes(20))

pdata = []
for owner, adjustment in ((original, 0), (helper, shift)):
    directory = owner.OPTIONAL_HEADER.DATA_DIRECTORY[3]
    raw = owner.get_data(directory.VirtualAddress, directory.Size)
    assert len(raw) % 12 == 0
    for i in range(0, len(raw), 12):
        begin, end, unwind = struct.unpack_from('<III', raw, i)
        if adjustment:
            # No C exception/termination handlers or chained unwind records
            # are emitted by this freestanding payload. Refuse unknown forms.
            assert mapped[unwind] >> 3 == 0, f'Unsupported unwind flags at {unwind:x}'
        pdata.append((begin + adjustment, end + adjustment, unwind + adjustment))
pdata.sort()
for first, second in zip(pdata, pdata[1:]): assert first[0] < first[1] <= second[0]

meta = bytearray()
def put(data, alignment=4):
    meta.extend(bytes(align(len(meta), alignment) - len(meta)))
    result = meta_rva + len(meta)
    meta.extend(data)
    return result

import_rva = put(imports)
reloc_rva = put(relocations)
pdata_bytes = b''.join(struct.pack('<III', *record) for record in pdata)
pdata_rva = put(pdata_bytes)
functions = list(struct.unpack('<' + 'I' * orig_exports.struct.NumberOfFunctions, original.get_data(orig_exports.struct.AddressOfFunctions, orig_exports.struct.NumberOfFunctions * 4)))
names = {}
for item in orig_exports.symbols:
    assert not item.forwarder
    if item.name: names[item.name] = item.ordinal - orig_exports.struct.Base
functions[names[b'DllMain']] = shift + exports['PMHQLocalSignExportDllMain']
for item in helper.DIRECTORY_ENTRY_EXPORT.symbols:
    assert item.name and not item.forwarder and item.name not in names
    names[item.name] = len(functions)
    functions.append(shift + item.address)
export_rva = put(bytes(40))
dll_name_rva = put(b'pmhq.dll\0', 1)
function_rva = put(struct.pack('<' + 'I' * len(functions), *functions))
ordered = sorted(names)
name_strings = [put(name + b'\0', 1) for name in ordered]
name_rva = put(struct.pack('<' + 'I' * len(name_strings), *name_strings))
ordinal_rva = put(struct.pack('<' + 'H' * len(ordered), *[names[name] for name in ordered]), 2)
struct.pack_into('<IIHHIIIIIII', meta, export_rva - meta_rva, 0, 0, 0, 0, dll_name_rva, orig_exports.struct.Base, len(functions), len(ordered), function_rva, name_rva, ordinal_rva)
export_size = meta_rva + len(meta) - export_rva

result = bytearray(original_data)
sections = []
for i, section in enumerate(helper.sections):
    if section.Name.rstrip(b'\0') == b'.buildid':
        continue  # Payload debug/PDB metadata is not used by the merged DLL.
    data = bytes(mapped[section.VirtualAddress:section.VirtualAddress + section.SizeOfRawData])
    sections.append((f'.ls{i}'.encode(), shift + section.VirtualAddress, section.Misc_VirtualSize, data, section.Characteristics))
sections.append((b'.lsmeta', meta_rva, len(meta), bytes(meta), 0x40000040))
table_offset = original.sections[0].get_file_offset()
section_count = len(original.sections) + len(sections)
assert table_offset + section_count * 40 <= original.OPTIONAL_HEADER.SizeOfHeaders
for i, (name, virtual, size, content, flags) in enumerate(sections):
    raw_start = align(len(result), original.OPTIONAL_HEADER.FileAlignment)
    raw_size = align(len(content), original.OPTIONAL_HEADER.FileAlignment)
    result.extend(bytes(raw_start - len(result)))
    result.extend(content)
    result.extend(bytes(raw_size - len(content)))
    header_offset = table_offset + (len(original.sections) + i) * 40
    struct.pack_into('<8sIIIIIIHHI', result, header_offset, name.ljust(8,b'\0'), size, virtual, raw_size, raw_start, 0, 0, 0, 0, flags)

def set_field(structure, name, format, value):
    struct.pack_into(format, result, structure.get_field_absolute_offset(name), value)
set_field(original.FILE_HEADER, 'NumberOfSections', '<H', section_count)
set_field(original.OPTIONAL_HEADER, 'AddressOfEntryPoint', '<I', shift + exports['PMHQLocalSignBoot'])
set_field(original.OPTIONAL_HEADER, 'SizeOfImage', '<I', align(meta_rva + len(meta), 0x1000))
set_field(original.OPTIONAL_HEADER, 'SizeOfCode', '<I', original.OPTIONAL_HEADER.SizeOfCode + sum(align(len(s[3]),0x200) for s in sections if s[4] & 0x20))
set_field(original.OPTIONAL_HEADER, 'SizeOfInitializedData', '<I', original.OPTIONAL_HEADER.SizeOfInitializedData + sum(align(len(s[3]),0x200) for s in sections if s[4] & 0x40))
for index, rva, size in [(0,export_rva,export_size),(1,import_rva,len(imports)),(3,pdata_rva,len(pdata_bytes)),(5,reloc_rva,len(relocations)),(4,0,0),(12,0,0)]:
    directory = original.OPTIONAL_HEADER.DATA_DIRECTORY[index]
    set_field(directory,'VirtualAddress','<I',rva)
    set_field(directory,'Size','<I',size)
checked = pefile.PE(data=result)
set_field(original.OPTIONAL_HEADER, 'CheckSum', '<I', checked.generate_checksum())
out = args.output
out.write_bytes(result)
checked = pefile.PE(str(out))
assert checked.verify_checksum()
assert len(checked.DIRECTORY_ENTRY_EXPORT.symbols) == len(names)
assert len(checked.DIRECTORY_ENTRY_IMPORT) == len(original.DIRECTORY_ENTRY_IMPORT) + len(helper.DIRECTORY_ENTRY_IMPORT)
for old in original.sections:
    assert checked.get_data(old.VirtualAddress,old.SizeOfRawData) == original.get_data(old.VirtualAddress,old.SizeOfRawData), old.Name
report = {'original_sha256': ORIGINAL_HASH, 'patched_sha256': hashlib.sha256(result).hexdigest(), 'bytes':len(result), 'payload_shift':hex(shift), 'original_entry':hex(original.OPTIONAL_HEADER.AddressOfEntryPoint), 'new_entry':hex(checked.OPTIONAL_HEADER.AddressOfEntryPoint), 'original_code_and_data_sections_preserved':True, 'exports':{e.name.decode():hex(e.address) for e in checked.DIRECTORY_ENTRY_EXPORT.symbols}, 'imports':[d.dll.decode() for d in checked.DIRECTORY_ENTRY_IMPORT]}
(out.parent/'merge-report.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report,indent=2))
