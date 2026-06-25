# BBC Model B Emulator

A C#/.NET BBC Micro Model B emulator. I have tried to build it with a level of authenticity that makes operating the BBC feel right including fully emulating the 2 MHz NMOS 6502, OS 1.20, BASIC II, Acorn DFS, the 8271 disc controller, the system and user 6522 VIAs, the BBC video hardware, the SN76489 sound chip, and the keyboard matrix.

This has been a practical if not slightly obsessive project. Compatibility has been earned the slow way by booting the game, watching the loader, listening for the problematic little timing clues, and then going back through the hardware path until my mistake shows itself.

<img src="Screenshot.png" alt="BBC Model B emulator screenshot" width="50%">

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

`AMXMSE331.rom` is optional for a normal BBC boot. I keep it listed because AMX software is a useful test of the mouse path, and especially useful with the Repton 3 editor.

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

--drive0 PATH
--drive1 PATH      Mount an SSD/DSD image in a physical drive.
--drive2 PATH
--drive3 PATH      Mount an SSD image in a DFS logical drive slot.
--blank-ssd PATH   Create a blank SSD image if the file does not exist.
--blank-dsd PATH   Create a blank DSD image if the file does not exist.
                  If no --driveN option names that image, it is mounted in drive 0.

--boot-disc        Run the mounted disc's boot path. This is the default.
--no-boot-disc
--no-autoboot      Mount the disc and leave the BBC at BASIC.

--headless-ms N    Run without a window for N milliseconds.
--speed VALUE      CPU speed scale, for example 0.5 or 50%.
--print-autoload   Print the DFS !BOOT command for a bootable image.
```

Plain paths are accepted too:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Games/Acornsoft/Elite.ssd
```

## Controls

### SDL Menu

The SDL window has a compact top menu for gameplay/session actions:

```text
File      Save screenshot, quit
Disc      Mount/eject drive 0, mount/eject drive 1, create blank SSD
Machine   BREAK, Shift-BREAK, Ctrl-BREAK
View      Fullscreen, scanlines
Input     Paste clipboard, Shift Lock
```

Disc menu mounts behave like inserting media into a real drive: they do not auto-boot the disc. Use `Shift-BREAK` from the Machine menu or keyboard when you want to boot the mounted disc. `Create blank SSD` uses the first empty physical drive, preferring drive 0; if both drives are occupied, the menu item is unavailable.

### Host Shortcuts

```text
F12                     BBC BREAK
Shift+F12               BBC Shift-BREAK
Ctrl+F12                BBC Ctrl-BREAK
F11                     Toggle scanline overlay
Ctrl+S / Cmd+S          Save screenshot to Screenshots/
Ctrl+T                  Toggle runtime and 8271 disc trace logging
Ctrl+L / Cmd+L          Open host file picker and mount selected file
Ctrl+V / Cmd+V          Paste host clipboard text into the BBC keyboard buffer
Left Ctrl+Left Shift    Toggle BBC SHIFT LOCK
```

### Keyboard

Most keys go through the BBC keyboard matrix rather than being treated as host characters. That matters, because plenty of BBC software reads the matrix directly. A few mappings are worth knowing:

```text
Host arrow keys   BBC cursor keys
F1-F10            BBC function keys
F12               BBC BREAK
Insert / §        BBC COPY
Backspace/Delete  BBC DELETE
```

On macOS, `§` is also used for the BBC `COPY` key.

Host Caps Lock follows the BBC `CAPS LOCK` key. `Left Ctrl+Left Shift` toggles a BBC-style `SHIFT LOCK` by holding the BBC Shift matrix key down until the chord is pressed again.

The bottom border includes small status LEDs for cassette motor, caps lock, shift lock, and drive activity. The cassette/caps/shift indicators sit at the bottom left; the drive glyphs, drive numbers, and drive LEDs sit at the bottom right.

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

### Mouse And AMX

AMX mouse support uses host relative mouse capture, so the emulated pointer can keep moving even when the host pointer would otherwise hit the edge of the window. It is one of those corners where a tiny host convenience has to disappear behind the BBC's expectations.

Inside the BBC, the useful commands are:

```text
*MOUSE ON
*MOUSE OFF
*POINTER ON
*POINTER OFF
```

With `ROMS/AMXMSE331.rom` present, the ROM is loaded in sideways bank 13. `*MOUSE` and `*POINTER ON/OFF` update the host capture state, because AMX titles do not all enable the pointer in the same order.

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

`--drive0` and `--drive1` name the physical BBC drives. An SSD uses the drive you mount it in. A DSD uses both sides of that physical drive: drive 0 maps to DFS drives 0 and 2, while drive 1 maps to DFS drives 1 and 3.

Two double-sided images are mounted by using the two physical drives:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot --drive0 first.dsd --drive1 second.dsd
```

Blank images default to drive 0 if you do not say otherwise:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot --blank-ssd save.ssd
```

When you do care about the drive, the command says both what to make and where to put it:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot --blank-ssd save.ssd --drive1 save.ssd
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot --blank-dsd work.dsd --drive0 work.dsd
```

`--drive2` and `--drive3` are there for explicit SSD logical-drive mounts. DSD images must be mounted through physical drive 0 or 1 so the emulator can keep the two sides paired.

The loading path is deliberately split in two, because the neat version of this design would be less honest than the useful one:

- `Intel8271_Disk` models the Acorn 8271-facing hardware that DFS talks to.
- `HostFilingSystem` sits outside DFS and only handles the small MOS shortcuts that make a raw host file practical to load.

The 8271 emulation is good enough for ordinary DFS disc work rather than just loading games. DFS can catalogue, load, save, delete, copy between mounted drives, verify sectors, and write changes back to the host image. The Welcome disc has also been used to format and verify a scratch SSD in drive 1; the format reported `00 format errors` and `00 verify errors`, and `*CAT 1` then showed a blank catalogue.

`--blank-ssd` and `--blank-dsd` are still the quickest way to create fresh host images, but they are convenience shortcuts. BBC-side formatting through DFS utilities can also reinitialise an existing mounted DFS image.

Boot behaviour:

- DFS option 3 with `!BOOT` queues `*EXEC !BOOT`.
- Discs without that boot path are mounted but left at BASIC.
- Use `--no-autoboot` when you want to inspect a disc manually.

### Disc Drive Sound

5.25 inch drive noise is mixed into the main audio path from WAV samples in `Assets`. The 8271 emulation raises passive motor and seek events, so drive sound follows DFS activity without changing disc timing. The drive samples are mixed at a fixed gain chosen to keep the mechanics audible alongside the SN76489 output.

The drive WAV samples are from the b-em BBC Micro emulator, which is licensed under the GPL. See `THIRD_PARTY_NOTICES.md` for the source details and acknowledgement.

## Screenshots

Press `Ctrl+S` or `Cmd+S` to write a PNG to:

```text
Screenshots/
```

## Project Layout

BBC documentation traditionally calls the memory-mapped I/O page at `&FE00-&FEFF` `SHEILA`. I use the same name here. It means the I/O page, not a separate chip.

```text
SRC/                  Emulator hardware, host UI, audio, and filing-system wiring
SRC/6502/             NMOS 6502 core, registers, flags, and memory bus
ROMS/                 OS, BASIC, DFS, and optional AMX ROMs
Games/                DFS disc images used for testing and play
Screenshots/          Runtime screenshot output
SRC/uPD7002_ADC.cs        Analogue joystick/paddle converter at &FEC0-&FEC3
SRC/TapeACIAStub.cs       Cassette/RS423 ACIA response for software probes
SRC/Intel8271_Disk.cs     Acorn 8271 DFS disc controller surface
SRC/Display.cs            SDL window plus BBC keyboard, mouse, and joystick input
SRC/HostFilingSystem.cs   MOS shortcuts for loading a raw host file
SRC/Emulator.cs           Memory map, ROM loading, CLI, and hardware wiring
SRC/SAA5050_Font.cs       Mode 7 teletext glyphs
SRC/SN76489_Sound.cs      SN76489 PSG and internal speaker output
SRC/DiscDriveSound.cs     5.25 inch drive motor and seek sample playback
SRC/System6522Via.cs      System 6522 VIA: keyboard, slow bus, timers, VSYNC
SRC/User6522Via.cs        User 6522 VIA: user port, joystick, mouse pulses
SRC/HD6845_Video.cs       CRTC, Video ULA, Mode 7, and framebuffer rendering
```

## Legal Note

This emulator is distributed under the GNU General Public License version 2. See `COPYING`.

BBC Micro ROMs and commercial software images remain the property of their respective rights holders. This emulator is for educational and preservation-oriented development.
