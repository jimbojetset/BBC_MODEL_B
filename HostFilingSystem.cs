// ============================================================================
// Project:     BBC
// File:        HostFilingSystem.cs
// Description: Host-backed filing system shim for MOS OSFILE loads.
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
    /// Provides a lightweight host-backed filing system by intercepting MOS OSFILE.
    /// </summary>
    public sealed class HostFilingSystem
    {
        private const ushort OsfileEntry = 0xFFDD;
        private const ushort OscliEntry = 0xFFF7;
        private const ushort OsbyteEntry = 0xFFF4;
        private const ushort FscvVector = 0x021E;
        private const ushort DefaultBasicLoadAddress = 0x1900;
        private readonly FlatMemoryBus memory;
        private HostFile[] files = [];
        private string? mountedPath;

        private string? mountedFileName;
        private bool mountedDiscImage;

        /// <summary>Initializes a host filing system shim.</summary>
        /// <param name="memory">The CPU-visible memory bus.</param>
        public HostFilingSystem(FlatMemoryBus memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        /// <summary>Gets the currently mounted host path, if any.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Gets the currently mounted host path, if any.</summary>
        public string? MountedFileName => mountedFileName;

        /// <summary>Gets whether a host file or image is mounted.</summary>
        public bool HasMountedFile => files.Length > 0;

        /// <summary>Gets or sets whether host-backed *RUN/FSCV execution shortcuts are enabled.</summary>
        public bool RunCommandInterceptionEnabled { get; set; } = true;

        /// <summary>Called after a host-backed disc-image load is copied into memory.</summary>
        public Action? DiscImageLoadActivity { get; set; }

        /// <summary>Queues text into the emulated keyboard buffer for soft-key expansion.</summary>
        public Action<string>? QueueKeyboardText { get; set; }

        /// <summary>Gets the command that should be typed at BASIC after mounting.</summary>
        public string? AutoLoadCommand => files.Length == 0 ? null : $"LOAD \"{files[0].Name}\"";

        private readonly string[] softKeyStrings = new string[16];

        /// <summary>Clears any mounted host-backed files.</summary>
        public void Unmount()
        {
            files = [];
            mountedPath = null;
            mountedFileName = null;
            mountedDiscImage = false;
            RunCommandInterceptionEnabled = true;
        }

        /// <summary>Mounts a host file or SSD disc image.</summary>
        /// <param name="path">The host path.</param>
        public void Mount(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A disc/file path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Disc/file not found: {fullPath}", fullPath);

            byte[] data = File.ReadAllBytes(fullPath);
            mountedDiscImage = IsSsdImage(fullPath, data);
            files = mountedDiscImage
                ? ReadSsdFiles(data)
                : [ReadRawHostFile(fullPath, data)];

            if (files.Length == 0)
                throw new InvalidOperationException($"No loadable files found in '{fullPath}'.");

            mountedPath = fullPath;
            mountedFileName = Path.GetFileName(fullPath);
            RunCommandInterceptionEnabled = true;
        }

        /// <summary>Handles an OSFILE call if the CPU is currently at the OSFILE entry.</summary>
        /// <param name="cpu">The CPU.</param>
        /// <returns>True when the call was handled by the host filing system.</returns>
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
                    : file.LoadAddress;

                for (int i = 0; i < file.Data.Length; i++)
                    memory.Memory[(targetAddress + i) & 0xFFFF] = file.Data[i];
            }

            WriteCatalogueInfo(controlBlock, file);
            cpu.registers.A = 1;
            ReturnFromSubroutine(cpu);
            if (action == 0xFF)
                NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Handles a host-backed *RUN command if the CPU is currently at the OSCLI entry.</summary>
        /// <param name="cpu">The CPU.</param>
        /// <returns>True when the command was handled by the host filing system.</returns>
        public bool TryHandleOscli(CPU_6502 cpu)
        {
            if ((cpu.registers.PC & 0xFFFF) != OscliEntry || files.Length == 0)
                return false;

            ushort commandAddress = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));
            string command = ReadOsString(commandAddress).Trim();
            if (command.StartsWith('*'))
                command = command[1..].TrimStart();

            // BBC shorthand: '*/FILE' is equivalent to '*RUN FILE'. Rewrite once
            // so the rest of the dispatcher (TryParseRunCommand etc.) just works.
            if (command.StartsWith('/'))
                command = "RUN " + command[1..].TrimStart();

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

            if (IsTvCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsOptCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsTapeCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (!RunCommandInterceptionEnabled)
            {
                return false;
            }

            if (TryParseLoadCommand(command, out string loadName, out ushort? loadAddress))
            {
                HostFile? matchedLoadFile = FindFile(loadName);
                if (!matchedLoadFile.HasValue)
                {
                    return false;
                }

                HostFile loadFile = matchedLoadFile.Value;
                ushort targetAddress = loadAddress ?? loadFile.LoadAddress;
                for (int i = 0; i < loadFile.Data.Length; i++)
                    memory.Memory[(targetAddress + i) & 0xFFFF] = loadFile.Data[i];

                ReturnFromSubroutine(cpu);
                NotifyDiscImageLoadActivity();
                return true;
            }

            if (!TryParseRunCommand(command, out string requestedName))
            {
                return false;
            }

            HostFile? matchedFile = FindFile(requestedName);
            if (!matchedFile.HasValue)
            {
                return false;
            }

            HostFile file = matchedFile.Value;
            for (int i = 0; i < file.Data.Length; i++)
                memory.Memory[(file.LoadAddress + i) & 0xFFFF] = file.Data[i];

            cpu.registers.PC = file.ExecutionAddress;
            NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Handles host-backed filing system control vector calls.</summary>
        /// <param name="cpu">The CPU.</param>
        /// <returns>True when the FSCV call was handled by the host filing system.</returns>
        public bool TryHandleFscv(CPU_6502 cpu)
        {
            if (files.Length == 0 || !RunCommandInterceptionEnabled || (cpu.registers.PC & 0xFFFF) != ReadWord(FscvVector))
                return false;

            if (cpu.registers.A != 0x04)
                return false;

            string requestedName = ReadOsString((ushort)(cpu.registers.X | (cpu.registers.Y << 8))).Trim();
            HostFile? matchedFile = FindFile(requestedName);
            if (!matchedFile.HasValue)
            {
                return false;
            }

            HostFile file = matchedFile.Value;
            for (int i = 0; i < file.Data.Length; i++)
                memory.Memory[(file.LoadAddress + i) & 0xFFFF] = file.Data[i];

            cpu.registers.PC = file.ExecutionAddress;
            NotifyDiscImageLoadActivity();
            return true;
        }

        private static bool IsBareExecCommand(string command)
        {
            const string exec = "EXEC";
            string trimmed = command.Trim();
            return trimmed.Length == exec.Length
                && string.Equals(trimmed, exec, StringComparison.OrdinalIgnoreCase);
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

            // OSBYTE 16 selects how many analogue (ADC) channels the MOS samples in the
            // background. The µPD7002 ADC is not emulated (ADVAL is serviced directly via
            // the OSBYTE &80 intercept), so MOS's ADC sampling must never be started.
            // Letting *FX 16 reach the real MOS drives the absent ADC and breaks games
            // such as Frogger (it drops out to BASIC). Swallow it as a no-op.
            if ((a & 0xFF) == 0x10)
                return true;

            if ((a & 0xFF) == 0x8A)
            {
                // Soft-key insertion is emulated at the host level so queued text
                // reaches the emulated keyboard buffer.
                InsertSoftKey((byte)y);
                return true;
            }

            // Parsed *FX commands are direct OSBYTE calls. Transfer to OSBYTE with the
            // original OSCLI return address still on the stack, so compact forms such
            // as *FX9,5 get real MOS side effects without relying on MOS OSCLI parsing.
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
            string normalized = NormalizeDiscName(requestedName).ToUpperInvariant();

            foreach (HostFile file in files)
            {
                if (NormalizeDiscName(file.Name).ToUpperInvariant() == normalized)
                    return file;
            }

            string fallbackNormalized = NormalizeName(requestedName);
            foreach (HostFile file in files)
            {
                if (NormalizeName(file.Name) == fallbackNormalized || NormalizeName(GetLeafName(file.Name)) == fallbackNormalized)
                    return file;
            }

            return files.Length == 1 ? files[0] : null;
        }

        private void NotifyDiscImageLoadActivity()
        {
            if (mountedDiscImage)
                DiscImageLoadActivity?.Invoke();
        }

        private static string GetLeafName(string name)
        {
            int dot = name.IndexOf('.');
            return dot < 0 ? name : name[(dot + 1)..];
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

        private static bool IsSsdImage(string path, byte[] data)
        {
            string extension = Path.GetExtension(path);
            return data.Length >= 512
                && data.Length % 256 == 0
                && (string.Equals(extension, ".ssd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".dsd", StringComparison.OrdinalIgnoreCase));
        }

        private static HostFile ReadRawHostFile(string path, byte[] data)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            HostFileMetadata metadata = ReadInfMetadata(path);
            return new HostFile(NormalizeDiscName(name), metadata.LoadAddress, metadata.ExecutionAddress, data);
        }

        private static HostFile[] ReadSsdFiles(byte[] image)
        {
            int fileCount = image[0x105] / 8;
            List<HostFile> result = new List<HostFile>();

            for (int i = 0; i < fileCount && i < 31; i++)
            {
                int nameOffset = 8 + (i * 8);
                int infoOffset = 0x108 + (i * 8);
                string name = ReadDfsName(image, nameOffset);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                int packed = image[infoOffset + 6];
                uint load = (uint)(image[infoOffset] | (image[infoOffset + 1] << 8) | ((packed & 0x0C) << 14));
                uint exec = (uint)(image[infoOffset + 2] | (image[infoOffset + 3] << 8) | ((packed & 0xC0) << 10));
                int length = image[infoOffset + 4] | (image[infoOffset + 5] << 8) | ((packed & 0x30) << 12);
                int startSector = image[infoOffset + 7] | ((packed & 0x03) << 8);
                int start = startSector * 256;

                if (length < 0 || start < 0 || start + length > image.Length)
                    continue;

                byte[] data = new byte[length];
                Array.Copy(image, start, data, 0, length);
                result.Add(new HostFile(name, ToCpuAddress(load, DefaultBasicLoadAddress), ToCpuAddress(exec, DefaultBasicLoadAddress), data));
            }

            return result.ToArray();
        }

        private static string ReadDfsName(byte[] image, int offset)
        {
            string leaf = Encoding.ASCII.GetString(image, offset, 7).Trim();
            char directory = (char)(image[offset + 7] & 0x7F);
            return directory is >= '!' and <= '~' && directory != '$'
                ? $"{directory}.{leaf}"
                : leaf;
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

        private readonly record struct HostFile(string Name, ushort LoadAddress, ushort ExecutionAddress, byte[] Data);
        private readonly record struct HostFileMetadata(ushort LoadAddress, ushort ExecutionAddress);
    }
}
