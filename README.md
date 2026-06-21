# BBC Model B Emulator

A BBC Micro Model B emulator written in C#/.NET. The project emulates a 6502-based BBC B with OS 1.20, BASIC II, Acorn DFS, BBC video and sound, keyboard matrix input, disc loading, mouse support, and both switched and analogue joystick input through SDL.

The emulator is currently focused on practical game compatibility and interactive debugging. The `Software` directory contains a working set of DFS disc images used during development, including Repton, Elite, Arcadians, AMX Art, and other BBC titles.

## Requirements

- .NET 9 SDK
- SDL2 runtime support through the `ppy.SDL2-CS` package
- BBC ROM images in `ROMS`

The project expects these ROM files:

```text
ROMS/OS12.rom
ROMS/BASIC2.rom
ROMS/DFS-0.9.rom
ROMS/AMXMSE331.rom
```

`AMXMSE331.rom` is optional for normal BBC use, but it is required for AMX mouse software and for AMX-aware programs such as Repton 3's editor mouse mode.

## Build

```bash
dotnet build BBC_MODEL_B.csproj
```

## Run

Start the emulator at BASIC:

```bash
dotnet run --project BBC_MODEL_B.csproj
```

Mount and boot a DFS disc image:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Software/Repton3.ssd
```

Mount a disc without running its boot script:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot Software/AMXArt.ssd
```

Mount a raw host file:

```bash
dotnet run --project BBC_MODEL_B.csproj -- path/to/file
```

Run headless for smoke testing:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --headless-ms 7000 Software/Repton3.ssd
```

Print the inferred auto-load command for a DFS disc:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --print-autoload Software/Repton3.ssd
```

Slow down or speed up the emulated CPU:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --speed 50% Software/Elite.ssd
dotnet run --project BBC_MODEL_B.csproj -- --speed 0.5 Software/Elite.ssd
```

## Command-Line Options

```text
--disc PATH
--disk PATH
--file PATH        Mount a disc image or host file.

--boot-disc        Auto-run the mounted disc boot script. This is the default.
--no-boot-disc
--no-autoboot      Mount the disc but stay at BASIC.

--headless-ms N    Run without a window for N milliseconds.
--speed VALUE      CPU speed scale, for example 0.5 or 50%.
                   The OS starts at normal speed, then the scale applies once input is enabled.
--print-autoload   Print the inferred boot command for a DFS disc image.
```

Plain paths are also accepted:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Software/Elite.ssd
```

## Controls

### Host Shortcuts

```text
F12              BBC BREAK
Shift+F12        BBC Shift-BREAK
Ctrl+F12         BBC Ctrl-BREAK
F11              Toggle scanline overlay
Ctrl+S / Cmd+S   Save screenshot to Screenshots
Ctrl+T           Toggle 8271 disc trace logging
Ctrl+L / Cmd+L   Open host file picker and mount selected file
Ctrl+V / Cmd+V   Paste host clipboard text into the BBC keyboard buffer
```

### Keyboard Mapping Notes

Most host keys map directly to BBC keyboard matrix positions. A few useful BBC-specific mappings are:

```text
Host arrow keys   BBC cursor keys
F1-F10            BBC function keys
F12               BBC BREAK
§                 BBC COPY
Backspace/Delete  BBC DELETE
```

On macOS keyboards, `§` is mapped to the BBC `COPY` key.

### Joystick Input

SDL game controllers and raw joysticks are opened automatically.

Keyboard fallback:

```text
Left arrow   Joystick left
Right arrow  Joystick right
Up arrow     Joystick up
Down arrow   Joystick down
Space        Fire
```

Physical controller mapping:

```text
Left stick X/Y or raw axes 0/1   Analogue joystick
D-pad or hat                     Switched joystick directions
Button A or raw button 0          Fire
```

The analogue joystick mapping follows the BBC `ADVAL` convention used by the emulator:

```text
ADVAL(1): left  = 65535, right = 0
ADVAL(2): up    = 65535, down  = 0
ADVAL(0): fire  = 1 when pressed
```

Switched joystick input is exposed through the user VIA as active-low lines:

```text
PB0 = Up
PB1 = Down
PB2 = Left
PB3 = Right
PB4 = Fire
```

## Mouse And AMX Support

The emulator supports host mouse/trackpad capture for AMX mouse software.

Relevant commands inside the emulated BBC:

```text
*MOUSE ON
*MOUSE OFF
*POINTER ON
*POINTER OFF
```

When mouse support is enabled, SDL relative mouse mode is used so the emulated pointer can continue moving even when the host pointer would otherwise hit the edge of the emulator window.

AMX software needs the AMX ROM loaded. If `ROMS/AMXMSE331.rom` is present, it is loaded into sideways ROM bank 13 and `*MOUSE` commands are passed to the ROM while still updating host mouse capture state.

### AMX Art

`Software/AMXArt.ssd` can be launched directly:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Software/AMXArt.ssd
```

If you boot it manually, the useful sequence is:

```text
*KEY 10 CHAIN "!MENU"|M
*POINTER ON
*MOUSE ON
*BREAK
CHAIN "!MENU"
```

The current emulator handles the boot `*EXEC !BOOT` flow and queues the soft-key continuation after `*BREAK`.

## Disc Images And Loading

DFS `.ssd` and `.dsd` images can be mounted from the command line or by using the host file picker shortcut.

The emulator includes an 8271 disc controller model and a host filing-system helper. The host helper intercepts selected MOS file operations where this improves compatibility or makes raw host-file loading practical.

Boot behavior:

- DFS boot option 3 with `!BOOT` queues `*EXEC !BOOT`.
- If no boot script is available, the emulator infers a likely load/run command.
- Use `--no-autoboot` to inspect or manually run a disc.

## Diagnostics

### 8271 Disc Trace

Press `Ctrl+T` while the emulator is running to toggle disc trace logging. Trace files are written to the project root.

This is useful for long loader paths such as Repton 3, where the trace can be started immediately before a failing load step.

### Environment Traces

Some focused diagnostic traces are controlled by environment variables:

```bash
env BBC_OSCLI_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Software/Repton3.ssd
env BBC_MOUSE_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Software/AMXArt.ssd
env BBC_OSCLI_TRACE=1 BBC_MOUSE_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Software/AMXArt.ssd
```

`BBC_OSCLI_TRACE=1` logs host filing-system OSCLI activity such as `*EXEC`, `*MOUSE`, `*POINTER`, `*FX`, and file matches.

`BBC_MOUSE_TRACE=1` logs host mouse movement and button data while mouse emulation is active.

### Screenshots

Press `Ctrl+S` or `Cmd+S` to write a PNG screenshot to:

```text
Screenshots/
```

## Project Layout

Terminology note: BBC Micro documentation commonly calls the memory-mapped I/O page at `&FE00-&FEFF` "SHEILA". Comments that mention SHEILA are referring to this hardware I/O address range, not to a separate chip.

```text
6502/                 6502 CPU, registers, flags, and bus interfaces
ROMS/                 BBC OS, BASIC, DFS, and optional AMX ROM images
Software/             DFS disc images used for testing and play
Screenshots/          Runtime screenshot output
uPD7002_ADC.cs        BBC uPD7002 analogue-to-digital converter
TapeACIAStub.cs       Cassette/serial ACIA stub
Intel8271_Disk.cs     Acorn 8271 DFS disc controller model
Display.cs            SDL window, rendering, keyboard, mouse, joystick input
HostFilingSystem.cs   Host-backed file/disc helper and OSCLI interception
Emulator.cs           Emulator coordinator, memory map, CLI, firmware hooks
SAA5050_Font.cs       Mode 7 teletext font data
SN76489_Sound.cs      Sound generator support
SystemVia.cs          BBC system VIA
UserVia.cs            BBC user VIA, AMX mouse, switched joystick input
HD6845_Video.cs       Video/ULA/CRTC rendering
```

## Compatibility Notes

The emulator is under active development, and compatibility is verified game by game. Some current observations:

- Repton 3 boots and its editor works with keyboard, switched joystick, analogue joystick, and AMX mouse where the software supports it.
- Arcadians confirms switched joystick and fire support through the SDL joystick path.
- Elite uses analogue joystick input for flight/navigation, but the BBC version's own instructions list laser fire as keyboard `A`; joystick fire may not be used for lasers by the game itself.
- AMX mouse support depends on the AMX ROM, not just the host mouse capture path.

## Legal Note

BBC Micro ROMs and commercial software images remain the property of their respective rights holders. This emulator is for educational and preservation-oriented development.
