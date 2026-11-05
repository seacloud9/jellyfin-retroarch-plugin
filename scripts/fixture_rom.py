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


def build_gba_rom():
    """Game Boy Advance save-counter: battery SRAM at 0x0E000000 holds RB01 and a counter at offset 4.

    It then shows moving stripes whose red tint follows the counter. ARM instructions are encoded directly. The "SRAM_V" marker tells emulators which save type the cartridge has.
    """
    words = []

    def at():
        return 0x08000000 + 4 * len(words)

    def ldrb(rd, rn, offset): words.append(0xE5D00000 | rn << 16 | rd << 12 | offset)
    def strb(rd, rn, offset): words.append(0xE5C00000 | rn << 16 | rd << 12 | offset)
    def cmp(rn, value): words.append(0xE3500000 | rn << 16 | value)
    def mov(rd, value): words.append(0xE3A00000 | rd << 12 | value)
    def add(rd, rn, value): words.append(0xE2800000 | rn << 16 | rd << 12 | value)

    def branch(condition, target):
        words.append(condition << 28 | 0x0A000000 | ((target - (at() + 8)) >> 2) & 0xFFFFFF)

    def fix(index, condition, target):
        words[index] = condition << 28 | 0x0A000000 | ((target - (0x08000000 + 4 * index + 8)) >> 2) & 0xFFFFFF

    branch(0xE, 0x080000C0)                      # entry point: skip the header
    words.extend([0] * (0xC0 // 4 - 1))
    words.append(0xE3A0040E)                     # mov r0, #0x0E000000
    pending = []
    for offset, char in enumerate(b'RB01'):
        ldrb(1, 0, offset)
        cmp(1, char)
        pending.append(len(words))
        words.append(0)                          # bne init (fixed below)
    pending_increment = len(words)
    words.append(0)                              # b increment
    init = at()
    for offset, char in enumerate(b'RB01'):
        mov(1, char)
        strb(1, 0, offset)
    mov(1, 0)
    strb(1, 0, 4)
    increment = at()
    ldrb(1, 0, 4)
    add(1, 1, 1)
    strb(1, 0, 4)
    # Display: mode 3 bitmap of moving diagonal stripes, tinted red by the boot counter.
    words.append(0xE1A0B181)                     # mov r11, r1, lsl #3
    words.append(0xE20BB01F)                     # and r11, r11, #31
    words.append(0xE3A02404)                     # mov r2, #0x04000000 (DISPCNT)
    words.append(0xE3A03B01)                     # mov r3, #0x400 (background 2 on)
    add(3, 3, 3)                                 # mode 3
    words.append(0xE1C230B0)                     # strh r3, [r2]
    mov(4, 0)                                    # t = 0
    frame = at()
    words.append(0xE3A05406)                     # mov r5, #0x06000000 (VRAM)
    mov(6, 0)                                    # y = 0
    row = at()
    mov(7, 0)                                    # x = 0
    pixel = at()
    words.append(0xE0878006)                     # add r8, r7, r6
    words.append(0xE0888004)                     # add r8, r8, r4
    words.append(0xE1A091A8)                     # mov r9, r8, lsr #3
    words.append(0xE209901F)                     # and r9, r9, #31
    words.append(0xE1A0A509)                     # mov r10, r9, lsl #10 (blue)
    words.append(0xE18AA289)                     # orr r10, r10, r9, lsl #5 (green)
    words.append(0xE18AA00B)                     # orr r10, r10, r11 (red)
    words.append(0xE0C5A0B2)                     # strh r10, [r5], #2
    add(7, 7, 1)
    cmp(7, 240)
    branch(0x1, pixel)
    add(6, 6, 1)
    cmp(6, 160)
    branch(0x1, row)
    add(4, 4, 3)
    branch(0xE, frame)
    for index in pending:
        fix(index, 0x1, init)
    fix(pending_increment, 0xE, increment)
    rom = bytearray(b''.join(w.to_bytes(4, 'little') for w in words))
    rom[0xA0:0xAC] = b'RB SAVE TEST'
    rom[0xAC:0xB0] = b'ZRBE'
    rom[0xB2] = 0x96
    rom[0xBD] = -sum(rom[0xA0:0xBD]) - 0x19 & 0xFF  # header checksum
    rom += b'SRAM_V113\0\0\0'
    rom += bytes(-len(rom) % 0x4000)
    return bytes(rom)


def build_genesis_rom():
    """Genesis / Mega Drive save-counter: battery SRAM on odd bytes from 0x200001 holds R, B, 0, 1 and a counter.

    It then cycles the background color, tinted red by the counter. 68000 instructions are encoded directly; the header declares the SRAM so emulators map it.
    """
    code = bytearray()

    def emit(*words):
        for word in words:
            code.extend(word.to_bytes(2, 'big'))

    emit(0x41F9, 0x0020, 0x0001)                 # lea $200001, a0
    branches = []
    for index, char in enumerate(b'RB01'):
        if index == 0: emit(0x0C10, char)        # cmpi.b #char, (a0)
        else: emit(0x0C28, char, 2 * index)      # cmpi.b #char, 2*index(a0)
        branches.append(len(code))
        emit(0x6600)                             # bne.s init (fixed below)
    skip = len(code)
    emit(0x6000)                                 # bra.s increment
    init = len(code)
    for index, char in enumerate(b'RB01'):
        if index == 0: emit(0x10BC, char)        # move.b #char, (a0)
        else: emit(0x117C, char, 2 * index)      # move.b #char, 2*index(a0)
    emit(0x4228, 8)                              # clr.b 8(a0)
    increment = len(code)
    emit(0x5228, 8)                              # addq.b #1, 8(a0)
    # Display: cycle the background color; its red part follows the boot counter.
    emit(0x43F9, 0x00C0, 0x0004)                 # lea $C00004, a1 (VDP control)
    emit(0x45F9, 0x00C0, 0x0000)                 # lea $C00000, a2 (VDP data)
    for register in (0x8004, 0x8144, 0x8700, 0x8F02):
        emit(0x32BC, register)                   # move.w #register, (a1)
    emit(0x7000)                                 # moveq #0, d0
    emit(0x1228, 8)                              # move.b 8(a0), d1
    emit(0xD201, 0xD201)                         # add.b d1, d1 (twice: counter * 4)
    emit(0x0241, 0x000E)                         # andi.w #$000E, d1
    loop = len(code)
    emit(0x22BC, 0xC000, 0x0000)                 # move.l #$C0000000, (a1) (write CRAM 0)
    emit(0x3400)                                 # move.w d0, d2
    emit(0x0242, 0x0EE0)                         # andi.w #$0EE0, d2
    emit(0x8441)                                 # or.w d1, d2
    emit(0x3482)                                 # move.w d2, (a2)
    emit(0x0640, 0x0020)                         # addi.w #$0020, d0
    emit(0x263C, 0x0000, 0x4000)                 # move.l #$4000, d3
    emit(0x5383, 0x66FC)                         # subq.l #1, d3; bne.s (delay)
    emit(0x6000 | (loop - (len(code) + 2)) & 0xFF) # bra.s loop
    idle = len(code)
    emit(0x60FE)                                 # bra.s * (exceptions land here)
    for at in branches:
        code[at + 1] = (init - (at + 2)) & 0xFF
    code[skip + 1] = (increment - (skip + 2)) & 0xFF
    rom = bytearray(0x200)
    rom[0:4] = (0x00FFFE00).to_bytes(4, 'big')   # initial stack pointer
    rom[4:8] = (0x00000200).to_bytes(4, 'big')   # entry point
    for vector in range(2, 64):                  # every exception lands in the idle loop
        rom[vector * 4:vector * 4 + 4] = (0x200 + idle).to_bytes(4, 'big')
    header = {0x100: b'SEGA MEGA DRIVE ', 0x110: b'(C)RBSV 2026.NOV', 0x120: b'RB SAVE TEST'.ljust(48),
              0x150: b'RB SAVE TEST'.ljust(48), 0x180: b'GM RB000001-00', 0x190: b'J'.ljust(16), 0x1F0: b'JUE'.ljust(16)}
    for offset, value in header.items():
        rom[offset:offset + len(value)] = value
    rom[0x1B0:0x1BC] = b'RA' + bytes([0xF8, 0x20]) + (0x200001).to_bytes(4, 'big') + (0x203FFF).to_bytes(4, 'big')
    rom[0x1C8:0x1D0] = b' ' * 8
    rom += code
    rom += bytes(-len(rom) % 0x20000)
    rom[0x1A0:0x1A8] = (0).to_bytes(4, 'big') + (len(rom) - 1).to_bytes(4, 'big')
    return bytes(rom)


def write_gba_fixture(destination):
    return _write(destination, build_gba_rom())


def write_genesis_fixture(destination):
    return _write(destination, build_genesis_rom())


def _write(destination, data):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


if __name__ == '__main__':
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('artifacts/fixtures/save-counter.nes')
    writer = {'.gb': write_gb_fixture, '.gba': write_gba_fixture, '.md': write_genesis_fixture}.get(destination.suffix, write_fixture)
    print(f'{writer(destination)}  {destination}')
