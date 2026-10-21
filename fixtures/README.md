# Save-counter NES fixture

Generate with `python3 scripts/fixture_rom.py`. The output goes to ignored `artifacts/fixtures/save-counter.nes` by default. The source is an original, purpose-built test program with no commercial ROM code, graphics, music, or external binary dependencies.

This NROM-128/iNES fixture uses battery-backed RAM at `$6000`. It initializes a four-byte `RB01` signature and a counter byte at offset 4. Each later reset increments the counter exactly once. It displays blue on odd boots and green on even boots. It has no gameplay or input handling; its sole purpose is to establish that actual emulation and save loading/flushing work.

The generator contains the 6502 instructions, branch fixups, reset vectors, and iNES header directly, so the binary is deterministic without installing an assembler. The native smoke test checks signature and counter bytes in the emulator-produced `.srm` file before and after a second process launch. Do not substitute arbitrary fixture bytes or a process mock for that test.
