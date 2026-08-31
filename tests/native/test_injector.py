import struct
import sys
from types import SimpleNamespace

import pytest

from src.native import injector


def _field(**values):
    return SimpleNamespace(**values)


def _fake_pe(*, with_imports: bool = False):
    pe = _field(
        OPTIONAL_HEADER=_field(
            ImageBase=0x180000000,
            SizeOfImage=0x3000,
            SizeOfHeaders=0x200,
            AddressOfEntryPoint=0x1010,
            DATA_DIRECTORY=[_field(VirtualAddress=0, Size=0) for _ in range(16)],
        ),
        FILE_HEADER=_field(Machine=injector.IMAGE_FILE_MACHINE_AMD64),
        sections=[
            _field(
                VirtualAddress=0x1000,
                Misc_VirtualSize=0x100,
                SizeOfRawData=4,
                PointerToRawData=0x200,
                Characteristics=injector.IMAGE_SCN_MEM_READ | injector.IMAGE_SCN_MEM_EXECUTE,
            )
        ],
        DIRECTORY_ENTRY_BASERELOC=[],
    )
    if with_imports:
        pe.DIRECTORY_ENTRY_IMPORT = [
            _field(
                dll=b"KERNEL32.dll",
                imports=[
                    _field(
                        address=pe.OPTIONAL_HEADER.ImageBase + 0x1080, name=b"Sleep", ordinal=None
                    ),
                    _field(address=pe.OPTIONAL_HEADER.ImageBase + 0x1088, name=None, ordinal=17),
                ],
            )
        ]
    return pe


def test_map_handle_shape():
    handle = injector.MapHandle(base=0x1000, entry=0x1100, exception_table=0x1200, size=0x3000)
    assert (handle.base, handle.entry, handle.exception_table, handle.size) == (
        0x1000,
        0x1100,
        0x1200,
        0x3000,
    )


def test_prepare_image_copies_headers_sections_and_reports_entry():
    pe = _fake_pe()
    raw = bytearray(0x204)
    raw[:4] = b"MZ!!"
    raw[0x200:0x204] = b"CODE"

    prepared = injector._prepare_image(pe, bytes(raw), 0x180000000, lambda *_: 0)

    assert prepared.data[:4] == b"MZ!!"
    assert prepared.data[0x1000:0x1004] == b"CODE"
    assert prepared.entry == 0x180001010
    assert prepared.exception_table == 0
    assert prepared.exception_count == 0
    assert prepared.sections[0].address == 0x180001000


def test_prepare_image_applies_dir64_relocation():
    pe = _fake_pe()
    pe.DIRECTORY_ENTRY_BASERELOC = [
        _field(entries=[_field(type=injector.IMAGE_REL_BASED_DIR64, rva=0x1020)])
    ]
    raw = bytearray(0x204)
    raw[0x200:0x204] = b"CODE"

    # The pointer lives in the virtual image, so place it through a wider raw section.
    pe.sections[0].SizeOfRawData = 0x100
    raw.extend(b"\0" * (0x200 + 0x100 - len(raw)))
    struct.pack_into("<Q", raw, 0x220, pe.OPTIONAL_HEADER.ImageBase + 0x1550)

    prepared = injector._prepare_image(pe, bytes(raw), 0x190000000, lambda *_: 0)

    assert struct.unpack_from("<Q", prepared.data, 0x1020)[0] == 0x190001550


def test_prepare_image_patches_named_and_ordinal_imports():
    pe = _fake_pe(with_imports=True)
    raw = bytearray(0x300)
    seen = []

    def resolve(module, name, ordinal):
        seen.append((module, name, ordinal))
        return 0x7FFF00001000 + len(seen)

    prepared = injector._prepare_image(pe, bytes(raw), pe.OPTIONAL_HEADER.ImageBase, resolve)

    assert seen == [
        ("KERNEL32.dll", b"Sleep", None),
        ("KERNEL32.dll", None, 17),
    ]
    assert struct.unpack_from("<Q", prepared.data, 0x1080)[0] == 0x7FFF00001001
    assert struct.unpack_from("<Q", prepared.data, 0x1088)[0] == 0x7FFF00001002


def test_prepare_image_reports_exception_directory():
    pe = _fake_pe()
    pe.OPTIONAL_HEADER.DATA_DIRECTORY[injector.IMAGE_DIRECTORY_ENTRY_EXCEPTION] = _field(
        VirtualAddress=0x1200,
        Size=24,
    )

    prepared = injector._prepare_image(pe, bytes(0x300), pe.OPTIONAL_HEADER.ImageBase, lambda *_: 0)

    assert prepared.exception_table == pe.OPTIONAL_HEADER.ImageBase + 0x1200
    assert prepared.exception_count == 2


def test_prepare_image_collects_tls_callbacks():
    pe = _fake_pe()
    pe.sections[0].SizeOfRawData = 0x100
    pe.OPTIONAL_HEADER.DATA_DIRECTORY[injector.IMAGE_DIRECTORY_ENTRY_TLS] = _field(
        VirtualAddress=0x1040,
        Size=40,
    )
    raw = bytearray(0x300)
    struct.pack_into("<Q", raw, 0x240 + 24, pe.OPTIONAL_HEADER.ImageBase + 0x1080)
    struct.pack_into("<Q", raw, 0x280, pe.OPTIONAL_HEADER.ImageBase + 0x1090)

    prepared = injector._prepare_image(pe, bytes(raw), pe.OPTIONAL_HEADER.ImageBase, lambda *_: 0)

    assert prepared.tls_callbacks == (pe.OPTIONAL_HEADER.ImageBase + 0x1090,)


def test_remote_tls_callbacks_are_recovered_from_mapped_headers():
    base = 0x180000000
    image = bytearray(0x3000)
    struct.pack_into("<I", image, 0x3C, 0x80)
    image[0x80:0x84] = b"PE\0\0"
    optional_header = 0x80 + 24
    struct.pack_into("<H", image, optional_header, injector.IMAGE_NT_OPTIONAL_HDR64_MAGIC)
    tls_entry = optional_header + 112 + injector.IMAGE_DIRECTORY_ENTRY_TLS * 16
    struct.pack_into("<II", image, tls_entry, 0x1000, 40)
    struct.pack_into("<Q", image, 0x1000 + 24, base + 0x1100)
    struct.pack_into("<QQ", image, 0x1100, base + 0x1200, 0)

    class FakeProcess:
        def read(self, address, size):
            offset = address - base
            return bytes(image[offset : offset + size])

    handle = injector.MapHandle(base=base, entry=base + 0x1010, exception_table=0, size=len(image))

    assert injector._remote_tls_callbacks(FakeProcess(), handle) == (base + 0x1200,)


@pytest.mark.skipif(sys.platform == "win32", reason="仅验证非 Windows 平台边界")
def test_inject_rejects_non_windows_before_touching_dll(tmp_path):
    missing = tmp_path / "missing.dll"
    with pytest.raises(OSError, match="Windows"):
        injector.inject(1234, str(missing))
