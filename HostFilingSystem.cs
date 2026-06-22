// ============================================================================
// Project:     BBC
// File:        HostFilingSystem.cs
// Description: Host-file bridge for MOS LOAD/RUN/EXEC shortcuts outside DFS.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using BBC.CPU;
using System.Text;

namespace BBC
{

    /// <summary>
    /// Lets a dropped host file behave enough like a BBC file for MOS LOAD,
    /// RUN, and EXEC paths, while real DFS discs stay on the 8271 controller.
    /// </summary>
    public sealed class HostFilingSystem
    {
        private const ushort OsfileEntry = 0xFFDD;
        private const ushort OscliEntry = 0xFFF7;
        private const ushort OsbyteEntry = 0xFFF4;
        private const ushort FscvVector = 0x021E;
        private const ushort DefaultBasicLoadAddress = 0x1900;
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_OSCLI_TRACE") == "1";
        private readonly FlatMemoryBus memory;
        private HostFile[] files = [];
        private string currentDirectory = "$";
        private string? mountedFileName;

        public HostFilingSystem(FlatMemoryBus memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        public string? MountedFileName => mountedFileName;

        public bool MouseCommandFallbackEnabled { get; set; } = true;

        /// <summary>Some AMX software uses *MOUSE/*POINTER before the mouse ROM has claimed those commands.</summary>
        public Action<bool>? MouseEnabledChanged { get; set; }

        /// <summary>Soft keys expand through the MOS keyboard buffer, just as firmware would type them.</summary>
        public Action<string>? QueueKeyboardText { get; set; }

        /// <summary>*EXEC feeds lines through the keyboard stream rather than loading program memory.</summary>
        public Action<string>? QueueKeyboardScript { get; set; }

        /// <summary>BREAK clears MOS state before the soft-key continuation can be replayed.</summary>
        public Action<string?>? BreakCommandObserved { get; set; }

        private readonly string[] softKeyStrings = new string[16];

        public void Unmount()
        {
            files = [];
            mountedFileName = null;
            currentDirectory = "$";
        }

        public void Mount(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A disc/file path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Disc/file not found: {fullPath}", fullPath);

            files = [ReadRawHostFile(fullPath, File.ReadAllBytes(fullPath))];

            if (files.Length == 0)
                throw new InvalidOperationException($"No loadable files found in '{fullPath}'.");

            mountedFileName = Path.GetFileName(fullPath);
            currentDirectory = "$";
        }

        /// <summary>OSFILE is the MOS path behind BASIC LOAD for the mounted host file.</summary>
        public bool TryHandleOsfile(CPU_6502 cpu)
        {
            if ((cpu.registers.PC & 0xFFFF) != OsfileEntry)
                return false;

            byte action = cpu.registers.A;
            if (files.Length == 0 || action is not (0x05 or 0xFF))
                return false;

            ushort controlBlock = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));
            string requestedName = ReadOsString(ReadWord(controlBlock));
            HostFile? matchedFile = FindFile(requestedName);
            Trace($"OSFILE action=${action:X2} name=\"{requestedName}\" match=\"{matchedFile?.Name ?? "<none>"}\"");

            if (!matchedFile.HasValue)
            {
                cpu.registers.A = 0;
                ReturnFromSubroutine(cpu);
                return true;
            }

            HostFile file = matchedFile.Value;

            if (action == 0xFF)
            {
                uint requestedAddress = ReadDword(controlBlock + 2);
                ushort targetAddress = memory.Memory[(controlBlock + 6) & 0xFFFF] == 0
                    ? (ushort)requestedAddress
                    : ToCpuAddress(file.LoadAddress, DefaultBasicLoadAddress);

                for (int i = 0; i < file.Data.Length; i++)
                    memory.Memory[(targetAddress + i) & 0xFFFF] = file.Data[i];
            }

            WriteCatalogueInfo(controlBlock, file);
            cpu.registers.A = 1;
            ReturnFromSubroutine(cpu);
            return true;
        }

        /// <summary>OSCLI carries star commands; this bridge only claims host-file shortcuts and host-side MOS fallbacks.</summary>
        public bool TryHandleOscli(CPU_6502 cpu)
        {
            if ((cpu.registers.PC & 0xFFFF) != OscliEntry)
                return false;

            ushort commandAddress = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));
            string command = ReadOsString(commandAddress).Trim();
            if (command.StartsWith('*'))
                command = command[1..].TrimStart();

            Trace($"OSCLI \"{command}\"");

            if (command.StartsWith('/'))
                command = "RUN " + command[1..].TrimStart();

            if (IsTvCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsTapeCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandleMouseCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandlePointerCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (files.Length == 0)
                return false;

            if (IsBreakCommand(command))
                BreakCommandObserved?.Invoke(GetSoftKeyText(10));

            if (command.Length == 0)
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsBareExecCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandleExecCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandleFxCommand(command, cpu, out bool returnFromOscli))
            {
                if (returnFromOscli)
                    ReturnFromSubroutine(cpu);

                return true;
            }

            if (TryHandleKeyCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsOptCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandleDirCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryParseLoadCommand(command, out string loadName, out ushort? loadAddress))
            {
                HostFile? matchedLoadFile = FindFile(loadName);
                Trace($"LOAD \"{loadName}\" match=\"{matchedLoadFile?.Name ?? "<none>"}\"");
                if (!matchedLoadFile.HasValue)
                {
                    return false;
                }

                HostFile loadFile = matchedLoadFile.Value;
                ushort targetAddress = loadAddress.GetValueOrDefault(ToCpuAddress(loadFile.LoadAddress, DefaultBasicLoadAddress));
                for (int i = 0; i < loadFile.Data.Length; i++)
                    memory.Memory[(targetAddress + i) & 0xFFFF] = loadFile.Data[i];

                ReturnFromSubroutine(cpu);
                return true;
            }

            if (!TryParseRunCommand(command, out string requestedName))
            {
                return false;
            }

            HostFile? matchedFile = FindFile(requestedName);
            Trace($"RUN \"{requestedName}\" match=\"{matchedFile?.Name ?? "<none>"}\"");
            if (!matchedFile.HasValue)
            {
                return false;
            }

            HostFile file = matchedFile.Value;
            ushort runLoadAddress = ToCpuAddress(file.LoadAddress, DefaultBasicLoadAddress);
            for (int i = 0; i < file.Data.Length; i++)
                memory.Memory[(runLoadAddress + i) & 0xFFFF] = file.Data[i];

            cpu.registers.PC = ToCpuAddress(file.ExecutionAddress, DefaultBasicLoadAddress);
            return true;
        }

        /// <summary>FSCV is used by MOS filing-system entry points that bypass OSFILE.</summary>
        public bool TryHandleFscv(CPU_6502 cpu)
        {
            if (files.Length == 0 || (cpu.registers.PC & 0xFFFF) != ReadWord(FscvVector))
                return false;

            if (cpu.registers.A != 0x04)
                return false;

            string requestedName = ReadOsString((ushort)(cpu.registers.X | (cpu.registers.Y << 8))).Trim();
            HostFile? matchedFile = FindFile(requestedName);
            Trace($"FSCV A=${cpu.registers.A:X2} name=\"{requestedName}\" match=\"{matchedFile?.Name ?? "<none>"}\"");
            if (!matchedFile.HasValue)
            {
                return false;
            }

            HostFile file = matchedFile.Value;
            ushort fscvLoadAddress = ToCpuAddress(file.LoadAddress, DefaultBasicLoadAddress);
            for (int i = 0; i < file.Data.Length; i++)
                memory.Memory[(fscvLoadAddress + i) & 0xFFFF] = file.Data[i];

            cpu.registers.PC = ToCpuAddress(file.ExecutionAddress, DefaultBasicLoadAddress);
            return true;
        }

        private static bool IsBareExecCommand(string command)
        {
            const string exec = "EXEC";
            string trimmed = command.Trim();
            return trimmed.Length == exec.Length
                && string.Equals(trimmed, exec, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBreakCommand(string command)
        {
            string trimmed = command.Trim();
            return string.Equals(trimmed, "BREAK", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryHandleExecCommand(string command)
        {
            if (!TryParseExecCommand(command, out string requestedName))
                return false;

            HostFile? matchedFile = FindFile(requestedName);
            Trace($"EXEC \"{requestedName}\" match=\"{matchedFile?.Name ?? "<none>"}\"");
            if (!matchedFile.HasValue)
                return false;

            string text = Encoding.ASCII.GetString(matchedFile.Value.Data).Replace('\0', '\r');
            if (QueueKeyboardScript is not null)
                QueueKeyboardScript(text);
            else
                QueueKeyboardText?.Invoke(text);

            return true;
        }

        private string? GetSoftKeyText(int key)
        {
            if (key < 0 || key >= softKeyStrings.Length)
                return null;

            string text = softKeyStrings[key];
            return text.Length == 0 ? null : text;
        }

        private static bool IsOptCommand(string command)
        {
            string trimmed = command.TrimStart();
            return trimmed.Length >= 3
                && string.Equals(trimmed[..3], "OPT", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3]));
        }

        private static bool IsTapeCommand(string command)
        {
            string trimmed = command.Trim();
            return trimmed.Length == 4
                && string.Equals(trimmed, "TAPE", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryHandleMouseCommand(string command)
        {
            string trimmed = command.TrimStart();
            if (trimmed.Length < 5
                || !string.Equals(trimmed[..5], "MOUSE", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > 5 && !char.IsWhiteSpace(trimmed[5])))
            {
                return false;
            }

            string option = trimmed.Length == 5 ? "ON" : trimmed[5..].TrimStart();
            bool enabled = !option.StartsWith("OFF", StringComparison.OrdinalIgnoreCase);
            MouseEnabledChanged?.Invoke(enabled);
            Trace($"MOUSE enabled={enabled}");
            return MouseCommandFallbackEnabled;
        }

        private bool TryHandlePointerCommand(string command)
        {
            string trimmed = command.TrimStart();
            if (trimmed.Length < 7
                || !string.Equals(trimmed[..7], "POINTER", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > 7 && !char.IsWhiteSpace(trimmed[7])))
            {
                return false;
            }

            string option = trimmed.Length == 7 ? "ON" : trimmed[7..].TrimStart();
            bool enabled = !option.StartsWith("OFF", StringComparison.OrdinalIgnoreCase);
            MouseEnabledChanged?.Invoke(enabled);
            Trace($"POINTER mouse enabled={enabled}");
            return MouseCommandFallbackEnabled;
        }

        private static bool IsTvCommand(string command)
        {
            string trimmed = command.TrimStart();
            if (trimmed.StartsWith("TV", StringComparison.OrdinalIgnoreCase))
                return trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2]) || char.IsDigit(trimmed[2]) || trimmed[2] == ',';

            return trimmed.StartsWith("T.", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryHandleFxCommand(string command, CPU_6502 cpu, out bool returnFromOscli)
        {
            returnFromOscli = true;
            string trimmed = command.TrimStart();
            if (trimmed.Length < 2 || !string.Equals(trimmed[..2], "FX", StringComparison.OrdinalIgnoreCase))
                return false;

            string arguments = trimmed.Length == 2 ? string.Empty : trimmed[2..].Trim();
            if (!TryParseFxArguments(arguments, out int a, out int x, out int y))
                return false;

            if ((a & 0xFF) == 0x10)
                return true;

            if ((a & 0xFF) == 0x8A)
            {
                InsertSoftKey((byte)y);
                return true;
            }

            cpu.registers.A = (byte)a;
            cpu.registers.X = (byte)x;
            cpu.registers.Y = (byte)y;
            cpu.registers.PC = OsbyteEntry;
            returnFromOscli = false;
            return true;
        }

        private bool TryHandleKeyCommand(string command)
        {
            string trimmed = command.TrimStart();
            int indexStart;
            if (trimmed.StartsWith("KEY", StringComparison.OrdinalIgnoreCase))
            {
                indexStart = 3;
            }
            else if (trimmed.Length >= 2
                && char.ToUpperInvariant(trimmed[0]) == 'K'
                && trimmed[1] == '.')
            {
                indexStart = 2;
            }
            else
            {
                return false;
            }

            while (indexStart < trimmed.Length && char.IsWhiteSpace(trimmed[indexStart]))
                indexStart++;

            int indexEnd = indexStart;
            while (indexEnd < trimmed.Length && char.IsDigit(trimmed[indexEnd]))
                indexEnd++;

            if (indexEnd == indexStart
                || !int.TryParse(trimmed[indexStart..indexEnd], out int key)
                || key < 0
                || key >= softKeyStrings.Length)
                return false;

            string value = indexEnd < trimmed.Length ? trimmed[indexEnd..].TrimStart() : string.Empty;
            softKeyStrings[key] = DecodeSoftKeyString(value);
            return true;
        }

        private void InsertSoftKey(byte keyCode)
        {
            int key = keyCode >= 0x80 ? keyCode - 0x80 : keyCode;
            if (key < 0 || key >= softKeyStrings.Length || QueueKeyboardText is null)
                return;

            QueueKeyboardText(softKeyStrings[key]);
        }

        private static string DecodeSoftKeyString(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '|' || i + 1 >= value.Length)
                {
                    builder.Append(value[i]);
                    continue;
                }

                char code = char.ToUpperInvariant(value[++i]);
                builder.Append(code switch
                {
                    'M' => '\r',
                    'A' => '\n',
                    '[' => (char)27,
                    '|' => '|',
                    _ => code
                });
            }

            return builder.ToString();
        }

        private static bool TryParseFxArguments(string text, out int a, out int x, out int y)
        {
            a = 0;
            x = 0;
            y = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 3)
                return false;

            if (!TryParseFxNumber(parts[0], out a))
                return false;

            if (parts.Length > 1 && parts[1].Length > 0 && !TryParseFxNumber(parts[1], out x))
                return false;

            if (parts.Length > 2 && parts[2].Length > 0 && !TryParseFxNumber(parts[2], out y))
                return false;

            return true;
        }

        private static bool TryParseFxNumber(string text, out int value)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                value = 0;
                return false;
            }

            if (trimmed.StartsWith('&'))
                return int.TryParse(trimmed[1..], System.Globalization.NumberStyles.HexNumber, null, out value);

            return int.TryParse(trimmed, out value);
        }

        private HostFile? FindFile(string requestedName)
        {
            string cleanedName = CleanDfsName(requestedName);
            string normalized = CanonicalDfsName(cleanedName);
            string leafNormalized = NormalizeName(GetLeafName(cleanedName));
            bool hasExplicitDirectory = HasExplicitDirectory(cleanedName);

            if (!hasExplicitDirectory && currentDirectory != "$")
            {
                string qualified = CanonicalDfsName($"{currentDirectory}.{cleanedName}");
                foreach (HostFile file in files)
                {
                    if (CanonicalDfsName(file.Name) == qualified)
                        return file;
                }
            }

            foreach (HostFile file in files)
            {
                if (CanonicalDfsName(file.Name) == normalized)
                    return file;
            }

            string fallbackNormalized = NormalizeName(cleanedName);
            foreach (HostFile file in files)
            {
                string fileNormalized = NormalizeName(file.Name);
                string fileLeafNormalized = NormalizeName(GetLeafName(file.Name));
                if (fileNormalized == fallbackNormalized
                    || fileLeafNormalized == fallbackNormalized
                    || fileNormalized == leafNormalized
                    || fileLeafNormalized == leafNormalized)
                {
                    return file;
                }
            }

            return files.Length == 1 ? files[0] : null;
        }

        private bool TryHandleDirCommand(string command)
        {
            if (!TryMatchCommandName(command, "DIR", out string rest))
                return false;

            string directory = rest.Trim().Trim('"');
            currentDirectory = string.IsNullOrEmpty(directory) || directory == "$"
                ? "$"
                : char.ToUpperInvariant(directory[0]).ToString();
            Trace($"DIR current=\"{currentDirectory}\"");
            return true;
        }

        private static bool HasExplicitDirectory(string name)
        {
            string cleanedName = CleanDfsName(name);
            int dot = cleanedName.IndexOf('.');
            return dot > 0 && dot < cleanedName.Length - 1;
        }

        private static string CanonicalDfsName(string name)
        {
            return CleanDfsName(name).ToUpperInvariant();
        }

        private static string CleanDfsName(string name)
        {
            string trimmed = name.Trim().Trim('"');
            if (trimmed.StartsWith('/'))
                trimmed = trimmed[1..].TrimStart();

            return trimmed;
        }

        private static void Trace(string message)
        {
            if (TraceEnabled)
                Console.WriteLine($"HOSTFS {message}");
        }

        private static string GetLeafName(string name)
        {
            string trimmed = name.Trim().Trim('"');
            if (trimmed.StartsWith('/'))
                trimmed = trimmed[1..].TrimStart();

            int dot = trimmed.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < trimmed.Length)
                return trimmed[(dot + 1)..];

            int colon = trimmed.LastIndexOf(':');
            if (colon >= 0 && colon + 1 < trimmed.Length)
                return trimmed[(colon + 1)..];

            return trimmed;
        }

        private static bool TryParseRunCommand(string command, out string fileName)
        {
            fileName = string.Empty;
            const string run = "RUN";

            if (!TryMatchCommandName(command, run, out string rest))
                return false;

            rest = rest.TrimStart();
            if (rest.Length == 0)
                return false;

            return TryReadCommandFileName(rest, out fileName, out _);
        }

        private static bool TryParseExecCommand(string command, out string fileName)
        {
            fileName = string.Empty;
            const string exec = "EXEC";

            if (!TryMatchCommandName(command, exec, out string rest))
                return false;

            rest = rest.TrimStart();
            if (rest.Length == 0)
                return false;

            return TryReadCommandFileName(rest, out fileName, out _);
        }

        private static bool TryParseLoadCommand(string command, out string fileName, out ushort? loadAddress)
        {
            fileName = string.Empty;
            loadAddress = null;

            if (!TryMatchCommandName(command, "LOAD", out string rest))
                return false;

            rest = rest.TrimStart();
            if (!TryReadCommandFileName(rest, out fileName, out int nextIndex))
                return false;

            string arguments = rest[nextIndex..].TrimStart();
            if (arguments.Length == 0)
                return true;

            int end = 0;
            while (end < arguments.Length && !char.IsWhiteSpace(arguments[end]))
                end++;

            if (!TryParseDfsAddress(arguments[..end], out ushort parsedAddress))
                return false;

            loadAddress = parsedAddress;
            return true;
        }

        private static bool TryMatchCommandName(string command, string name, out string rest)
        {
            rest = string.Empty;
            string trimmed = command.TrimStart();

            if (trimmed.Length >= name.Length && string.Equals(trimmed[..name.Length], name, StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Length == name.Length || char.IsWhiteSpace(trimmed[name.Length]) || trimmed[name.Length] == '"')
                {
                    rest = trimmed[name.Length..];
                    return true;
                }
            }

            if (trimmed.Length >= 2
                && char.ToUpperInvariant(trimmed[0]) == char.ToUpperInvariant(name[0])
                && trimmed[1] == '.')
            {
                rest = trimmed[2..];
                return true;
            }

            return false;
        }

        private static bool TryReadCommandFileName(string rest, out string fileName, out int nextIndex)
        {
            fileName = string.Empty;
            nextIndex = 0;

            if (rest[0] == '"')
            {
                int endQuote = rest.IndexOf('"', 1);
                if (endQuote < 0)
                    return false;

                fileName = rest[1..endQuote];
                nextIndex = endQuote + 1;
                return fileName.Length > 0;
            }

            int separator = rest.IndexOfAny([' ', '\t', '\r']);
            fileName = separator < 0 ? rest : rest[..separator];
            nextIndex = separator < 0 ? rest.Length : separator;
            return fileName.Length > 0;
        }

        private static bool TryParseDfsAddress(string text, out ushort address)
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith('&'))
                trimmed = trimmed[1..];

            return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        private void WriteCatalogueInfo(ushort controlBlock, HostFile file)
        {
            WriteDword(controlBlock + 2, file.LoadAddress);
            WriteDword(controlBlock + 6, file.ExecutionAddress);
            WriteDword(controlBlock + 10, (uint)file.Data.Length);
            WriteDword(controlBlock + 14, 0);
        }

        private void ReturnFromSubroutine(CPU_6502 cpu)
        {
            byte lo = memory.Memory[0x0100 + ((cpu.registers.S + 1) & 0xFF)];
            byte hi = memory.Memory[0x0100 + ((cpu.registers.S + 2) & 0xFF)];
            cpu.registers.S += 2;
            cpu.registers.PC = (ushort)(((hi << 8) | lo) + 1);
        }

        private string ReadOsString(uint address)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < 255; i++)
            {
                byte value = memory.Memory[(address + i) & 0xFFFF];
                if (value == 0x0D)
                    break;

                builder.Append((char)value);
            }

            return builder.ToString();
        }

        private ushort ReadWord(int address)
        {
            return (ushort)(memory.Memory[address & 0xFFFF] | (memory.Memory[(address + 1) & 0xFFFF] << 8));
        }

        private uint ReadDword(int address)
        {
            return (uint)(ReadWord(address) | (ReadWord(address + 2) << 16));
        }

        private void WriteDword(int address, uint value)
        {
            memory.Memory[address & 0xFFFF] = (byte)value;
            memory.Memory[(address + 1) & 0xFFFF] = (byte)(value >> 8);
            memory.Memory[(address + 2) & 0xFFFF] = (byte)(value >> 16);
            memory.Memory[(address + 3) & 0xFFFF] = (byte)(value >> 24);
        }

        private static HostFile ReadRawHostFile(string path, byte[] data)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            HostFileMetadata metadata = ReadInfMetadata(path);
            return new HostFile(NormalizeDiscName(name), metadata.LoadAddress, metadata.ExecutionAddress, data);
        }

        private static ushort ToCpuAddress(uint address, ushort fallback)
        {
            return address == 0 ? fallback : (ushort)(address & 0xFFFF);
        }

        private static HostFileMetadata ReadInfMetadata(string path)
        {
            string infPath = path + ".inf";
            if (!File.Exists(infPath))
                infPath = Path.ChangeExtension(path, ".inf");

            if (!File.Exists(infPath))
                return new HostFileMetadata(DefaultBasicLoadAddress, DefaultBasicLoadAddress);

            string[] parts = File.ReadAllText(infPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return new HostFileMetadata(DefaultBasicLoadAddress, DefaultBasicLoadAddress);

            return new HostFileMetadata(ParseHex(parts[1], DefaultBasicLoadAddress), ParseHex(parts[2], DefaultBasicLoadAddress));
        }

        private static ushort ParseHex(string text, ushort fallback)
        {
            return ushort.TryParse(text.TrimStart('&'), System.Globalization.NumberStyles.HexNumber, null, out ushort value)
                ? value
                : fallback;
        }

        private static string NormalizeName(string name)
        {
            return NormalizeDiscName(name).Replace(".", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        private static string NormalizeDiscName(string name)
        {
            string safe = new string(name.Where(ch => ch is >= '!' and <= '~' && ch != '"').ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "HOST" : safe.Length > 7 ? safe[..7] : safe;
        }

        private readonly record struct HostFile(string Name, uint LoadAddress, uint ExecutionAddress, byte[] Data);
        private readonly record struct HostFileMetadata(uint LoadAddress, uint ExecutionAddress);
    }
}
