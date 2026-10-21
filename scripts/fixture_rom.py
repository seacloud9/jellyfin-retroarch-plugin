"""Build an original NROM battery-RAM fixture. No external ROM/assembler required."""
import hashlib
from pathlib import Path
import struct
import sys


def build_rom():
    code = bytearray()
    labels = {}
    patches = []

    def emit(*values):
        code.extend(values)

    def label(name):
        labels[name] = 0x8000 + len(code)

    def branch(opcode, target):
        emit(opcode, 0)
        patches.append((len(code) - 1, target, True))

    def jump(target):
        emit(0x4C, 0, 0)
        patches.append((len(code) - 2, target, False))

    # SEI; CLD; LDX #$FF; TXS; INX. Disable rendering, NMI and APU.
    label('reset')
    emit(0x78, 0xD8, 0xA2, 0xFF, 0x9A, 0xE8)
    for address in (0x2000, 0x2001, 0x4010, 0x4015):
        emit(0x8E, address & 0xFF, address >> 8)  # STX abs
    emit(0xA9, 0x40, 0x8D, 0x17, 0x40)  # LDA #$40; STA $4017

    # Battery-backed RAM starts at $6000. Verify signature before incrementing.
    for offset, value in enumerate(b'RB01'):
        emit(0xAD, offset, 0x60, 0xC9, value)  # LDA abs; CMP immediate
        branch(0xD0, 'initialize')  # BNE
    emit(0xEE, 0x04, 0x60)  # INC $6004 exactly once per reset
    jump('display')

    label('initialize')
    for offset, value in enumerate(b'RB01\x01'):
        emit(0xA9, value, 0x8D, offset, 0x60)  # LDA immediate; STA abs

    label('display')
    # Wait two vblanks so the PPU accepts palette writes after power-on.
    emit(0x2C, 0x02, 0x20)  # BIT $2002 clears an already-set vblank flag
    for name in ('vblank1', 'vblank2'):
        label(name)
        emit(0x2C, 0x02, 0x20)
        branch(0x10, name)  # BPL until vblank
    # Universal background color: blue on odd boots, green on even boots.
    emit(0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20)
    emit(0xAD, 0x04, 0x60, 0x29, 0x01)  # LDA $6004; AND #1
    branch(0xF0, 'even')
    emit(0xA9, 0x21)
    jump('palette')
    label('even')
    emit(0xA9, 0x29)
    label('palette')
    emit(0x8D, 0x07, 0x20)  # STA $2007
    label('forever')
    jump('forever')

    for offset, target, relative in patches:
        address = labels[target]
        if relative:
            distance = address - (0x8000 + offset + 1)
            if not -128 <= distance <= 127:
                raise ValueError('Branch out of range')
            code[offset] = distance & 0xFF
        else:
            code[offset:offset + 2] = struct.pack('<H', address)

    prg = code + bytes(16384 - len(code))
    # NROM-128 mirrors the 16 KiB bank at $C000; vector locations are in its end.
    prg[-6:] = struct.pack('<HHH', labels['reset'], labels['reset'], labels['reset'])
    # iNES 1.0: one PRG bank, one CHR bank, battery-backed 8 KiB RAM, mapper 0.
    header = b'NES\x1a' + bytes([1, 1, 2, 0, 1]) + bytes(7)
    return header + prg + bytes(8192)


def write_fixture(destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = build_rom()
    destination.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


if __name__ == '__main__':
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('artifacts/fixtures/save-counter.nes')
    print(f'{write_fixture(destination)}  {destination}')
