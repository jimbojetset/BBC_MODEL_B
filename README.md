# BBC Model B Emulator

A BBC Micro Model B emulator written in C# and .NET.

I wrote this to feel more like a real Beeb rather than just a launcher for disc images.
It emulates the 6502, OS 1.20, BASIC II, DFS, selectable Intel 8271 and WD1770 disc interfaces, the VIAs, video, sound, keyboard matrix, sideways RAM, tape, serial hardware, joysticks, AMX-style mouse input, a Hayes modem, a Jessop floor turtle, the Acorn Speech System, and an optional 65C02 Tube second processor.

<p>
    <img src="Screenshot0.png" alt="BBC Model B emulator screenshot" width="49%">
    <img src="Screenshot1.png" alt="BBC Model B emulator screenshot" width="49%">
</p>

## Requirements

- .NET 10 SDK
- SDL2 runtime support through `ppy.SDL2-CS`
- BBC ROM images in `ROMS/`

The normal boot expects:

```text
ROMS/OS12.rom
ROMS/BASIC2.rom
ROMS/DFS-0.9.rom
```

Optional ROMs add extra hardware or modes:

```text
ROMS/HiBASIC.rom        Tube BASIC
ROMS/DNFS302.rom        BBC-side Tube host ROM
ROMS/6502tube_120.rom   65C02 Tube parasite ROM
ROMS/AMXMSE331.rom      AMX mouse ROM
ROMS/LOGO-1.rom        Acornsoft Logo, first ROM
ROMS/LOGO-2-1201387.rom Acornsoft Logo, second ROM
ROMS/PHROM.rom          Acorn Word PHROM A speech data (16 KB)
ROMS/DFS-2.26.rom       Acorn 1770 DFS 2.26 (16 KB)
ROMS/ADFS-1.30.rom      Acorn ADFS 1.30, fitted with the WD1770 interface (16 KB)
```

## Build And Run

Build it:

```bash
dotnet build BBC_MODEL_B.csproj
```

Start at BASIC:

```bash
dotnet run --project BBC_MODEL_B.csproj
```

Boot a disc image:

```bash
dotnet run --project BBC_MODEL_B.csproj -- Games/Phoenix.ssd
```

Mount a disc but stay at BASIC:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --no-autoboot Games/Phoenix.ssd
```

Load a tape:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --tape Games/Elite-v1.0_B.uef
```

Run with optional hardware enabled:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --tube-6502 Games/Elite-v1.0_B.uef
dotnet run --project BBC_MODEL_B.csproj -- --modem
dotnet run --project BBC_MODEL_B.csproj -- --print
dotnet run --project BBC_MODEL_B.csproj -- --speech
dotnet run --project BBC_MODEL_B.csproj -- --wd1770 Games/Phoenix.ssd
dotnet run --project BBC_MODEL_B.csproj -- --wd1770 Discs/Utilities.adf
```

## Command Line

Plain paths are accepted. Disc images boot by default, tape images enable the tape player, and explicit drive options enable the matching drive before mounting.

```text
--help              Print all command-line options, usage examples, and exit.

--disc PATH
--disk PATH
--file PATH         Mount a disc image, tape image, or host file.

--tape PATH         Enable the tape player and mount a UEF tape.

--drive0 PATH
--drive1 PATH       Enable that physical drive and mount SSD, DSD, or ZIP media.
--drive2 PATH
--drive3 PATH       Mount an SSD in a DFS logical drive slot.

--blank-ssd PATH    Create a blank SSD image if needed.
--blank-dsd PATH    Create a blank DSD image if needed.

--boot-disc         Boot mounted discs. This is the default.
--no-boot-disc
--no-autoboot       Mount media and leave the BBC at BASIC.

--type TEXT         Type text into the BBC keyboard buffer after boot. May be repeated.
--load-state PATH   Restore a .sav state before running.
--headless-ms N     Run without a window for N milliseconds.
--print-autoload PATH
                   Print the !BOOT command for a bootable DFS image.

--tube-6502         Start with the 65C02 Tube co-processor enabled.
--tube-enable       Alias for --tube-6502.
--tube-host-rom PATH
--tube-6502-rom PATH
                   Use non-default Tube ROMs.

--modem             Start with the Hayes modem enabled.
--print             Start with the dot-matrix printer enabled.
--speech            Start with the Acorn Speech System enabled.
--wd1770            Start with the WD1770 interface, Acorn 1770 DFS, and ADFS 1.30.
```

Examples:

```bash
dotnet run --project BBC_MODEL_B.csproj -- --drive0 game.ssd --drive1 data.dsd
dotnet run --project BBC_MODEL_B.csproj -- --blank-ssd work.ssd --drive1 work.ssd --no-autoboot
dotnet run --project BBC_MODEL_B.csproj -- --load-state Saves/Elite.sav
```

## Menus

The SDL window has a small menu bar for the common jobs:

```text
File         Screenshot, save state, open state, quit
Machine      BREAK, reset, sound, pause
Peripherals  Tape player, modem, printer, disc drives, speech, Tube
Disc interface
             Intel 8271 + Acorn DFS 1.20, or WD1770 + Acorn 1770 DFS
Sideways Memory  Configure empty, ROM, and writable RAM banks and import/export layouts
Keyboard Mapper
             Remap the BBC keyboard and import/export input profiles
Printer      Screen printing, output viewer, page controls, PNG export, sound
Debugger     Open the host 6502 debugger
View         Fullscreen, scanlines, BBC logo, and the optional FPS display
```

Click an on-screen disc drive or cassette player to open its media and transport menu.

A few useful details:

- Disc menu loads accept `.ssd`, `.dsd`, `.adf`, `.ads`, `.adm`, `.adl`, and `.zip`. ZIP files are browsed without extracting them.
- ADFS floppy images require the WD1770 interface. S (160 KB), M (320 KB), and L (640 KB) geometries use 16 MFM sectors per track; ADFS 1.30 is fitted in sideways ROM bank 12 alongside 1770 DFS.
- Sideways banks 4–7 are fitted as 16K RAM by default. Sideways Memory can add, remove, or move RAM banks independently; ROM and empty sockets remain read-only. BREAK preserves sideways RAM, a power reset clears it, and save states preserve both its contents and the socket configuration.
- Sideways Memory shows logical ROMSEL banks in hexadecimal, 0–F. Banks C–F are highlighted as the four motherboard sockets fitted to a Model B; banks 0–B represent expansion hardware.
- The Intel 8271 is selected by default. Changing `Disc interface` swaps the controller and DFS ROM, preserves mounted media, and power-resets the BBC.
- Drive 0 is on by default. Drive 1 is off until enabled from `Peripherals` or used from the command line.
- The tape player is on by default. The Hayes modem is off until enabled from `Peripherals` or started with `--modem`.
- Enabling the printer opens the `Epson FX-80 Printer` window and adds a `Printer` menu between `Keyboard Mapper` and `View`. The display models continuous tractor-feed paper curling behind the printer, including moving transparent sprocket holes and animated form feeds between connected pages.
- The `Printer` menu can print the current screen or a saved PNG from `Screenshots/`, open a scrollable undistorted view of every rendered page, invert screen-to-paper colours, save the paper as PNG, start a new page or sheet, cancel a job, and enable or mute printer sound.
- `New page` advances to the next connected fanfold sheet while preserving the current paper roll. `New paper` clears the rendered document and loads a fresh roll.
- Draft text runs at 160 characters per second. ESC/P bit-image graphics use an 80-cps-equivalent head speed.
- Saved-screenshot printing lists only PNG files directly inside `Screenshots/`; an empty folder produces an on-screen notification.
- Menu disc mounts behave like inserting a disc. They do not auto-boot; use `Shift-BREAK` when you want to boot. With ADFS media mounted, the emulator selects ADFS and executes `$.!BOOT`; SSD and DSD media continue to boot through DFS.
- Save states use `.sav` files and are opened or saved from the `File` menu.
- Screenshots go into `Screenshots/`.

## Keys

Most keys go through the BBC keyboard matrix, so games that read the keyboard directly behave properly.

```text
F12                     BREAK
Shift+F12               Shift-BREAK
Ctrl+F12                Ctrl-BREAK
Ctrl+P                  Pause or resume
Space                   Advance 10 frames while paused
F11                     Toggle scanlines
Ctrl+S / Cmd+S          Save screenshot
Ctrl+L / Cmd+L          Open the disc picker
Ctrl+V / Cmd+V          Paste clipboard text into the BBC
Ctrl+Shift+P            Toggle printer
Ctrl+Shift+R            Open Sideways Memory
Ctrl+Shift+K            Open the Keyboard Mapper
Ctrl+Home               Open the debugger
Left Ctrl+Left Shift    Toggle BBC SHIFT LOCK
```

Host arrow keys map to the BBC cursor keys, `F1` to `F10` map to BBC function keys, and `Insert` or `§` maps to BBC `COPY`.

Open `Keyboard Mapper` from the menu bar. Click a BBC key, press the host key you want, then save the map if you want to keep it. If `Assets/DefaultInputProfile.json` exists, it is loaded at startup.

## Jessop turtle

Enable **Peripherals → Turtle** to connect a Jessop Ralph turtle and show its separate SDL window. **View → Turtle drawing floor** reopens the window and is greyed out unless the turtle is connected. Closing the drawing window hides it while the turtle continues operating. The attachment and window can also be enabled at startup with `BBC_JESSOP_TURTLE=1`:

```sh
BBC_JESSOP_TURTLE=1 dotnet run --project BBC_MODEL_B.csproj
```

Place `LOGO-1.rom` and `LOGO-2-1201387.rom` in `ROMS/`. Enabling Turtle automatically fits them in free expansion sockets (preferring A and B), or reuses identical ROMs already fitted. Other ROM and RAM banks are preserved. If either ROM is missing, invalid, or there is insufficient socket space, Turtle remains disabled and a message explains why. Installing new ROMs resets the BBC so MOS recognises Logo; the banks appear in Sideways Memory.

With Turtle enabled, mount `Games/AcornsoftLogoExtensions.ssd` and enter `*LOGO`. At the Logo prompt, load the driver and select the floor turtle:

```logo
LOAD "JESSOP
FLOOR
```

Logo uses a leading double quote for a literal word, so `LOAD "JESSOP` has no closing quote. Paste commands into the main BBC window with **Ctrl+V**. `FLOOR` directs commands to the separate Turtle window; `SCREEN` switches to Logo's on-screen turtle. They do not move together automatically.

For an 18-circle rosette, start near the centre, select a pen colour, and enter:

```logo
PENDOWN
REPEAT 18 [REPEAT 36 [FORWARD 30 RIGHT 10] RIGHT 20]
PENUP
```

The pattern is approximately 70 cm across and takes several minutes at normal speed. Small closure errors reflect the wheel encoder resolution.

Disabling Turtle closes the drawing window, unloads both Logo ROMs (including moved sockets) and resets the BBC, preserving other fitted ROM and RAM banks. The OS ROM and reset vector remain intact when the Logo ROM configuration changes.

The floor defaults to **3 m × 3 m**, with **2 m × 2 m** and **1 m × 1 m** zoom presets centred on the same origin. A faint **0.25 m grid** provides scale. The turtle starts in the centre, facing up; its 300 mm body, clear dome and visible mechanisms follow the [Museums Victoria Jessop reference](https://collections.museumsvictoria.com.au/items/2620712). Movement and rotation follow continuous differential wheel travel, rather than jumping between encoder pulses. The drawing window has a fixed size, with text rendered at the display’s native pixel density. Pen buttons select **Red**, **Blue**, **Green**, or **Black** (the default); changing pens preserves the colour of existing lines, including in PNG exports. Zoom does not alter physical distances. Motion is not stopped or wrapped at the view boundary; the status line indicates when the turtle centre is outside the view.

**Load grid** replaces the reference grid with a PNG, JPEG, BMP or WebP image. It uses a centred fill crop over the 3 m floor, preserving aspect ratio, and is dimmed by 70% (30% opacity). Zoom stays aligned with the drawing. **Default grid** restores the 0.25 m grid without changing the ink or turtle position. The custom background is kept while the window is hidden, but is not included in PNG exports or machine save states.

**Save PNG** writes the full 3 m floor to a timestamped file in `Drawings/` at 3000 × 3000 pixels, containing only the pen marks on white, without the grid or turtle. Marks outside that floor are clipped in the export. **Clear** removes pen marks while preserving position and heading. Disconnecting the turtle removes its current drawing; save before disconnecting.

The attachment uses PB0 for pen power, PB1/PB2 for right-wheel direction/power, PB3/PB4 for left-wheel direction/power, PB5/PB6 for wheel sensors, and PB7 for pen feedback or hooter output depending on DDRB. It uses no CB1/CB2 handshake. Hooter output is represented electrically but does not produce audio yet. Mouse and switched-joystick user-port input is ignored while the turtle is attached; the analogue joystick and Port A printer remain separate.

Wheel feedback follows the [Jessop technical notes](https://stardot.org.uk/forums/download/file.php?id=61468): one encoder transition per 1.750 mm, at a nominal 100 mm/s while powered. Pen feedback uses an approximate 200 ms per cam half-turn. Timing follows emulated CPU cycles; motor inertia is not modelled. Reset releases the motor outputs while retaining sensor position. Save states and debugger backward stepping preserve the attachment, sensor phases, position, heading and drawing. New saves use format 35 and preserve the selected pen and each stroke’s colour; existing format 32, 33 and 34 saves remain readable. Format 34 drawings load in black. Format 32 restores without a turtle; format 33 restores its signals with a fresh centred drawing.

## Debugger

The separate SDL debugger provides host 6502 registers, memory search, disassembly and instruction history, stack inspection, run/pause, step-over/out control, optional backward stepping, execution breakpoints, read/write watchpoints, hardware inspection, a command console, and built-in or external symbols. A half-resolution live BBC display preview makes it possible to relate machine state directly to the visible output while stepping.

`Step` (`F10`), `Over` (`F9`), and `Out` (`Shift+F9`) stay highlighted while their mouse button or shortcut key is held. Disassembly follows the completed step’s PC, including jumps and branches, while preserving manual browsing between steps. Pressing `Break` brings the stopped PC into view, just like pressing `Break` followed by `PC`. In the `CPU` tab, each `NV-BDIZC` flag label lines up with its binary value.

Backward stepping is off by default. Click `Undo OFF` to switch to `Undo ON` and record machine snapshots. When enabled while paused, the current state becomes the starting point; when enabled while running, recording starts at the next breakpoint. `Back` (`F8`) restores the state before the last completed STEP, OVER, or OUT and remains paused. OVER and OUT each undo as a whole action; counted command steps record each instruction separately. `Start` returns directly to the starting breakpoint or paused state. Stepping forward after Back creates a new execution path, and instruction history from the abandoned future is removed. The toolbar shows the number of saved actions; Back is available after an action completes. Restoring refreshes the registers, disassembly, stack and display preview. Command output remains available, with a message marking the restored state.

For example, enable Undo, run to a breakpoint, then Step three instructions. Press `F8` to undo the third instruction, or click `Start` to return straight to the breakpoint. Run resumes from the restored state and ends that Back history.

Back history retains up to 128 actions within a 96 MB budget for compressed snapshots. If the limit is reached, older intermediate actions are dropped, but the starting state remains available through `Start`. Run, closing the debugger, memory edits, loading a state, or input/configuration changes through the main emulator window end the current history; Undo mode remains enabled for subsequent paused steps or the next breakpoint. Snapshots include CPU/RAM/ROM state, interrupt queues, VIAs, video, sound, disc controllers and media, and the Tube when enabled. Disc-file writes are deferred while Back history is retained and flushed when execution resumes or through normal disc flushing. For this version, Back requires the modem and printer disabled and no tape mounted. It cannot undo sound already played.

The `STACK` tab in CPU / HARDWARE shows PC, SP, and nine bytes of the 6502 stack page, highlighting the next pull address and marking SP as the next push location when visible. Scroll over the panel to inspect the full `$0100–$01FF` page; the view follows the next pull whenever SP changes. Select the panel and press `Ctrl+C` to copy its visible contents. Stack bytes are shown without guessing return addresses or call frames.

The memory panel’s `Find:` field accepts an address or symbol to navigate, hexadecimal bytes such as `A9 00`, or case-sensitive ASCII text in double quotes such as `"HELLO WORLD"`. Use `? FF` to search for a single byte. Press Enter to search from the displayed memory address, then use `<` and `>` for previous/next matches, wrapping through the currently mapped 64 KB address space. Matching bytes are highlighted; results and invalid-input messages appear in command output. Searches skip FRED, JIM and SHEILA, and matches do not span `$FFFF` to `$0000`. Ctrl+V pastes into the field; Escape cancels entry.

The memory panel also has a `SIDEWAYS` tab for inspecting populated sideways ROM/RAM banks. Select a bank using the hexadecimal `0–F` buttons. Empty banks are dimmed and cannot be selected. The view shows the bank’s status and title, followed by bytes at `$8000–$BFFF`. It defaults to bank `F`; choosing another bank only changes the debugger view and does not change ROMSEL. Find, previous/next search, scrolling and Ctrl+C operate within the inspected bank; scrolling wraps within its 16 KB window. Switching banks clears the previous search and change highlights. Each tab retains its memory position. The inspector is read-only; the `m` and `e` console commands use CPU-mapped memory and return to the `MEMORY` tab.

The centre panel has `DISASSEMBLY` and `HISTORY` tabs. In `HISTORY`, enable `Record` before running or stepping to retain the latest 8,192 operations, including IRQ/NMI entries and host-handled MOS calls. The panel displays 16 rows at a time. Each row shows registers **before** the operation; selecting a row shows its flags and cycle count. Scrolling holds the history position while new entries are recorded, until those older entries are overwritten. Use `Latest` to resume following, double-click a row to open its address in disassembly, or press `Ctrl+C` to copy the visible history. `Clear` empties the history without changing recording. Older entries are overwritten when the buffer fills. Selecting history does not restore past CPU or memory state.

The `Clear` button at the right of the `COMMAND OUTPUT` title row clears retained output and resets its scroll position, preserving command-entry history. New command results and hardware trace messages can still appear afterward. The history `Clear` and `Latest` buttons, memory search arrows, and command output `Clear` button stay highlighted while the mouse button is held; releasing it or losing debugger focus clears the highlight.

Open it from the `Debugger` menu or with `Ctrl+Home`. Opening the debugger leaves the emulator running; pause it in the debugger when you need a stable machine state. The complete command reference, practical examples, symbol-file guidance, screenshots, and functional tests are in the [Debugger Manual](https://github.com/jimbojetset/BBC_MODEL_B/wiki/Debugger).

## Discs, Tapes, And Drives

The emulator supports DFS `.ssd` and `.dsd` images, UEF tapes, and ZIP archives containing disc images.

Physical drive 0 maps to DFS drives 0 and 2. Physical drive 1 maps to DFS drives 1 and 3. That matters for double-sided DSD images: mount them with `--drive0` or `--drive1` so both sides stay together.

You can create blank media from the menu or command line. DFS can also catalogue, load, save, delete, copy, verify, and format mounted images from inside the BBC.

Blank UEF tapes created from the cassette menu are recordable 10-minute tapes. Loaded game UEFs are treated like tapes with the record tab removed, so `REC` is disabled for them.

5.25 inch drive sounds are mixed into the main audio path from WAV samples in `Assets/Sound/`. The sample source is credited in `THIRD_PARTY_NOTICES.md`.

## Optional Hardware

The Peripherals menu lets you add or remove hardware while the emulator is running:

- `Tape Player` toggles the cassette hardware and tape controls.
- `Hayes Modem` enables the modem and its front-panel LEDs.
- `Printer` connects an Epson FX-80-compatible dot-matrix printer to the BBC printer port and opens its paper display and controls.
- `Acorn Speech System` fits the TMS5220 processor and loads `ROMS/PHROM.rom` as the TMS6100 Word PHROM A. Enabling or disabling it from the `Peripherals` menu power-resets the BBC.
- `Disc Drive 0` and `Disc Drive 1` enable or remove each physical drive.
- `6502 Co-Processor` enables the Tube hardware. Enabling or disabling it from the `Peripherals` menu power-resets the BBC so the host detects the new configuration.

The Hayes modem accepts familiar AT commands such as `AT`, `ATZ`, `ATH`, `ATO`, `ATE0/1`, `ATV0/1`, `AT&F`, and `ATDThost:port`. A successful dial opens a TCP connection, defaulting to port 23 if no port is given. The BBC serial side should be set to 9600 baud, 8 data bits, no parity.

AMX mouse support is available through Sideways Memory. Add `ROMS/AMXMSE331.rom` to a sideways ROM bank, then use the usual BBC commands such as `*MOUSE ON` and `*POINTER ON`.

The speech system supports both PHROM vocabulary and speech data supplied from BBC RAM through the TMS5220 FIFO. With Word PHROM A installed, `SOUND -1,160,0,0` says “ACORN”. The PHROM is speech data rather than a sideways ROM and must remain named `PHROM.rom` in the `ROMS/` directory.

## Project Layout

```text
SRC/                 Emulator hardware, SDL UI, audio, loading, and machine wiring
SRC/6502/            6502 CPU core
ROMS/                BBC ROM images
Games/               Disc and tape images used for testing and play
Assets/              Config files, drive sounds, and sample media
Screenshots/         Runtime screenshot output
```

The main files are named after the hardware they emulate: `Intel8271_Disk.cs`, `System6522Via.cs`, `User6522Via.cs`, `SerialACIA.cs`, `SN76489_Sound.cs`, `TMS5220_Speech.cs`, `TubeUla.cs`, `HD6845_Video.cs`, and so on. `Emulator.cs` ties the machine together.

## Legal Note

This emulator is distributed under the GNU General Public License version 2. See `LICENSE`.

BBC Micro ROMs and commercial software images remain the property of their respective rights holders. Use your own legally obtained copies.
