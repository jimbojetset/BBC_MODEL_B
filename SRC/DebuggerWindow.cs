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
using BBC.CPU;
using SkiaSharp;

namespace BBC
{
    public sealed class DebuggerWindow : IDisposable
    {
        private const int Width = 1280;
        private const int Height = 800;
        private const int ToolbarHeight = 42;
        private const int CommandTop = 570;
        private const int CommandInputTop = 736;
        private const int StatusTop = 770;
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
        private readonly Action pause;
        private readonly Action resume;
        private readonly Func<bool> step;
        private readonly Func<bool> paused;
        private readonly SKBitmap bitmap;
        private readonly SKCanvas canvas;
        private readonly SKTypeface typeface;
        private readonly SKPaint textPaint;
        private readonly SKPaint titlePaint;
        private readonly SKPaint smallPaint;
        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private uint windowId;
        private bool visible;
        private bool disposed;

        public DebuggerWindow(
            CPU_6502 cpu,
            Func<ushort, byte> readByte,
            Action pause,
            Action resume,
            Func<bool> step,
            Func<bool> paused)
        {
            this.cpu = cpu;
            this.readByte = readByte;
            this.pause = pause;
            this.resume = resume;
            this.step = step;
            this.paused = paused;

            bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            canvas = new SKCanvas(bitmap);
            typeface = SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
            textPaint = CreatePaint(15, Text);
            titlePaint = CreatePaint(14, DimText, bold: true);
            smallPaint = CreatePaint(13, DimText);
        }

        public bool Visible => visible;

        public void Show()
        {
            EnsureWindow();
            visible = true;
            pause();
            SDL_ShowWindow(window);
            SDL_RaiseWindow(window);
            Present();
        }

        public bool HandleEvent(uint type, uint eventWindowId, byte windowEvent, int keySym, byte mouseButton, int mouseX, int mouseY)
        {
            if (windowId == 0 || eventWindowId != windowId)
                return false;

            if (type == SDL_WINDOWEVENT && windowEvent == SDL_WINDOWEVENT_CLOSE)
            {
                CloseAndResume();
                return true;
            }

            if (type == SDL_KEYDOWN)
            {
                switch (keySym)
                {
                    case SDLK_F5:
                        resume();
                        break;
                    case SDLK_F6:
                        pause();
                        break;
                    case SDLK_F10:
                        step();
                        break;
                    case SDLK_ESCAPE:
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
                        resume();
                    else if (logicalX is >= 88 and < 170)
                        pause();
                    else if (logicalX is >= 176 and < 250)
                        step();
                }
                return true;
            }

            return true;
        }

        private void CloseAndResume()
        {
            visible = false;
            SDL_HideWindow(window);
            resume();
        }

        public void Present()
        {
            if (!visible || renderer == IntPtr.Zero)
                return;

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
            DrawPlaceholder(24, 94, "Memory viewer will be added in phase 3");

            DrawPanel(new SKRect(358, 48, 972, CommandTop - 8), "DISASSEMBLY");
            DrawDisassembly(374, 91);

            DrawPanel(new SKRect(980, 48, 1272, CommandTop - 8), "CPU / HARDWARE");
            DrawHardwareTabs();
            DrawRegisters(996, 126);

            DrawPanel(new SKRect(8, CommandTop, 1272, CommandInputTop - 6), "COMMAND OUTPUT");
            DrawPlaceholder(24, CommandTop + 48, "Command history and results will be added in phase 5");

            Fill(new SKRect(8, CommandInputTop, 1272, StatusTop - 6), PanelDark);
            Stroke(new SKRect(8, CommandInputTop, 1272, StatusTop - 6), Border);
            DrawText(">", 20, CommandInputTop + 24, Accent);
            DrawText("Command entry will be added in phase 5", 42, CommandInputTop + 24, DimText);

            Fill(new SKRect(0, StatusTop, Width, Height), PanelDark);
            DrawText(paused() ? "PAUSED" : "RUNNING", 14, 791, paused() ? 0xFFFFC857 : 0xFF67D391);
            DrawText($"PC ${cpu.registers.PC & 0xFFFF:X4}", 116, 791, Text);
            DrawText($"{cpu.TotalCycles:N0} cycles", 1030, 791, DimText);
        }

        private void DrawToolbar()
        {
            Fill(new SKRect(0, 0, Width, ToolbarHeight), PanelDark);
            DrawButton(new SKRect(10, 7, 82, 35), "Run F5", !paused());
            DrawButton(new SKRect(88, 7, 170, 35), "Break F6", paused());
            DrawButton(new SKRect(176, 7, 250, 35), "Step F10", false);
            DrawButton(new SKRect(256, 7, 350, 35), "Step over", false, enabled: false);
            DrawButton(new SKRect(356, 7, 442, 35), "Step out", false, enabled: false);
            DrawText("Host 6502", 1150, 27, Accent);
        }

        private void DrawHardwareTabs()
        {
            string[] tabs = ["CPU", "SYS", "USER", "VIDEO", "DISC", "TUBE"];
            float[] widths = [42, 42, 46, 48, 44, 44];
            float x = 990;
            for (int i = 0; i < tabs.Length; i++)
            {
                float width = widths[i];
                Fill(new SKRect(x, 78, x + width, 105), i == 0 ? CurrentInstruction : PanelDark);
                DrawText(tabs[i], x + 5, 96, i == 0 ? Text : DimText, small: true);
                x += width + 2;
            }
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
            DrawText(FormatFlags(p), x, y + 153, Text);
            DrawText("Interrupts", x, y + 204, DimText);
            DrawText($"IRQ  {(cpu.IrqLineAsserted ? "asserted" : "clear")}", x, y + 232, Text);
            DrawText($"CPU  {(paused() ? "paused" : "running")}", x, y + 260, Text);
            DrawText("Hardware tabs will be populated", x, y + 320, DimText, small: true);
            DrawText("in later phases", x, y + 340, DimText, small: true);
        }

        private void DrawDisassembly(float x, float y)
        {
            ushort address = (ushort)cpu.registers.PC;
            for (int row = 0; row < 22; row++)
            {
                DecodedInstruction instruction = Decode(address);
                float baseline = y + row * 20;
                if (row == 0)
                    Fill(new SKRect(x - 8, baseline - 15, 956, baseline + 5), CurrentInstruction);

                DrawText(row == 0 ? "▶" : " ", x, baseline, row == 0 ? Accent : DimText);
                DrawText($"{address:X4}", x + 24, baseline, row == 0 ? Accent : Text);
                DrawText(instruction.Bytes, x + 82, baseline, DimText);
                DrawText(instruction.Text, x + 184, baseline, Text);
                address = (ushort)(address + instruction.Length);
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
            return new DecodedInstruction(op.Length, bytes, string.IsNullOrEmpty(operand) ? op.Mnemonic : $"{op.Mnemonic} {operand}");
        }

        private static string FormatOperand(AddressMode mode, ushort address, byte lo, byte hi)
        {
            ushort word = (ushort)(lo | hi << 8);
            return mode switch
            {
                AddressMode.Imp => string.Empty,
                AddressMode.Acc => "A",
                AddressMode.Imm => $"#${lo:X2}",
                AddressMode.Zp => $"${lo:X2}",
                AddressMode.ZpX => $"${lo:X2},X",
                AddressMode.ZpY => $"${lo:X2},Y",
                AddressMode.Abs => $"${word:X4}",
                AddressMode.AbsX => $"${word:X4},X",
                AddressMode.AbsY => $"${word:X4},Y",
                AddressMode.Ind => $"(${word:X4})",
                AddressMode.IndX => $"(${lo:X2},X)",
                AddressMode.IndY => $"(${lo:X2}),Y",
                AddressMode.Rel => $"${(ushort)(address + 2 + (sbyte)lo):X4}",
                _ => string.Empty
            };
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
            textPaint.Dispose();
            titlePaint.Dispose();
            smallPaint.Dispose();
            typeface.Dispose();
            canvas.Dispose();
            bitmap.Dispose();
            disposed = true;
        }

        private readonly record struct DecodedInstruction(int Length, string Bytes, string Text);
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
        private const uint SDL_MOUSEBUTTONDOWN = 0x401;
        private const byte SDL_WINDOWEVENT_CLOSE = 0x0E;
        private const byte SDL_BUTTON_LEFT = 1;
        private const int SDLK_ESCAPE = 27;
        private const int SDLK_F5 = 1073741886;
        private const int SDLK_F6 = 1073741887;
        private const int SDLK_F10 = 1073741891;
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
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_DestroyTexture(IntPtr texture);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderClear(IntPtr renderer);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr source, IntPtr destination);
        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_RenderPresent(IntPtr renderer);
    }
}
