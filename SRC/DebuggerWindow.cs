// ============================================================================
// Project:     BBC
// File:        DebuggerWindow.cs
// Description: Separate SDL debugger display for host CPU state and disassembly.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.InteropServices;
using System.Text;
using BBC.CPU;
using SkiaSharp;

namespace BBC
{
    public sealed class DebuggerWindow : IDisposable
    {
        private const int Width = 1324;
        private const int Height = 766;
        private const int ToolbarHeight = 42;
        private const int CommandTop = 536;
        private const int CommandInputTop = 702;
        private const int StatusTop = 736;
        private const int CommandOutputVisibleLines = 6;
        private const int DisassemblyLeft = 358;
        private const int DisassemblyRight = 908;
        private const int HardwareLeft = 916;
        private const int HardwareBottom = 384;
        private const int DisplayTop = 392;
        private const int ContentRight = Width - 8;
        private const uint Background = 0xFF15181C;
        private const uint Panel = 0xFF20242A;
        private const uint PanelDark = 0xFF191D22;
        private const uint Border = 0xFF3B424B;
        private const uint Text = 0xFFD8DEE9;
        private const uint DimText = 0xFF89929D;
        private const uint Accent = 0xFF58A6FF;
        private const uint CurrentInstruction = 0xFF293E55;

        private readonly CPU_6502 cpu;
        private readonly Func<ushort, byte> readByte;
        private readonly Action<ushort, byte> writeByte;
        private readonly Action pause;
        private readonly Action resume;
        private readonly Func<bool> step;
        private readonly Func<bool> paused;
        private readonly System6522Via systemVia;
        private readonly User6522Via userVia;
        private readonly HD6845_Video video;
        private readonly Func<IDiscController> discController;
        private readonly TubeUla tubeUla;
        private readonly Func<bool> tubeEnabled;
        private readonly DebuggerSymbols symbols = new DebuggerSymbols();
        private readonly HashSet<ushort> breakpoints = new HashSet<ushort>();
        private readonly HashSet<ushort> temporaryBreakpoints = new HashSet<ushort>();
        private readonly List<WatchRange> readWatchpoints = new List<WatchRange>();
        private readonly List<WatchRange> writeWatchpoints = new List<WatchRange>();
        private readonly object breakpointLock = new object();
        private readonly SKBitmap bitmap;
        private readonly SKCanvas canvas;
        private readonly SKTypeface typeface;
        private readonly SKPaint textPaint;
        private readonly SKPaint titlePaint;
        private readonly SKPaint smallPaint;
        private readonly SKBitmap displayFrame;
        private GCHandle displayFrameHandle;
        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private uint windowId;
        private bool visible;
        private bool disposed;
        private ushort memoryAddress;
        private ushort disassemblyAddress;
        private AddressField activeAddressField;
        private string addressEntry = string.Empty;
        private ushort? breakpointHitAt;
        private readonly List<string> commandOutput = new List<string>();
        private readonly List<string> commandHistory = new List<string>();
        private string commandLine = string.Empty;
        private int commandHistoryIndex;
        private bool commandFocus = true;
        private int pendingCommandSteps;
        private long observedCompletedSteps;
        private int commandScrollOffset;
        private string? temporaryStopDescription;
        private WatchedAccess? pendingWatchedAccess;
        private WatchedAccess? stoppedWatchedAccess;
        private HardwareTab selectedHardwareTab;
        private ClipboardPanel clipboardPanel = ClipboardPanel.Memory;

        public DebuggerWindow(
            CPU_6502 cpu,
            Func<ushort, byte> readByte,
            Action<ushort, byte> writeByte,
            Action pause,
            Action resume,
            Func<bool> step,
            Func<bool> paused,
            System6522Via systemVia,
            User6522Via userVia,
            HD6845_Video video,
            Func<IDiscController> discController,
            TubeUla tubeUla,
            Func<bool> tubeEnabled,
            uint[] displayPixels,
            int displayWidth,
            int displayHeight)
        {
            this.cpu = cpu;
            this.readByte = readByte;
            this.writeByte = writeByte;
            this.pause = pause;
            this.resume = resume;
            this.step = step;
            this.paused = paused;
            this.systemVia = systemVia;
            this.userVia = userVia;
            this.video = video;
            this.discController = discController;
            this.tubeUla = tubeUla;
            this.tubeEnabled = tubeEnabled;
            cpu.ShouldBreakBeforeInstruction = HasBreakpoint;

            bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            canvas = new SKCanvas(bitmap);
            typeface = SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
            textPaint = CreatePaint(15, Text);
            titlePaint = CreatePaint(14, DimText, bold: true);
            smallPaint = CreatePaint(13, DimText);

            displayFrameHandle = GCHandle.Alloc(displayPixels, GCHandleType.Pinned);
            displayFrame = new SKBitmap();
            SKImageInfo displayInfo = new SKImageInfo(displayWidth, displayHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            if (!displayFrame.InstallPixels(displayInfo, displayFrameHandle.AddrOfPinnedObject(), displayWidth * sizeof(uint)))
                throw new InvalidOperationException("Unable to attach the debugger display preview to the emulator frame buffer.");
        }

        public bool Visible => visible;

        public void Show()
        {
            EnsureWindow();
            visible = true;
            pause();
            disassemblyAddress = (ushort)cpu.registers.PC;
            activeAddressField = AddressField.None;
            addressEntry = string.Empty;
            commandFocus = true;
            SDL_ShowWindow(window);
            SDL_RaiseWindow(window);
            Present();
        }

        public void ShowBreakpoint(ushort address)
        {
            lock (breakpointLock)
            {
                WatchedAccess? watchedAccess = stoppedWatchedAccess;
                if (watchedAccess is WatchedAccess access)
                {
                    WriteCommandOutput($"{(access.Write ? "Write" : "Read")} watchpoint ${access.Address:X4} = ${access.Value:X2}, instruction ${access.InstructionAddress:X4}");
                }
                bool temporary = temporaryBreakpoints.Remove(address);
                if (watchedAccess.HasValue)
                    breakpointHitAt = null;
                else if (breakpoints.Contains(address))
                    breakpointHitAt = address;
                else if (temporary)
                    temporaryStopDescription ??= "STEP COMPLETE";
                else
                    breakpointHitAt = address;
            }
            Show();
            disassemblyAddress = address;
        }

        public bool HandleEvent(uint type, uint eventWindowId, byte windowEvent, int keySym, byte mouseButton, int mouseX, int mouseY, int mouseWheelY, byte[] textInput)
        {
            if (windowId == 0 || eventWindowId != windowId)
                return false;

            if (type == SDL_WINDOWEVENT && windowEvent == SDL_WINDOWEVENT_CLOSE)
            {
                CloseAndResume();
                return true;
            }

            if (type == SDL_TEXTINPUT)
            {
                int length = Array.IndexOf(textInput, (byte)0);
                if (length < 0) length = textInput.Length;
                string text = Encoding.UTF8.GetString(textInput, 0, length);
                if (activeAddressField != AddressField.None)
                {
                    foreach (char character in text)
                    {
                        if (!char.IsWhiteSpace(character) && !char.IsControl(character) && addressEntry.Length < 64)
                            addressEntry += character;
                    }
                    return true;
                }
                if (!commandFocus)
                    return true;
                foreach (char character in text)
                {
                    if (!char.IsControl(character) && commandLine.Length < 120)
                        commandLine += character;
                }
                return true;
            }

            if (type == SDL_KEYDOWN)
            {
                int modifiers = SDL_GetModState();
                if ((modifiers & (KMOD_CTRL | KMOD_GUI)) != 0)
                {
                    if (keySym == SDLK_C)
                    {
                        CopyPanelToClipboard();
                        return true;
                    }
                    if (keySym == SDLK_V && commandFocus && activeAddressField == AddressField.None)
                    {
                        PasteCommandFromClipboard();
                        return true;
                    }
                }

                if (HandleAddressKey(keySym))
                    return true;
                if (HandleCommandKey(keySym))
                    return true;

                switch (keySym)
                {
                    case SDLK_F5:
                        ResumeExecution();
                        break;
                    case SDLK_F6:
                        PauseExecution();
                        break;
                    case SDLK_F10:
                        StepOnce();
                        break;
                    case SDLK_F9:
                        ToggleBreakpoint(disassemblyAddress);
                        break;
                    case SDLK_F11:
                        if ((SDL_GetModState() & KMOD_SHIFT) != 0)
                            StepOut();
                        else
                            StepOver();
                        break;
                    case SDLK_ESCAPE:
                        ClearBreakpoints();
                        ClearWatchpoints();
                        visible = false;
                        SDL_HideWindow(window);
                        break;
                }
                return true;
            }

            if (type == SDL_MOUSEBUTTONDOWN && mouseButton == SDL_BUTTON_LEFT)
            {
                SDL_RenderWindowToLogical(renderer, mouseX, mouseY, out float logicalX, out float logicalY);
                if (logicalY >= 7 && logicalY < 35)
                {
                    if (logicalX is >= 10 and < 82)
                        ResumeExecution();
                    else if (logicalX is >= 88 and < 170)
                        PauseExecution();
                    else if (logicalX is >= 176 and < 250)
                        StepOnce();
                    else if (logicalX is >= 256 and < 350)
                        StepOver();
                    else if (logicalX is >= 356 and < 442)
                        StepOut();
                }
                else if (logicalY is >= 76 and < 110 && logicalX is >= 18 and < 338)
                {
                    clipboardPanel = ClipboardPanel.Memory;
                    BeginAddressEntry(AddressField.Memory);
                    commandFocus = false;
                }
                else if (logicalY is >= 76 and < 110 && logicalX is >= 368 and < 808)
                {
                    clipboardPanel = ClipboardPanel.Disassembly;
                    BeginAddressEntry(AddressField.Disassembly);
                    commandFocus = false;
                }
                else if (logicalY is >= 76 and < 110 && logicalX is >= 820 and < 892)
                {
                    disassemblyAddress = (ushort)cpu.registers.PC;
                    activeAddressField = AddressField.None;
                    commandFocus = false;
                }
                else if (logicalY is >= 78 and < 105 && logicalX is >= 926 and < 1204)
                {
                    clipboardPanel = ClipboardPanel.Hardware;
                    SelectHardwareTab(logicalX);
                    commandFocus = false;
                }
                else if (logicalX is >= 366 and < 410 && logicalY is >= 117 and < 517)
                {
                    clipboardPanel = ClipboardPanel.Disassembly;
                    int row = (int)((logicalY - 117) / 20);
                    ToggleBreakpoint(GetDisassemblyRowAddress(row));
                }
                else if (logicalY is >= CommandInputTop and < StatusTop)
                {
                    clipboardPanel = ClipboardPanel.CommandOutput;
                    activeAddressField = AddressField.None;
                    commandFocus = true;
                }
                else if (logicalY is >= 48 and < CommandTop && logicalX < 350)
                {
                    clipboardPanel = ClipboardPanel.Memory;
                    commandFocus = false;
                }
                else if (logicalY is >= 48 and < CommandTop && logicalX is >= DisassemblyLeft and < DisassemblyRight)
                {
                    clipboardPanel = ClipboardPanel.Disassembly;
                    commandFocus = false;
                }
                else if (logicalX >= HardwareLeft && logicalY is >= 48 and < HardwareBottom)
                {
                    clipboardPanel = ClipboardPanel.Hardware;
                    commandFocus = false;
                }
                else if (logicalX < DisassemblyRight && logicalY is >= CommandTop and < CommandInputTop)
                {
                    clipboardPanel = ClipboardPanel.CommandOutput;
                    commandFocus = false;
                }
                else
                {
                    commandFocus = false;
                }
                return true;
            }

            if (type == SDL_MOUSEWHEEL)
            {
                SDL_GetMouseState(out int currentMouseX, out int currentMouseY);
                SDL_RenderWindowToLogical(renderer, currentMouseX, currentMouseY, out float logicalX, out float logicalY);
                if (logicalY is >= 76 and < CommandTop)
                {
                    if (logicalX < 350)
                        memoryAddress = (ushort)(memoryAddress - mouseWheelY * 8);
                    else if (logicalX is >= DisassemblyLeft and < DisassemblyRight)
                        MoveDisassembly(mouseWheelY > 0 ? -1 : 1, Math.Abs(mouseWheelY));
                }
                else if (logicalX < DisassemblyRight && logicalY is >= CommandTop and < CommandInputTop)
                {
                    int maximum = Math.Max(0, commandOutput.Count - CommandOutputVisibleLines);
                    commandScrollOffset = Math.Clamp(commandScrollOffset + mouseWheelY, 0, maximum);
                }
                return true;
            }

            return true;
        }

        private void CopyPanelToClipboard()
        {
            string text = clipboardPanel switch
            {
                ClipboardPanel.Memory => GetVisibleMemoryText(),
                ClipboardPanel.Disassembly => GetVisibleDisassemblyText(),
                ClipboardPanel.Hardware => GetVisibleHardwareText(),
                ClipboardPanel.CommandOutput => string.Join(Environment.NewLine, commandOutput),
                _ => string.Empty
            };
            if (text.Length > 0)
                SDL_SetClipboardText(text);
        }

        private string GetVisibleMemoryText()
        {
            StringBuilder result = new StringBuilder();
            ushort address = (ushort)(memoryAddress & 0xFFF8);
            Span<char> ascii = stackalloc char[8];
            for (int row = 0; row < 20; row++)
            {
                result.Append($"{address:X4}");
                for (int column = 0; column < 8; column++)
                {
                    byte value = readByte((ushort)(address + column));
                    result.Append($" {value:X2}");
                    ascii[column] = value is >= 32 and <= 126 ? (char)value : '.';
                }
                result.Append("  ").Append(ascii);
                if (row < 19) result.AppendLine();
                address += 8;
            }
            return result.ToString();
        }

        private string GetVisibleDisassemblyText()
        {
            StringBuilder result = new StringBuilder();
            ushort address = disassemblyAddress;
            ushort pc = (ushort)cpu.registers.PC;
            for (int row = 0; row < 20; row++)
            {
                DecodedInstruction instruction = Decode(address);
                result.Append(address == pc ? "> " : "  ")
                    .Append($"{address:X4}  {instruction.Bytes,-8} {instruction.Text}");
                if (row < 19) result.AppendLine();
                address = (ushort)(address + instruction.Length);
            }
            return result.ToString();
        }

        private string GetVisibleHardwareText()
        {
            if (selectedHardwareTab != HardwareTab.Cpu)
                return string.Join(Environment.NewLine, GetHardwareState(selectedHardwareTab).Take(11));

            Registers r = cpu.registers;
            string flags = string.Join(' ', Convert.ToString(r.P, 2).PadLeft(8, '0').ToCharArray());
            return string.Join(Environment.NewLine,
            [
                $"PC  ${r.PC & 0xFFFF:X4}",
                $"A   ${r.A:X2}       X   ${r.X:X2}",
                $"Y   ${r.Y:X2}       SP  ${r.S:X2}",
                $"P   ${r.P:X2}",
                "N V - B D I Z C",
                flags,
                $"IRQ  {(cpu.IrqLineAsserted ? "asserted" : "clear")}",
                $"CPU  {(paused() ? "paused" : "running")}"
            ]);
        }

        private void PasteCommandFromClipboard()
        {
            IntPtr textPointer = SDL_GetClipboardText();
            if (textPointer == IntPtr.Zero)
                return;

            try
            {
                string? text = Marshal.PtrToStringUTF8(textPointer);
                if (string.IsNullOrEmpty(text))
                    return;
                string singleLine = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
                int available = Math.Max(0, 120 - commandLine.Length);
                commandLine += singleLine[..Math.Min(singleLine.Length, available)];
            }
            finally
            {
                SDL_free(textPointer);
            }
        }

        private void CloseAndResume()
        {
            ClearBreakpoints();
            ClearWatchpoints();
            visible = false;
            SDL_HideWindow(window);
            ResumeExecution();
        }

        private void ResumeExecution()
        {
            ClearTemporaryBreakpoints();
            breakpointHitAt = null;
            temporaryStopDescription = null;
            stoppedWatchedAccess = null;
            resume();
        }

        private void PauseExecution()
        {
            ClearTemporaryBreakpoints();
            temporaryStopDescription = null;
            pause();
        }

        private void StepOnce()
        {
            ClearTemporaryBreakpoints();
            if (step())
            {
                breakpointHitAt = null;
                temporaryStopDescription = null;
                disassemblyAddress = (ushort)cpu.registers.PC;
            }
        }

        private void StepOver()
        {
            if (!paused())
                return;

            ushort pc = (ushort)cpu.registers.PC;
            if (readByte(pc) != 0x20) // JSR absolute
            {
                StepOnce();
                return;
            }

            RunToTemporaryBreakpoint((ushort)(pc + 3), "STEP OVER COMPLETE");
        }

        private void StepOut()
        {
            if (!paused())
                return;

            byte stackPointer = cpu.registers.S;
            if (stackPointer > 0xFD)
            {
                WriteCommandOutput("Step out requires a return address on the 6502 stack");
                return;
            }
            byte low = readByte((ushort)(0x0100 | (byte)(stackPointer + 1)));
            byte high = readByte((ushort)(0x0100 | (byte)(stackPointer + 2)));
            ushort returnAddress = (ushort)(((high << 8) | low) + 1);
            RunToTemporaryBreakpoint(returnAddress, "STEP OUT COMPLETE");
        }

        private void RunToTemporaryBreakpoint(ushort address, string description)
        {
            lock (breakpointLock)
                temporaryBreakpoints.Add(address);
            breakpointHitAt = null;
            temporaryStopDescription = description;
            resume();
        }

        public void Present()
        {
            if (!visible || renderer == IntPtr.Zero)
                return;

            ContinueCommandSteps();
            DrawWindow();
            SDL_UpdateTexture(texture, IntPtr.Zero, bitmap.GetPixels(), bitmap.RowBytes);
            SDL_RenderClear(renderer);
            SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero);
            SDL_RenderPresent(renderer);
        }

        private void DrawWindow()
        {
            canvas.Clear(new SKColor(Background));
            DrawToolbar();

            DrawPanel(new SKRect(8, 48, 350, CommandTop - 8), "MEMORY");
            DrawAddressField(new SKRect(18, 76, 338, 108), "Address", memoryAddress, AddressField.Memory);
            DrawMemory(20, 132);

            DrawPanel(new SKRect(DisassemblyLeft, 48, DisassemblyRight, CommandTop - 8), "DISASSEMBLY");
            DrawAddressField(new SKRect(368, 76, 808, 108), "Address", disassemblyAddress, AddressField.Disassembly);
            DrawButton(new SKRect(820, 78, 892, 106), "PC", false);
            DrawDisassembly(374, 132);

            DrawPanel(new SKRect(HardwareLeft, 48, ContentRight, HardwareBottom), "CPU / HARDWARE");
            DrawHardwareTabs();
            DrawSelectedHardware(932, 126);

            DrawPanel(new SKRect(8, CommandTop, DisassemblyRight, CommandInputTop - 6), "COMMAND OUTPUT");
            DrawCommandOutput();

            Fill(new SKRect(HardwareLeft, DisplayTop, ContentRight, CommandInputTop - 6), Panel);
            Stroke(new SKRect(HardwareLeft, DisplayTop, ContentRight, CommandInputTop - 6), Border);
            DrawDisplayPreview(new SKRect(HardwareLeft + 8, DisplayTop + 8, ContentRight - 8, CommandInputTop - 14));

            Fill(new SKRect(8, CommandInputTop, ContentRight, StatusTop - 6), PanelDark);
            Stroke(new SKRect(8, CommandInputTop, ContentRight, StatusTop - 6), Border);
            DrawText(">", 20, CommandInputTop + 20, Accent);
            DrawText(commandLine, 42, CommandInputTop + 20, commandFocus ? Text : DimText);
            if (commandFocus && (Environment.TickCount64 / 500 & 1) == 0)
            {
                float caretX = 42 + textPaint.MeasureText(commandLine);
                Line(caretX, CommandInputTop + 5, caretX, CommandInputTop + 23, Accent);
            }

            Fill(new SKRect(0, StatusTop, Width, Height), PanelDark);
            string state = breakpointHitAt.HasValue
                ? $"BREAKPOINT ${breakpointHitAt.Value:X4}"
                : stoppedWatchedAccess.HasValue
                    ? $"{(stoppedWatchedAccess.Value.Write ? "WRITE" : "READ")} WATCH ${stoppedWatchedAccess.Value.Address:X4}"
                : paused() && temporaryStopDescription is not null
                    ? temporaryStopDescription
                    : paused() ? "PAUSED" : "RUNNING";
            DrawText(state, 14, StatusTop + 21, paused() ? 0xFFFFC857 : 0xFF67D391);
            DrawText($"PC ${cpu.registers.PC & 0xFFFF:X4}", 260, StatusTop + 21, Text);
            DrawText($"{cpu.TotalCycles:N0} cycles", 1074, StatusTop + 21, DimText);
        }

        private void DrawToolbar()
        {
            Fill(new SKRect(0, 0, Width, ToolbarHeight), PanelDark);
            DrawButton(new SKRect(10, 7, 82, 35), "Run F5", !paused());
            DrawButton(new SKRect(88, 7, 170, 35), "Break F6", paused());
            DrawButton(new SKRect(176, 7, 250, 35), "Step F10", false);
            DrawButton(new SKRect(256, 7, 350, 35), "Over F11", false);
            DrawButton(new SKRect(356, 7, 442, 35), "Out Sh-F11", false);
            DrawText($"F9 breakpoint    {BreakpointCount} break / {WatchpointCount} watch    Host 6502", 756, 27, Accent, small: true);
        }

        private void DrawHardwareTabs()
        {
            string[] tabs = ["CPU", "SYS", "USER", "VIDEO", "DISC", "TUBE"];
            float[] widths = [42, 42, 46, 48, 44, 44];
            float x = 926;
            for (int i = 0; i < tabs.Length; i++)
            {
                float width = widths[i];
                bool selected = i == (int)selectedHardwareTab;
                Fill(new SKRect(x, 78, x + width, 105), selected ? CurrentInstruction : PanelDark);
                DrawText(tabs[i], x + 5, 96, selected ? Text : DimText, small: true);
                x += width + 2;
            }
        }

        private void SelectHardwareTab(float x)
        {
            float[] widths = [42, 42, 46, 48, 44, 44];
            float left = 926;
            for (int i = 0; i < widths.Length; i++)
            {
                if (x >= left && x < left + widths[i])
                {
                    selectedHardwareTab = (HardwareTab)i;
                    return;
                }
                left += widths[i] + 2;
            }
        }

        private void DrawSelectedHardware(float x, float y)
        {
            if (selectedHardwareTab == HardwareTab.Cpu)
            {
                DrawRegisters(x, y);
                return;
            }

            string[] lines = GetHardwareState(selectedHardwareTab);
            for (int i = 0; i < lines.Length && i < 18; i++)
                DrawText(lines[i], x, y + i * 25, i == 0 ? Accent : Text, small: true);
        }

        private void DrawRegisters(float x, float y)
        {
            Registers r = cpu.registers;
            byte p = r.P;
            DrawText($"PC  ${r.PC & 0xFFFF:X4}", x, y, Accent);
            DrawText($"A   ${r.A:X2}       X   ${r.X:X2}", x, y + 30, Text);
            DrawText($"Y   ${r.Y:X2}       SP  ${r.S:X2}", x, y + 58, Text);
            DrawText($"P   ${p:X2}", x, y + 86, Text);
            DrawText("N V - B D I Z C", x, y + 126, DimText);
            DrawText(string.Join(' ', Convert.ToString(p, 2).PadLeft(8, '0').ToCharArray()), x, y + 153, Text);
            DrawText("Interrupts", x + 190, y + 126, DimText);
            DrawText($"IRQ  {(cpu.IrqLineAsserted ? "asserted" : "clear")}", x + 190, y + 153, Text);
            DrawText($"CPU  {(paused() ? "paused" : "running")}", x + 190, y + 180, Text);
        }

        private void DrawDisassembly(float x, float y)
        {
            ushort address = disassemblyAddress;
            ushort pc = (ushort)cpu.registers.PC;
            canvas.Save();
            canvas.ClipRect(new SKRect(DisassemblyLeft + 1, 83, DisassemblyRight - 1, CommandTop - 9));
            for (int row = 0; row < 20; row++)
            {
                DecodedInstruction instruction = Decode(address);
                float baseline = y + row * 20;
                bool current = address == pc;
                if (current)
                    Fill(new SKRect(x - 8, baseline - 15, DisassemblyRight - 16, baseline + 5), CurrentInstruction);

                if (HasPermanentBreakpoint(address))
                    Circle(x + 4, baseline - 5, 5, 0xFFE05252);
                else if (HasTemporaryBreakpoint(address))
                    Circle(x + 4, baseline - 5, 5, 0xFFFFC857);
                DrawText(current ? "▶" : " ", x, baseline, current ? Accent : DimText);
                DrawText($"{address:X4}", x + 24, baseline, current ? Accent : Text);
                DrawText(instruction.Bytes, x + 82, baseline, DimText);
                DrawText(instruction.Text, x + 184, baseline, Text);
                address = (ushort)(address + instruction.Length);
            }
            canvas.Restore();
        }

        private void DrawMemory(float x, float y)
        {
            ushort address = (ushort)(memoryAddress & 0xFFF8);
            Span<char> ascii = stackalloc char[8];
            for (int row = 0; row < 20; row++)
            {
                float baseline = y + row * 20;
                DrawText($"{address:X4}", x, baseline, Accent);
                for (int column = 0; column < 8; column++)
                {
                    byte value = readByte((ushort)(address + column));
                    DrawText($"{value:X2}", x + 54 + column * 24, baseline, Text, small: true);
                    ascii[column] = value is >= 32 and <= 126 ? (char)value : '.';
                }
                DrawText(new string(ascii), x + 260, baseline, DimText, small: true);
                address += 8;
            }
        }

        private void DrawAddressField(SKRect rect, string label, ushort address, AddressField field)
        {
            Fill(rect, PanelDark);
            Stroke(rect, activeAddressField == field ? Accent : Border);
            string value = activeAddressField == field ? addressEntry : $"{address:X4}";
            DrawText($"{label}:  ${value}", rect.Left + 10, rect.Top + 22, activeAddressField == field ? Accent : Text, small: true);
        }

        private void BeginAddressEntry(AddressField field)
        {
            activeAddressField = field;
            addressEntry = string.Empty;
        }

        private bool HandleCommandKey(int keySym)
        {
            if (!commandFocus || activeAddressField != AddressField.None)
                return false;

            switch (keySym)
            {
                case SDLK_BACKSPACE:
                    if (commandLine.Length > 0)
                        commandLine = commandLine[..^1];
                    return true;
                case SDLK_RETURN:
                case SDLK_KP_ENTER:
                    ExecuteCommand();
                    return true;
                case SDLK_UP:
                    if (commandHistory.Count > 0)
                    {
                        commandHistoryIndex = Math.Max(0, commandHistoryIndex - 1);
                        commandLine = commandHistory[commandHistoryIndex];
                    }
                    return true;
                case SDLK_DOWN:
                    if (commandHistoryIndex < commandHistory.Count - 1)
                        commandLine = commandHistory[++commandHistoryIndex];
                    else
                    {
                        commandHistoryIndex = commandHistory.Count;
                        commandLine = string.Empty;
                    }
                    return true;
                default:
                    return false;
            }
        }

        private void ExecuteCommand()
        {
            string entered = commandLine.Trim();
            commandLine = string.Empty;
            if (entered.Length == 0)
                return;

            commandHistory.Add(entered);
            commandHistoryIndex = commandHistory.Count;
            WriteCommandOutput($"> {entered}");

            string[] parts = entered.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "?":
                    case "help":
                        WriteHelp();
                        break;
                    case "run":
                    case "g":
                        pendingCommandSteps = 0;
                        ResumeExecution();
                        WriteCommandOutput("Running");
                        break;
                    case "pause":
                        PauseExecution();
                        WriteCommandOutput($"Paused at ${cpu.registers.PC & 0xFFFF:X4}");
                        break;
                    case "n":
                    case "step":
                        StartCommandSteps(parts.Length > 1 ? ParseCount(parts[1], 1, 10000) : 1);
                        break;
                    case "o":
                    case "over":
                        StepOver();
                        break;
                    case "out":
                        StepOut();
                        break;
                    case "r":
                    case "regs":
                        WriteRegisters();
                        break;
                    case "m":
                    case "memory":
                        ExecuteMemoryCommand(parts);
                        break;
                    case "e":
                        ExecuteEnterMemoryCommand(parts);
                        break;
                    case "d":
                    case "disassemble":
                        ExecuteDisassembleCommand(parts);
                        break;
                    case "b":
                    case "breakpoint":
                        ExecuteBreakpointCommand(parts);
                        break;
                    case "bc":
                        ClearBreakpoints();
                        WriteCommandOutput("All breakpoints cleared");
                        break;
                    case "wr":
                        ExecuteWatchpointCommand(parts, write: false);
                        break;
                    case "ww":
                        ExecuteWatchpointCommand(parts, write: true);
                        break;
                    case "wc":
                        ClearWatchpoints();
                        WriteCommandOutput("All watchpoints cleared");
                        break;
                    case "clear":
                        ClearBreakpoints();
                        ClearWatchpoints();
                        WriteCommandOutput("All breakpoints and watchpoints cleared");
                        break;
                    case "ss":
                        WriteHardwareState(HardwareTab.SystemVia);
                        break;
                    case "su":
                        WriteHardwareState(HardwareTab.UserVia);
                        break;
                    case "sv":
                        WriteHardwareState(HardwareTab.Video);
                        break;
                    case "sd":
                        WriteHardwareState(HardwareTab.Disc);
                        break;
                    case "st":
                        WriteHardwareState(HardwareTab.Tube);
                        break;
                    case "sl":
                    case "symbols-load":
                        LoadSymbols(entered);
                        break;
                    case "sc":
                    case "symbols-clear":
                        int unloaded = symbols.ExternalCount;
                        symbols.Unload();
                        WriteCommandOutput($"Unloaded {unloaded} external symbols; {symbols.BuiltInCount} built-in symbols remain");
                        break;
                    case "symbols":
                        ListSymbols(parts.Length > 1 ? string.Join(' ', parts[1..]) : null);
                        break;
                    case "symbol":
                        LookupSymbol(parts);
                        break;
                    default:
                        WriteCommandOutput($"Unknown command: {parts[0]} (type help)");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                WriteCommandOutput(ex.Message);
            }
            catch (IOException ex)
            {
                WriteCommandOutput($"Unable to load symbols: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteCommandOutput($"Unable to load symbols: {ex.Message}");
            }
        }

        private void WriteHelp()
        {
            int firstHelpLine = commandOutput.Count;
            string[] lines =
            [
                "EXECUTION",
                "run (g)                 Resume 6502 execution.",
                "pause                   Pause execution at the next instruction boundary.",
                "n [count] (step)        Execute one instruction, or a decimal number of instructions.",
                "over (o)                Step over JSR; otherwise execute one instruction.",
                "out                     Run until the current subroutine returns.",
                "r (regs)                Display the host 6502 registers and processor flags.",
                "MEMORY AND CODE",
                "m [address] [count]      Display bytes; count is decimal and defaults to 32.",
                "e address byte [...]    Write hexadecimal bytes through the BBC bus while paused.",
                "d [address] [count]      Disassemble instructions; count is decimal and defaults to 6.",
                "BREAKPOINTS AND WATCHPOINTS",
                "b address [end]         Toggle one execution breakpoint, or set an inclusive range.",
                "bc                      Clear every execution breakpoint.",
                "wr address [end]        Toggle a read watchpoint or inclusive address range.",
                "ww address [end]        Toggle a write watchpoint or inclusive address range.",
                "wc                      Clear every read and write watchpoint.",
                "clear                   Clear all breakpoints and watchpoints.",
                "HARDWARE",
                "ss / su                 Show System VIA / User VIA state.",
                "sv / sd / st            Show video / disc controller / Tube state.",
                "SYMBOLS",
                "sl path                 Load an external symbol file, replacing the previous one.",
                "sc                      Unload external symbols; built-in BBC symbols remain.",
                "symbols [filter]        List symbols, optionally filtered by part of the name.",
                "symbol name/address     Resolve a symbol to an address or an address to a symbol.",
                "Addresses are hexadecimal and accept $, &, or 0x prefixes; symbol+offset is valid.",
                "Ctrl+C copies the selected panel; command output copies its complete retained history.",
                "Ctrl+V pastes clipboard text into the command entry as one command line.",
                "Use the mouse wheel over command output to read the rest of this help."
            ];

            foreach (string line in lines)
                WriteCommandOutput(line);

            commandScrollOffset = Math.Max(0, commandOutput.Count - firstHelpLine - CommandOutputVisibleLines);
        }

        private void StartCommandSteps(int count)
        {
            if (!paused())
            {
                WriteCommandOutput("Pause the CPU before stepping");
                return;
            }

            pendingCommandSteps = count;
            observedCompletedSteps = cpu.CompletedSingleSteps;
            if (!step())
            {
                pendingCommandSteps = 0;
                WriteCommandOutput("Unable to request a CPU step");
            }
        }

        private void ContinueCommandSteps()
        {
            if (pendingCommandSteps <= 0 || cpu.CompletedSingleSteps == observedCompletedSteps)
                return;

            observedCompletedSteps = cpu.CompletedSingleSteps;
            pendingCommandSteps--;
            disassemblyAddress = (ushort)cpu.registers.PC;
            if (pendingCommandSteps == 0)
            {
                WriteCommandOutput($"Stopped at ${cpu.registers.PC & 0xFFFF:X4}");
                return;
            }

            if (!step())
            {
                pendingCommandSteps = 0;
                WriteCommandOutput("Stepping stopped");
            }
        }

        private void ExecuteMemoryCommand(string[] parts)
        {
            ushort address = parts.Length > 1 ? ParseAddress(parts[1]) : memoryAddress;
            int count = parts.Length > 2 ? ParseCount(parts[2], 1, 256) : 32;
            memoryAddress = (ushort)(address & 0xFFF8);
            for (int offset = 0; offset < count; offset += 8)
            {
                int lineCount = Math.Min(8, count - offset);
                StringBuilder line = new StringBuilder($"{(ushort)(address + offset):X4}:");
                for (int i = 0; i < lineCount; i++)
                    line.Append($" {readByte((ushort)(address + offset + i)):X2}");
                WriteCommandOutput(line.ToString());
            }
        }

        private void ExecuteEnterMemoryCommand(string[] parts)
        {
            if (!paused())
                throw new ArgumentException("Pause the CPU before editing memory");
            if (parts.Length < 3)
                throw new ArgumentException("Usage: e address byte [byte ...]");
            if (parts.Length > 258)
                throw new ArgumentException("A memory edit is limited to 256 bytes");

            ushort start = ParseAddress(parts[1]);
            byte[] values = parts[2..].Select(ParseByte).ToArray();
            for (int i = 0; i < values.Length; i++)
                writeByte((ushort)(start + i), values[i]);

            int count = values.Length;
            memoryAddress = (ushort)(start & 0xFFF8);
            if (count == 1)
                WriteCommandOutput($"${start:X4} = ${values[0]:X2}");
            else
                WriteCommandOutput($"Wrote {count} bytes at ${start:X4}-${(ushort)(start + count - 1):X4}");
        }

        private void ExecuteDisassembleCommand(string[] parts)
        {
            ushort address = parts.Length > 1 ? ParseAddress(parts[1]) : disassemblyAddress;
            int count = parts.Length > 2 ? ParseCount(parts[2], 1, 32) : 6;
            disassemblyAddress = address;
            for (int i = 0; i < count; i++)
            {
                DecodedInstruction instruction = Decode(address);
                WriteCommandOutput($"{address:X4}  {instruction.Bytes,-8} {instruction.Text}");
                address = (ushort)(address + instruction.Length);
            }
        }

        private void ExecuteBreakpointCommand(string[] parts)
        {
            if (parts.Length < 2)
                throw new ArgumentException("Usage: b address [end]");

            ushort start = ParseAddress(parts[1]);
            if (parts.Length == 2)
            {
                bool added;
                lock (breakpointLock)
                {
                    added = !breakpoints.Remove(start);
                    if (added) breakpoints.Add(start);
                }
                WriteCommandOutput($"Breakpoint {(added ? "set" : "cleared")} at ${start:X4}");
                return;
            }

            ushort end = ParseAddress(parts[2]);
            if (end < start)
                throw new ArgumentException("Breakpoint range end must not precede its start");
            lock (breakpointLock)
            {
                for (int address = start; address <= end; address++)
                    breakpoints.Add((ushort)address);
            }
            WriteCommandOutput($"Breakpoint range set ${start:X4}-${end:X4}");
        }

        private void WriteRegisters()
        {
            Registers r = cpu.registers;
            WriteCommandOutput($"PC={r.PC & 0xFFFF:X4} A={r.A:X2} X={r.X:X2} Y={r.Y:X2} SP={r.S:X2} P={r.P:X2}");
            WriteCommandOutput(FormatFlags(r.P));
        }

        private void LoadSymbols(string entered)
        {
            int separator = entered.IndexOfAny([' ', '\t']);
            if (separator < 0 || separator == entered.Length - 1)
                throw new ArgumentException("Usage: sl path");

            string path = entered[(separator + 1)..].Trim();
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
                path = path[1..^1];
            int count = symbols.Load(path);
            WriteCommandOutput($"Loaded {count} external symbols from {Path.GetFileName(path)}");
        }

        private void ListSymbols(string? filter)
        {
            (string Name, ushort Address, bool External)[] matches = symbols.Find(filter).Take(100).ToArray();
            if (matches.Length == 0)
            {
                WriteCommandOutput(filter is null ? "No symbols loaded" : $"No symbols match: {filter}");
                return;
            }
            foreach ((string name, ushort address, bool external) in matches)
                WriteCommandOutput($"{address:X4}  {name}{(external ? "  [external]" : string.Empty)}");
            if (matches.Length == 100)
                WriteCommandOutput("First 100 matches shown; use a filter to narrow the list");
        }

        private void LookupSymbol(string[] parts)
        {
            if (parts.Length != 2)
                throw new ArgumentException("Usage: symbol name/address");

            if (symbols.TryAddress(parts[1], out ushort address))
            {
                WriteCommandOutput($"{parts[1]} = ${address:X4}");
                return;
            }
            address = ParseAddress(parts[1]);
            string? name = symbols.FormatAddress(address, nearest: true);
            WriteCommandOutput(name is null ? $"No symbol for ${address:X4}" : $"${address:X4}  {name}");
        }

        private void WriteHardwareState(HardwareTab tab)
        {
            selectedHardwareTab = tab;
            foreach (string line in GetHardwareState(tab))
                WriteCommandOutput(line);
        }

        private string[] GetHardwareState(HardwareTab tab) => tab switch
        {
            HardwareTab.SystemVia => systemVia.GetDebuggerState(),
            HardwareTab.UserVia => userVia.GetDebuggerState(),
            HardwareTab.Video => video.GetDebuggerState(),
            HardwareTab.Disc => discController() switch
            {
                Intel8271_Disk intel8271 => intel8271.GetDebuggerState(),
                WD1770_Disk wd1770 => wd1770.GetDebuggerState(),
                _ => ["Unknown disc controller"]
            },
            HardwareTab.Tube => tubeEnabled() ? tubeUla.GetDebuggerState() : ["Tube disabled"],
            _ => []
        };

        private void ExecuteWatchpointCommand(string[] parts, bool write)
        {
            if (parts.Length < 2)
                throw new ArgumentException($"Usage: {(write ? "ww" : "wr")} address [end]");

            ushort start = ParseAddress(parts[1]);
            ushort end = parts.Length > 2 ? ParseAddress(parts[2]) : start;
            if (end < start)
                throw new ArgumentException("Watchpoint range end must not precede its start");

            lock (breakpointLock)
            {
                List<WatchRange> watchpoints = write ? writeWatchpoints : readWatchpoints;
                WatchRange range = new WatchRange(start, end);
                int existing = watchpoints.IndexOf(range);
                if (existing >= 0)
                {
                    watchpoints.RemoveAt(existing);
                    WriteCommandOutput($"{(write ? "Write" : "Read")} watchpoint cleared {FormatRange(range)}");
                }
                else
                {
                    watchpoints.Add(range);
                    WriteCommandOutput($"{(write ? "Write" : "Read")} watchpoint set {FormatRange(range)}");
                }
                UpdateMemoryWatchCallbacks();
            }
        }

        private static string FormatRange(WatchRange range) => range.Start == range.End
            ? $"${range.Start:X4}"
            : $"${range.Start:X4}-${range.End:X4}";

        private ushort ParseAddress(string value)
        {
            string text = value.Trim();
            if (TryParseNumericAddress(text, out ushort address))
                return address;
            if (symbols.TryAddress(text, out address))
                return address;

            int operatorIndex = text.IndexOfAny(['+', '-'], 1);
            if (operatorIndex > 0 && symbols.TryAddress(text[..operatorIndex], out ushort baseAddress)
                && TryParseHexOffset(text[(operatorIndex + 1)..], out int offset))
            {
                int result = text[operatorIndex] == '+' ? baseAddress + offset : baseAddress - offset;
                if (result is >= 0 and <= 0xFFFF)
                    return (ushort)result;
            }
            throw new ArgumentException($"Invalid address or unknown symbol: {value}");
        }

        private static bool TryParseNumericAddress(string value, out ushort address)
        {
            string text = value.Trim();
            if (text.StartsWith('$') || text.StartsWith('&')) text = text[1..];
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        private static bool TryParseHexOffset(string value, out int offset)
        {
            string text = value.Trim();
            if (text.StartsWith('$') || text.StartsWith('&')) text = text[1..];
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out offset);
        }

        private static byte ParseByte(string value)
        {
            string text = value.Trim();
            if (text.StartsWith('$') || text.StartsWith('&')) text = text[1..];
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (!byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out byte result))
                throw new ArgumentException($"Invalid byte value: {value}");
            return result;
        }

        private static int ParseCount(string value, int minimum, int maximum)
        {
            if (!int.TryParse(value, out int count) || count < minimum || count > maximum)
                throw new ArgumentException($"Count must be between {minimum} and {maximum}");
            return count;
        }

        private void WriteCommandOutput(string line)
        {
            commandOutput.Add(line);
            commandScrollOffset = 0;
            if (commandOutput.Count > 200)
                commandOutput.RemoveRange(0, commandOutput.Count - 200);
        }

        private void DrawCommandOutput()
        {
            int last = Math.Max(0, commandOutput.Count - commandScrollOffset);
            int first = Math.Max(0, last - CommandOutputVisibleLines);
            float y = CommandTop + 48;
            canvas.Save();
            canvas.ClipRect(new SKRect(9, CommandTop + 35, DisassemblyRight - 1, CommandInputTop - 7));
            for (int i = first; i < last; i++, y += 19)
                DrawText(commandOutput[i], 24, y, commandOutput[i].StartsWith('>') ? Accent : Text, small: true);
            canvas.Restore();
        }

        private void DrawDisplayPreview(SKRect bounds)
        {
            float scale = Math.Min(bounds.Width / displayFrame.Width, bounds.Height / displayFrame.Height);
            float width = displayFrame.Width * scale;
            float height = displayFrame.Height * scale;
            SKRect destination = new SKRect(
                bounds.MidX - width / 2,
                bounds.MidY - height / 2,
                bounds.MidX + width / 2,
                bounds.MidY + height / 2);

            using SKPaint paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true
            };
            canvas.DrawBitmap(displayFrame, destination, paint);
            Stroke(destination, Border);
        }

        private bool HandleAddressKey(int keySym)
        {
            if (activeAddressField == AddressField.None)
                return false;

            if (keySym == SDLK_ESCAPE)
            {
                activeAddressField = AddressField.None;
                addressEntry = string.Empty;
                return true;
            }

            if (keySym == SDLK_BACKSPACE)
            {
                if (addressEntry.Length > 0)
                    addressEntry = addressEntry[..^1];
                return true;
            }

            if (keySym is SDLK_RETURN or SDLK_KP_ENTER)
            {
                try
                {
                    ushort address = ParseAddress(addressEntry);
                    if (activeAddressField == AddressField.Memory)
                        memoryAddress = (ushort)(address & 0xFFF8);
                    else
                        disassemblyAddress = address;
                }
                catch (ArgumentException ex)
                {
                    WriteCommandOutput(ex.Message);
                }
                activeAddressField = AddressField.None;
                addressEntry = string.Empty;
                return true;
            }
            return true;
        }

        private void MoveDisassembly(int direction, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (direction > 0)
                {
                    disassemblyAddress = (ushort)(disassemblyAddress + Decode(disassemblyAddress).Length);
                    continue;
                }

                // A 6502 instruction is at most three bytes. Prefer the furthest
                // valid predecessor so scrolling remains aligned with displayed code.
                ushort previous = (ushort)(disassemblyAddress - 1);
                for (int length = 3; length >= 1; length--)
                {
                    ushort candidate = (ushort)(disassemblyAddress - length);
                    if (Decode(candidate).Length == length)
                    {
                        previous = candidate;
                        break;
                    }
                }
                disassemblyAddress = previous;
            }
        }

        private ushort GetDisassemblyRowAddress(int row)
        {
            ushort address = disassemblyAddress;
            for (int i = 0; i < row; i++)
                address = (ushort)(address + Decode(address).Length);
            return address;
        }

        private void ToggleBreakpoint(ushort address)
        {
            lock (breakpointLock)
            {
                if (!breakpoints.Remove(address))
                    breakpoints.Add(address);
            }
        }

        private void ClearBreakpoints()
        {
            lock (breakpointLock)
            {
                breakpoints.Clear();
                temporaryBreakpoints.Clear();
            }
            breakpointHitAt = null;
            temporaryStopDescription = null;
        }

        private void ClearWatchpoints()
        {
            lock (breakpointLock)
            {
                readWatchpoints.Clear();
                writeWatchpoints.Clear();
                pendingWatchedAccess = null;
                stoppedWatchedAccess = null;
                UpdateMemoryWatchCallbacks();
            }
        }

        private void UpdateMemoryWatchCallbacks()
        {
            cpu.OnMemoryRead = readWatchpoints.Count == 0 ? null : WatchMemoryRead;
            cpu.OnMemoryWrite = writeWatchpoints.Count == 0 ? null : WatchMemoryWrite;
        }

        private void WatchMemoryRead(ushort address, byte value, ushort instructionAddress) =>
            RecordWatchedAccess(address, value, instructionAddress, write: false);

        private void WatchMemoryWrite(ushort address, byte value, ushort instructionAddress) =>
            RecordWatchedAccess(address, value, instructionAddress, write: true);

        private void RecordWatchedAccess(ushort address, byte value, ushort instructionAddress, bool write)
        {
            lock (breakpointLock)
            {
                if (pendingWatchedAccess.HasValue)
                    return;
                List<WatchRange> watchpoints = write ? writeWatchpoints : readWatchpoints;
                if (watchpoints.Any(range => address >= range.Start && address <= range.End))
                    pendingWatchedAccess = new WatchedAccess(address, value, instructionAddress, write);
            }
        }

        private void ClearTemporaryBreakpoints()
        {
            lock (breakpointLock)
                temporaryBreakpoints.Clear();
        }

        private bool HasBreakpoint(ushort address)
        {
            lock (breakpointLock)
            {
                if (pendingWatchedAccess.HasValue)
                {
                    stoppedWatchedAccess = pendingWatchedAccess;
                    pendingWatchedAccess = null;
                    return true;
                }
                return breakpoints.Contains(address) || temporaryBreakpoints.Contains(address);
            }
        }

        private bool HasPermanentBreakpoint(ushort address)
        {
            lock (breakpointLock)
                return breakpoints.Contains(address);
        }

        private bool HasTemporaryBreakpoint(ushort address)
        {
            lock (breakpointLock)
                return temporaryBreakpoints.Contains(address);
        }

        private int BreakpointCount
        {
            get
            {
                lock (breakpointLock)
                    return breakpoints.Count;
            }
        }

        private int WatchpointCount
        {
            get
            {
                lock (breakpointLock)
                    return readWatchpoints.Count + writeWatchpoints.Count;
            }
        }

        private DecodedInstruction Decode(ushort address)
        {
            byte opcode = readByte(address);
            OpCode op = OpCodes[opcode];
            byte operand1 = readByte((ushort)(address + 1));
            byte operand2 = readByte((ushort)(address + 2));
            string bytes = op.Length switch
            {
                1 => $"{opcode:X2}",
                2 => $"{opcode:X2} {operand1:X2}",
                _ => $"{opcode:X2} {operand1:X2} {operand2:X2}"
            };
            string operand = FormatOperand(op.Mode, address, operand1, operand2);
            string instruction = string.IsNullOrEmpty(operand) ? op.Mnemonic : $"{op.Mnemonic} {operand}";
            if (symbols.TryExactName(address, out string label))
                instruction = $"{label}: {instruction}";
            return new DecodedInstruction(op.Length, bytes, instruction);
        }

        private string FormatOperand(AddressMode mode, ushort address, byte lo, byte hi)
        {
            ushort word = (ushort)(lo | hi << 8);
            return mode switch
            {
                AddressMode.Imp => string.Empty,
                AddressMode.Acc => "A",
                AddressMode.Imm => $"#${lo:X2}",
                AddressMode.Zp => FormatSymbolicAddress(lo, 2),
                AddressMode.ZpX => $"{FormatSymbolicAddress(lo, 2)},X",
                AddressMode.ZpY => $"{FormatSymbolicAddress(lo, 2)},Y",
                AddressMode.Abs => FormatSymbolicAddress(word, 4),
                AddressMode.AbsX => $"{FormatSymbolicAddress(word, 4)},X",
                AddressMode.AbsY => $"{FormatSymbolicAddress(word, 4)},Y",
                AddressMode.Ind => $"({FormatSymbolicAddress(word, 4)})",
                AddressMode.IndX => $"(${lo:X2},X)",
                AddressMode.IndY => $"(${lo:X2}),Y",
                AddressMode.Rel => FormatSymbolicAddress((ushort)(address + 2 + (sbyte)lo), 4),
                _ => string.Empty
            };
        }

        private string FormatSymbolicAddress(ushort address, int digits)
        {
            string? name = symbols.FormatAddress(address, nearest: digits == 4);
            return name is null ? $"${address.ToString($"X{digits}")}" : name;
        }

        private void DrawPanel(SKRect rect, string title)
        {
            Fill(rect, Panel);
            Stroke(rect, Border);
            canvas.DrawText(title, rect.Left + 12, rect.Top + 24, titlePaint);
            Line(rect.Left, rect.Top + 34, rect.Right, rect.Top + 34, Border);
        }

        private void DrawPlaceholder(float x, float y, string text) => DrawText(text, x, y, DimText, small: true);

        private void DrawButton(SKRect rect, string label, bool active, bool enabled = true)
        {
            Fill(rect, active ? CurrentInstruction : Panel);
            Stroke(rect, active ? Accent : Border);
            DrawText(label, rect.Left + 9, rect.Top + 19, enabled ? Text : DimText, small: true);
        }

        private void DrawText(string value, float x, float baseline, uint colour, bool small = false)
        {
            SKPaint paint = small ? smallPaint : textPaint;
            paint.Color = new SKColor(colour);
            canvas.DrawText(value, x, baseline, paint);
        }

        private void Fill(SKRect rect, uint colour)
        {
            using SKPaint paint = new SKPaint { Color = new SKColor(colour), Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, paint);
        }

        private void Stroke(SKRect rect, uint colour)
        {
            using SKPaint paint = new SKPaint { Color = new SKColor(colour), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(rect, paint);
        }

        private void Circle(float x, float y, float radius, uint colour)
        {
            using SKPaint paint = new SKPaint { Color = new SKColor(colour), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(x, y, radius, paint);
        }

        private void Line(float x1, float y1, float x2, float y2, uint colour)
        {
            using SKPaint paint = new SKPaint { Color = new SKColor(colour), StrokeWidth = 1 };
            canvas.DrawLine(x1, y1, x2, y2, paint);
        }

        private SKPaint CreatePaint(float size, uint colour, bool bold = false) => new SKPaint
        {
            Typeface = bold ? SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? typeface : typeface,
            TextSize = size,
            Color = new SKColor(colour),
            IsAntialias = true
        };

        private static string FormatFlags(byte p)
        {
            const string names = "NV-BDIZC";
            Span<char> result = stackalloc char[15];
            for (int bit = 7, output = 0; bit >= 0; bit--, output += 2)
            {
                char name = names[7 - bit];
                result[output] = (p & (1 << bit)) != 0 ? name : char.ToLowerInvariant(name);
                if (output + 1 < result.Length)
                    result[output + 1] = ' ';
            }
            return new string(result);
        }

        private void EnsureWindow()
        {
            if (window != IntPtr.Zero)
                return;

            window = SDL_CreateWindow("BBC Model B Debugger", 100, 100, Width, Height,
                SDL_WINDOW_HIDDEN | SDL_WINDOW_RESIZABLE | SDL_WINDOW_ALLOW_HIGHDPI);
            if (window == IntPtr.Zero)
                throw new InvalidOperationException("SDL_CreateWindow failed for debugger.");

            windowId = SDL_GetWindowID(window);
            renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
            if (renderer == IntPtr.Zero)
                renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_SOFTWARE);
            if (renderer == IntPtr.Zero)
                throw new InvalidOperationException("SDL_CreateRenderer failed for debugger.");

            SDL_RenderSetLogicalSize(renderer, Width, Height);
            texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ARGB8888, SDL_TEXTUREACCESS_STREAMING, Width, Height);
            if (texture == IntPtr.Zero)
                throw new InvalidOperationException("SDL_CreateTexture failed for debugger.");
        }

        public void Dispose()
        {
            if (disposed)
                return;
            if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
            if (renderer != IntPtr.Zero) SDL_DestroyRenderer(renderer);
            if (window != IntPtr.Zero) SDL_DestroyWindow(window);
            if (cpu.ShouldBreakBeforeInstruction == HasBreakpoint)
                cpu.ShouldBreakBeforeInstruction = null;
            if (cpu.OnMemoryRead == WatchMemoryRead) cpu.OnMemoryRead = null;
            if (cpu.OnMemoryWrite == WatchMemoryWrite) cpu.OnMemoryWrite = null;
            textPaint.Dispose();
            titlePaint.Dispose();
            smallPaint.Dispose();
            typeface.Dispose();
            canvas.Dispose();
            bitmap.Dispose();
            displayFrame.Dispose();
            if (displayFrameHandle.IsAllocated)
                displayFrameHandle.Free();
            disposed = true;
        }

        private readonly record struct DecodedInstruction(int Length, string Bytes, string Text);
        private readonly record struct WatchRange(ushort Start, ushort End);
        private readonly record struct WatchedAccess(ushort Address, byte Value, ushort InstructionAddress, bool Write);
        private readonly record struct OpCode(string Mnemonic, AddressMode Mode)
        {
            public int Length => Mode switch
            {
                AddressMode.Imp or AddressMode.Acc => 1,
                AddressMode.Abs or AddressMode.AbsX or AddressMode.AbsY or AddressMode.Ind => 3,
                _ => 2
            };
        }

        private enum AddressMode { Imp, Acc, Imm, Zp, ZpX, ZpY, Abs, AbsX, AbsY, Ind, IndX, IndY, Rel }
        private enum AddressField { None, Memory, Disassembly }
        private enum HardwareTab { Cpu, SystemVia, UserVia, Video, Disc, Tube }
        private enum ClipboardPanel { Memory, Disassembly, Hardware, CommandOutput }

        private static readonly OpCode[] OpCodes = CreateOpCodes();

        private static OpCode[] CreateOpCodes()
        {
            string[] rows =
            [
                "BRK:Imp ORA:IndX KIL:Imp SLO:IndX NOP:Zp ORA:Zp ASL:Zp SLO:Zp PHP:Imp ORA:Imm ASL:Acc ANC:Imm NOP:Abs ORA:Abs ASL:Abs SLO:Abs",
                "BPL:Rel ORA:IndY KIL:Imp SLO:IndY NOP:ZpX ORA:ZpX ASL:ZpX SLO:ZpX CLC:Imp ORA:AbsY NOP:Imp SLO:AbsY NOP:AbsX ORA:AbsX ASL:AbsX SLO:AbsX",
                "JSR:Abs AND:IndX KIL:Imp RLA:IndX BIT:Zp AND:Zp ROL:Zp RLA:Zp PLP:Imp AND:Imm ROL:Acc ANC:Imm BIT:Abs AND:Abs ROL:Abs RLA:Abs",
                "BMI:Rel AND:IndY KIL:Imp RLA:IndY NOP:ZpX AND:ZpX ROL:ZpX RLA:ZpX SEC:Imp AND:AbsY NOP:Imp RLA:AbsY NOP:AbsX AND:AbsX ROL:AbsX RLA:AbsX",
                "RTI:Imp EOR:IndX KIL:Imp SRE:IndX NOP:Zp EOR:Zp LSR:Zp SRE:Zp PHA:Imp EOR:Imm LSR:Acc ALR:Imm JMP:Abs EOR:Abs LSR:Abs SRE:Abs",
                "BVC:Rel EOR:IndY KIL:Imp SRE:IndY NOP:ZpX EOR:ZpX LSR:ZpX SRE:ZpX CLI:Imp EOR:AbsY NOP:Imp SRE:AbsY NOP:AbsX EOR:AbsX LSR:AbsX SRE:AbsX",
                "RTS:Imp ADC:IndX KIL:Imp RRA:IndX NOP:Zp ADC:Zp ROR:Zp RRA:Zp PLA:Imp ADC:Imm ROR:Acc ARR:Imm JMP:Ind ADC:Abs ROR:Abs RRA:Abs",
                "BVS:Rel ADC:IndY KIL:Imp RRA:IndY NOP:ZpX ADC:ZpX ROR:ZpX RRA:ZpX SEI:Imp ADC:AbsY NOP:Imp RRA:AbsY NOP:AbsX ADC:AbsX ROR:AbsX RRA:AbsX",
                "NOP:Imm STA:IndX NOP:Imm SAX:IndX STY:Zp STA:Zp STX:Zp SAX:Zp DEY:Imp NOP:Imm TXA:Imp XAA:Imm STY:Abs STA:Abs STX:Abs SAX:Abs",
                "BCC:Rel STA:IndY KIL:Imp AHX:IndY STY:ZpX STA:ZpX STX:ZpY SAX:ZpY TYA:Imp STA:AbsY TXS:Imp TAS:AbsY SHY:AbsX STA:AbsX SHX:AbsY AHX:AbsY",
                "LDY:Imm LDA:IndX LDX:Imm LAX:IndX LDY:Zp LDA:Zp LDX:Zp LAX:Zp TAY:Imp LDA:Imm TAX:Imp LAX:Imm LDY:Abs LDA:Abs LDX:Abs LAX:Abs",
                "BCS:Rel LDA:IndY KIL:Imp LAX:IndY LDY:ZpX LDA:ZpX LDX:ZpY LAX:ZpY CLV:Imp LDA:AbsY TSX:Imp LAS:AbsY LDY:AbsX LDA:AbsX LDX:AbsY LAX:AbsY",
                "CPY:Imm CMP:IndX NOP:Imm DCP:IndX CPY:Zp CMP:Zp DEC:Zp DCP:Zp INY:Imp CMP:Imm DEX:Imp AXS:Imm CPY:Abs CMP:Abs DEC:Abs DCP:Abs",
                "BNE:Rel CMP:IndY KIL:Imp DCP:IndY NOP:ZpX CMP:ZpX DEC:ZpX DCP:ZpX CLD:Imp CMP:AbsY NOP:Imp DCP:AbsY NOP:AbsX CMP:AbsX DEC:AbsX DCP:AbsX",
                "CPX:Imm SBC:IndX NOP:Imm ISC:IndX CPX:Zp SBC:Zp INC:Zp ISC:Zp INX:Imp SBC:Imm NOP:Imp SBC:Imm CPX:Abs SBC:Abs INC:Abs ISC:Abs",
                "BEQ:Rel SBC:IndY KIL:Imp ISC:IndY NOP:ZpX SBC:ZpX INC:ZpX ISC:ZpX SED:Imp SBC:AbsY NOP:Imp ISC:AbsY NOP:AbsX SBC:AbsX INC:AbsX ISC:AbsX"
            ];

            OpCode[] result = new OpCode[256];
            int index = 0;
            foreach (string row in rows)
            {
                foreach (string entry in row.Split(' '))
                {
                    string[] parts = entry.Split(':');
                    result[index++] = new OpCode(parts[0], Enum.Parse<AddressMode>(parts[1]));
                }
            }
            return result;
        }

        private const string SdlLibrary = "SDL2";
        private const uint SDL_WINDOWEVENT = 0x200;
        private const uint SDL_KEYDOWN = 0x300;
        private const uint SDL_TEXTINPUT = 0x303;
        private const uint SDL_MOUSEBUTTONDOWN = 0x401;
        private const uint SDL_MOUSEWHEEL = 0x403;
        private const byte SDL_WINDOWEVENT_CLOSE = 0x0E;
        private const byte SDL_BUTTON_LEFT = 1;
        private const int SDLK_ESCAPE = 27;
        private const int SDLK_BACKSPACE = 8;
        private const int SDLK_RETURN = 13;
        private const int SDLK_C = 99;
        private const int SDLK_V = 118;
        private const int SDLK_F5 = 1073741886;
        private const int SDLK_F6 = 1073741887;
        private const int SDLK_F10 = 1073741891;
        private const int SDLK_F9 = 1073741890;
        private const int SDLK_F11 = 1073741892;
        private const int SDLK_KP_ENTER = 1073741912;
        private const int SDLK_DOWN = 1073741905;
        private const int SDLK_UP = 1073741906;
        private const int KMOD_SHIFT = 0x0003;
        private const int KMOD_CTRL = 0x00C0;
        private const int KMOD_GUI = 0x0C00;
        private const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;
        private const uint SDL_WINDOW_HIDDEN = 0x00000008;
        private const uint SDL_WINDOW_RESIZABLE = 0x00000020;
        private const uint SDL_WINDOW_ALLOW_HIGHDPI = 0x00002000;
        private const uint SDL_RENDERER_SOFTWARE = 0x00000001;
        private const uint SDL_RENDERER_ACCELERATED = 0x00000002;
        private const uint SDL_RENDERER_PRESENTVSYNC = 0x00000004;
        private const uint SDL_PIXELFORMAT_ARGB8888 = 0x16362004;
        private const int SDL_TEXTUREACCESS_STREAMING = 1;

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_DestroyWindow(IntPtr window);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetWindowID(IntPtr window);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_ShowWindow(IntPtr window);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_HideWindow(IntPtr window);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_RaiseWindow(IntPtr window);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_DestroyRenderer(IntPtr renderer);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderSetLogicalSize(IntPtr renderer, int w, int h);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderWindowToLogical(IntPtr renderer, int windowX, int windowY, out float logicalX, out float logicalY);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetMouseState(out int x, out int y);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GetModState();
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GetClipboardText();
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_free(IntPtr memblock);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_DestroyTexture(IntPtr texture);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderClear(IntPtr renderer);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr source, IntPtr destination);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_RenderPresent(IntPtr renderer);
    }
}
