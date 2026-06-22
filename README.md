# BBC Model B Emulator

A C#/.NET BBC Micro Model B emulator. I have tried to build it around the details that make BBC software feel right rather than merely start: the 2 MHz NMOS 6502, OS 1.20, BASIC II, Acorn DFS, the 8271 disc controller, the system and user 6522 VIAs, the BBC video hardware, the SN76489 sound chip, and the keyboard matrix.

This has been a practical, slightly obsessive project. Compatibility is checked the slow way: boot the game, watch the loader, listen for the ugly little timing clues, and then go back through the hardware path until the mistake shows itself. The aim is not architectural purity; it is to make more BBC software behave as if it has landed on familiar iron.

## Requirements

- .NET 9 SDK
- SDL2 runtime support through `ppy.SDL2-CS`
- BBC ROM images in `ROMS/`

Expected ROMs:

```text
ROMS/OS12.rom
ROMS/BASIC2.rom
ROMS/DFS-0.9.rom
ROMS/AMXMSE331.rom
```

`AMXMSE331.rom` is optional for a normal BBC boot. I keep it listed because AMX software is a useful test of the mouse path, especially modes such as the Repton 3 editor.

## Build

```bash
dotnet build BBC_MODEL_B.csproj
```

## Run

Start at BASIC:

```bash
dotnet run --project BBC_MODEL_B.csproj
```

Boot a DFS disc image:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Games/Superior/Repton3.ssd
```

Mount a disc but stay at BASIC:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot Games/Misc/AMXArt.ssd
```

Mount a raw host file:

```bash
dotnet run --project BBC_MODEL_B.csproj -- path/to/file
```

Run without a window for a quick smoke test:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --headless-ms 7000 Games/Superior/Repton3.ssd
```

Ask the emulator what it would auto-run from a DFS image:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --print-autoload Games/Superior/Repton3.ssd
```

Change CPU speed:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --speed 50% Games/Acornsoft/Elite.ssd
dotnet run --project BBC_MODEL_B.csproj -- --speed 0.5 Games/Acornsoft/Elite.ssd
```

The speed scale is held back until MOS has reached its input path. That is intentional: the first few seconds are part of the machine's character, especially the startup sound, so I let that happen at normal speed before applying the requested scale.

## Command-Line Options

```text
--disc PATH
--disk PATH
--file PATH        Mount a DFS image or host file.

--boot-disc        Run the mounted disc's boot path. This is the default.
--no-boot-disc
--no-autoboot      Mount the disc and leave the BBC at BASIC.

--headless-ms N    Run without a window for N milliseconds.
--speed VALUE      CPU speed scale, for example 0.5 or 50%.
--print-autoload   Print the inferred boot command for a DFS image.
```

Plain paths are accepted too:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Games/Acornsoft/Elite.ssd
```

## Controls

### Host Shortcuts

```text
F12              BBC BREAK
Shift+F12        BBC Shift-BREAK
Ctrl+F12         BBC Ctrl-BREAK
F11              Toggle scanline overlay
Ctrl+S / Cmd+S   Save screenshot to Screenshots/
Ctrl+T           Toggle 8271 disc trace logging
Ctrl+L / Cmd+L   Open host file picker and mount selected file
Ctrl+V / Cmd+V   Paste host clipboard text into the BBC keyboard buffer
```

### Keyboard

Most keys go through the BBC keyboard matrix rather than being treated as host characters. That matters, because plenty of BBC software reads the matrix directly. A few mappings are worth knowing:

```text
Host arrow keys   BBC cursor keys
F1-F10            BBC function keys
F12               BBC BREAK
§                 BBC COPY
Backspace/Delete  BBC DELETE
```

On macOS, `§` is used for the BBC `COPY` key.

### Joysticks

SDL game controllers and raw joysticks are opened automatically. The keyboard fallback is deliberately simple:

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

BBC analogue joystick reads follow the `ADVAL` convention used here:

```text
ADVAL(1): left  = 65535, right = 0
ADVAL(2): up    = 65535, down  = 0
ADVAL(0): fire  = 1 when pressed
```

Switched joysticks sit on the user 6522 VIA as active-low lines:

```text
PB0 = Up
PB1 = Down
PB2 = Left
PB3 = Right
PB4 = Fire
```

## Mouse And AMX

AMX mouse support uses host relative mouse capture, so the emulated pointer can keep moving even when the host pointer would otherwise hit the edge of the window. It is one of those corners where a tiny host convenience has to disappear behind the BBC's expectations.

Inside the BBC, the useful commands are:

```text
*MOUSE ON
*MOUSE OFF
*POINTER ON
*POINTER OFF
```

With `ROMS/AMXMSE331.rom` present, the ROM is loaded in sideways bank 13. `*MOUSE` commands update host capture state, while `*POINTER` is left to the AMX software/ROM side of the world.

`Games/Misc/AMXArt.ssd` can be launched directly:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Games/Misc/AMXArt.ssd
```

For a manual AMX Art start, this is the useful sequence:

```text
*KEY 10 CHAIN "!MENU"|M
*POINTER ON
*MOUSE ON
*BREAK
CHAIN "!MENU"
```

The emulator handles the boot `*EXEC !BOOT` path and queues the soft-key continuation after `*BREAK`.

## Discs And Loading

DFS `.ssd` and `.dsd` images can be mounted from the command line, drag/drop, or the host file picker.

The loading path is deliberately split in two, because the neat version of this design would be less honest than the useful one:

- `Intel8271_Disk` models the Acorn 8271-facing hardware that DFS talks to.
- `HostFilingSystem` sits outside DFS and only handles the small MOS shortcuts that make a raw host file practical to load.

Boot behaviour:

- DFS option 3 with `!BOOT` queues `*EXEC !BOOT`.
- If no boot script is present, the emulator infers a likely load/run command.
- Use `--no-autoboot` when you want to inspect a disc manually.

## Diagnostics

### 8271 Disc Trace

Press `Ctrl+T` while the emulator is running to toggle disc trace logging. Trace files are written to the project root.

I usually start this just before a loader step that fails or hangs. A trace from power-on can be comforting, but it is often mostly noise.

### Environment Traces

Focused traces are controlled with environment variables:

```bash
env BBC_OSCLI_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Games/Superior/Repton3.ssd
env BBC_MOUSE_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Games/Misc/AMXArt.ssd
env BBC_OSCLI_TRACE=1 BBC_MOUSE_TRACE=1 dotnet run --project BBC_MODEL_B.csproj -- Games/Misc/AMXArt.ssd
```

`BBC_OSCLI_TRACE=1` follows host filing-system OSCLI activity such as `*EXEC`, `*MOUSE`, `*POINTER`, `*FX`, and file matches.

`BBC_MOUSE_TRACE=1` follows host mouse movement and button data while mouse emulation is active.

### Screenshots

Press `Ctrl+S` or `Cmd+S` to write a PNG to:

```text
Screenshots/
```

## Project Layout

BBC documentation traditionally calls the memory-mapped I/O page at `&FE00-&FEFF` `SHEILA`. I use the same name here. It means the I/O page, not a separate chip.

```text
6502/                 NMOS 6502 core, registers, flags, and memory bus
ROMS/                 OS, BASIC, DFS, and optional AMX ROMs
Games/                DFS disc images used for testing and play
Screenshots/          Runtime screenshot output
uPD7002_ADC.cs        Analogue joystick/paddle converter at &FEC0-&FEC3
TapeACIAStub.cs       Cassette/RS423 ACIA response for software probes
Intel8271_Disk.cs     Acorn 8271 DFS disc controller surface
Display.cs            SDL window plus BBC keyboard, mouse, and joystick input
HostFilingSystem.cs   MOS shortcuts for loading a raw host file
Emulator.cs           Memory map, ROM loading, CLI, and hardware wiring
SAA5050_Font.cs       Mode 7 teletext glyphs
SN76489_Sound.cs      SN76489 PSG and internal speaker output
System6522Via.cs      System 6522 VIA: keyboard, slow bus, timers, VSYNC
User6522Via.cs        User 6522 VIA: user port, joystick, mouse pulses
HD6845_Video.cs       CRTC, Video ULA, Mode 7, and framebuffer rendering
```

## Compatibility Notes

Compatibility is earned one title at a time. A few current notes from the games I keep coming back to:

- Repton 3 boots, and its editor works with keyboard, switched joystick, analogue joystick, and AMX mouse where the software supports it.
- Arcadians confirms switched joystick and fire through the SDL joystick path.
- Elite uses analogue joystick input for flight/navigation. The BBC version's own instructions list laser fire as keyboard `A`, so joystick fire may not be used for lasers by the game itself.
- AMX mouse support depends on the AMX ROM as well as host mouse capture.

## Legal Note

BBC Micro ROMs and commercial software images remain the property of their respective rights holders. This emulator is for educational and preservation-oriented development.
