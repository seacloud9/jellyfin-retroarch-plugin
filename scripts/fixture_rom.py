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


# The cartridge header logo every Game Boy program carries (checked by the boot ROM).
GB_LOGO = bytes.fromhex(
    'CEED6666CC0D000B03730083000C000D0008111F8889000EDCCC6EE6DDDDD999'
    'BBBB67636E0EECCCDDDC999FBBB9333E')


def build_gb_rom():
    """Original DMG save-counter program on an MBC1+RAM+BATTERY cartridge, the Game Boy twin of build_rom().

    Cartridge RAM at $A000 holds the signature RB01 and a counter at offset 4, incremented once per boot.
    The screen is black on odd boots and white on even boots.
    """
    code = bytearray()
    labels, patches = {}, []

    def label(name):
        labels[name] = 0x150 + len(code)

    def jr(opcode, target):
        code.extend([opcode, 0])
        patches.append((len(code) - 1, target))

    label('start')
    code.extend([0xF3, 0x31, 0xFE, 0xFF])          # DI; LD SP,$FFFE
    code.extend([0x3E, 0x0A, 0xEA, 0x00, 0x00])    # LD A,$0A; LD ($0000),A: enable cartridge RAM
    for offset, value in enumerate(b'RB01'):
        code.extend([0xFA, offset, 0xA0, 0xFE, value])  # LD A,($A000+n); CP n
        jr(0x20, 'initialize')                           # JR NZ
    code.extend([0x21, 0x04, 0xA0, 0x34])          # LD HL,$A004; INC (HL): exactly once per boot
    jr(0x18, 'display')                            # JR
    label('initialize')
    for offset, value in enumerate(b'RB01\x01'):
        code.extend([0x3E, value, 0xEA, offset, 0xA0])  # LD A,n; LD ($A000+n),A
    label('display')
    code.extend([0xFA, 0x04, 0xA0, 0xE6, 0x01])    # LD A,($A004); AND 1
    jr(0x28, 'even')                               # JR Z
    code.extend([0x3E, 0xFF])                      # LD A,$FF: odd boot, every shade black
    jr(0x18, 'palette')
    label('even')
    code.extend([0x3E, 0x00])                      # LD A,$00: even boot, white
    label('palette')
    code.extend([0xE0, 0x47])                      # LDH ($47),A: background palette
    code.extend([0xAF, 0xEA, 0x00, 0x00])          # XOR A; LD ($0000),A: disable cartridge RAM
    label('forever')
    jr(0x18, 'forever')
    for offset, target in patches:
        distance = labels[target] - (0x150 + offset + 1)
        if not -128 <= distance <= 127:
            raise ValueError('Branch out of range')
        code[offset] = distance & 0xFF

    rom = bytearray(32768)
    rom[0x100:0x104] = bytes([0x00, 0xC3, 0x50, 0x01])  # NOP; JP $0150
    rom[0x104:0x134] = GB_LOGO
    rom[0x134:0x143] = b'RB SAVE COUNTER'.ljust(15, b'\0')
    rom[0x143] = 0x00                                   # DMG only
    rom[0x147], rom[0x148], rom[0x149] = 0x03, 0x00, 0x02  # MBC1+RAM+BATTERY, 32 KiB ROM, 8 KiB RAM
    rom[0x14A] = 0x01                                   # non-Japanese
    rom[0x150:0x150 + len(code)] = code
    checksum = 0
    for byte in rom[0x134:0x14D]:
        checksum = (checksum - byte - 1) & 0xFF
    rom[0x14D] = checksum
    total = (sum(rom) - rom[0x14E] - rom[0x14F]) & 0xFFFF
    rom[0x14E:0x150] = struct.pack('>H', total)
    return bytes(rom)


def write_gb_fixture(destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = build_gb_rom()
    destination.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


def write_fixture(destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = build_rom()
    destination.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


if __name__ == '__main__':
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('artifacts/fixtures/save-counter.nes')
    writer = write_gb_fixture if destination.suffix == '.gb' else write_fixture
    print(f'{writer(destination)}  {destination}')
