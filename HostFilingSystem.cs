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
        private const ushort DefaultBasicLoadAddress = 0x1900;
        private readonly FlatMemoryBus memory;
        private readonly bool traceOscli = Environment.GetEnvironmentVariable("BBC_OSCLI_TRACE") == "1";
        private readonly string oscliTracePath = Path.Combine(Environment.CurrentDirectory, "bbc-oscli-trace.log");
        private HostFile[] files = [];
        private string? mountedPath;

        /// <summary>Initializes a host filing system shim.</summary>
        /// <param name="memory">The CPU-visible memory bus.</param>
        public HostFilingSystem(FlatMemoryBus memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        /// <summary>Gets the currently mounted host path, if any.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Gets whether a host file or image is mounted.</summary>
        public bool HasMountedFile => files.Length > 0;

        /// <summary>Gets the command that should be typed at BASIC after mounting.</summary>
        public string? AutoLoadCommand => files.Length == 0 ? null : $"LOAD \"{files[0].Name}\"";

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
            files = IsSsdImage(fullPath, data)
                ? ReadSsdFiles(data)
                : [ReadRawHostFile(fullPath, data)];

            if (files.Length == 0)
                throw new InvalidOperationException($"No loadable files found in '{fullPath}'.");

            mountedPath = fullPath;
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
            WriteCatalogueInfo(controlBlock, file);

            if (action == 0xFF)
            {
                ushort targetAddress = memory.Memory[(controlBlock + 6) & 0xFFFF] == 0
                    ? (ushort)ReadDword(controlBlock + 2)
                    : file.LoadAddress;

                for (int i = 0; i < file.Data.Length; i++)
                    memory.Memory[(targetAddress + i) & 0xFFFF] = file.Data[i];
            }

            cpu.registers.A = 1;
            ReturnFromSubroutine(cpu);
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

            if (command.Length == 0)
            {
                TraceOscli(command, "handled empty command");
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (IsBareExecCommand(command))
            {
                TraceOscli(command, "handled bare EXEC");
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (!TryParseRunCommand(command, out string requestedName))
            {
                TraceOscli(command, "passed through");
                return false;
            }

            HostFile? matchedFile = FindFile(requestedName);
            if (!matchedFile.HasValue)
            {
                TraceOscli(command, $"RUN target not found: {requestedName}");
                return false;
            }

            HostFile file = matchedFile.Value;
            for (int i = 0; i < file.Data.Length; i++)
                memory.Memory[(file.LoadAddress + i) & 0xFFFF] = file.Data[i];

            TraceOscli(command, $"handled RUN {file.Name} load=${file.LoadAddress:X4} exec=${file.ExecutionAddress:X4} length=${file.Data.Length:X4}");
            cpu.registers.PC = file.ExecutionAddress;
            return true;
        }

        private void TraceOscli(string command, string outcome)
        {
            if (!traceOscli)
                return;

            File.AppendAllText(oscliTracePath, $"{DateTimeOffset.Now:O} OSCLI \"{command}\" -> {outcome}{Environment.NewLine}");
        }

        private static bool IsBareExecCommand(string command)
        {
            const string exec = "EXEC";
            string trimmed = command.Trim();
            return trimmed.Length == exec.Length
                && string.Equals(trimmed, exec, StringComparison.OrdinalIgnoreCase);
        }

        private HostFile? FindFile(string requestedName)
        {
            string normalized = NormalizeName(requestedName);

            foreach (HostFile file in files)
            {
                if (NormalizeName(file.Name) == normalized)
                    return file;
            }

            return files.Length == 1 ? files[0] : null;
        }

        private static bool TryParseRunCommand(string command, out string fileName)
        {
            fileName = string.Empty;
            const string run = "RUN";

            if (!command.StartsWith(run, StringComparison.OrdinalIgnoreCase))
                return false;

            string rest = command[run.Length..].TrimStart();
            if (rest.Length == 0)
                return false;

            if (rest[0] == '"')
            {
                int endQuote = rest.IndexOf('"', 1);
                if (endQuote < 0)
                    return false;

                fileName = rest[1..endQuote];
                return fileName.Length > 0;
            }

            int separator = rest.IndexOfAny([' ', '\t', '\r']);
            fileName = separator < 0 ? rest : rest[..separator];
            return fileName.Length > 0;
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
