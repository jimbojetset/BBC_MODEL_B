// ============================================================================
// Project:     BBC
// File:        HostFilingSystem.cs
// Description: Host-backed filing system shim for MOS OSFILE, OSWORD, OSCLI,
//              and FSCV file/disc loading paths.
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
        private const ushort OswordEntry = 0xFFF1;
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 10;
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_OSCLI_TRACE") == "1";
        private readonly FlatMemoryBus memory;
        private HostFile[] files = [];
        private byte[] mountedImage = [];
        private string? mountedPath;
        private string currentDirectory = "$";

        private string? mountedFileName;
        private bool mountedDiscImage;

        /// <summary>Initializes a host filing system shim.</summary>
        /// <param name="memory">The CPU-visible memory bus.</param>
        public HostFilingSystem(FlatMemoryBus memory)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        /// <summary>Gets the currently mounted host path.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Gets the currently mounted host filename.</summary>
        public string? MountedFileName => mountedFileName;

        /// <summary>Gets whether a host file or image is mounted.</summary>
        public bool HasMountedFile => files.Length > 0;

        /// <summary>Gets or sets whether host RUN/LOAD command interception is enabled.</summary>
        public bool RunCommandInterceptionEnabled { get; set; } = true;

        /// <summary>Called after a host-backed disc-image load is copied into memory.</summary>
        public Action? DiscImageLoadActivity { get; set; }

        /// <summary>Called when an emulated mouse ROM command enables or disables mouse input.</summary>
        public Action<bool>? MouseEnabledChanged { get; set; }

        /// <summary>Queues text into the emulated keyboard buffer for soft-key expansion.</summary>
        public Action<string>? QueueKeyboardText { get; set; }

        /// <summary>Gets the BASIC load command for the first mounted host file.</summary>
        public string? AutoLoadCommand => files.Length == 0 ? null : $"LOAD \"{files[0].Name}\"";

        private readonly string[] softKeyStrings = new string[16];

        /// <summary>Clears any mounted host-backed files.</summary>
        public void Unmount()
        {
            files = [];
            mountedImage = [];
            mountedPath = null;
            mountedFileName = null;
            mountedDiscImage = false;
            currentDirectory = "$";
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
            mountedImage = mountedDiscImage ? data : [];
            files = mountedDiscImage
                ? ReadSsdFiles(data)
                : [ReadRawHostFile(fullPath, data)];

            if (files.Length == 0)
                throw new InvalidOperationException($"No loadable files found in '{fullPath}'.");

            mountedPath = fullPath;
            mountedFileName = Path.GetFileName(fullPath);
            currentDirectory = "$";
            RunCommandInterceptionEnabled = true;
        }

        /// <summary>Handles OSFILE when the current emulator state matches the expected firmware entry.</summary>
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
            if (action == 0xFF)
                NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Attempts to handle osword.</summary>
        /// <param name="cpu">The CPU.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
        public bool TryHandleOsword(CPU_6502 cpu)
        {
            if ((cpu.registers.PC & 0xFFFF) != OswordEntry || cpu.registers.A != 0x7F || !mountedDiscImage || mountedImage.Length == 0)
                return false;

            ushort controlBlock = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));
            byte parameterCount = memory.Memory[(controlBlock + 5) & 0xFFFF];
            if (parameterCount < 3)
                return false;

            byte command = memory.Memory[(controlBlock + 6) & 0xFFFF];
            byte opcode = (byte)(command & 0x3F);
            if (opcode is not (0x07 or 0x13 or 0x17))
                return false;

            uint dataAddress = ReadDword(controlBlock + 1);
            int track = memory.Memory[(controlBlock + 7) & 0xFFFF];
            int sector = memory.Memory[(controlBlock + 8) & 0xFFFF];
            byte sectorSizeAndCount = memory.Memory[(controlBlock + 9) & 0xFFFF];
            int sectorSize = GetSectorSize(sectorSizeAndCount);
            int count = GetSectorCount(sectorSizeAndCount);
            ushort targetAddress = (ushort)dataAddress;

            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetSectorOffset(track, sector, out int offset) || offset + sectorSize > mountedImage.Length)
                    return false;

                for (int i = 0; i < sectorSize; i++)
                    memory.Memory[(targetAddress + (sectorIndex * sectorSize) + i) & 0xFFFF] = mountedImage[offset + i];

                AdvanceSector(ref track, ref sector);
            }

            cpu.registers.A = 0;
            ReturnFromSubroutine(cpu);
            NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Handles OSCLI when the current emulator state matches the expected firmware entry.</summary>
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

            Trace($"OSCLI \"{command}\"");

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

            if (TryHandleDirCommand(command))
            {
                ReturnFromSubroutine(cpu);
                return true;
            }

            if (TryHandleMouseCommand(command))
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
                NotifyDiscImageLoadActivity();
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
            NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Intercepts FSCV run/load requests for mounted host files.</summary>
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
            NotifyDiscImageLoadActivity();
            return true;
        }

        /// <summary>Checks whether an OSCLI command is a bare EXEC command.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when bare exec command is true; otherwise, false.</returns>
        private static bool IsBareExecCommand(string command)
        {
            const string exec = "EXEC";
            string trimmed = command.Trim();
            return trimmed.Length == exec.Length
                && string.Equals(trimmed, exec, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Checks whether an OSCLI command is an OPT command handled by the host.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when opt command is true; otherwise, false.</returns>
        private static bool IsOptCommand(string command)
        {
            string trimmed = command.TrimStart();
            return trimmed.Length >= 3
                && string.Equals(trimmed[..3], "OPT", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3]));
        }

        /// <summary>Checks whether an OSCLI command switches back to tape filing.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when tape command is true; otherwise, false.</returns>
        private static bool IsTapeCommand(string command)
        {
            string trimmed = command.Trim();
            return trimmed.Length == 4
                && string.Equals(trimmed, "TAPE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Handles optional mouse ROM control commands.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when the command targeted the mouse ROM.</returns>
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
            return true;
        }

        /// <summary>Checks whether an OSCLI command is a TV display command handled by the host.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when tv command is true; otherwise, false.</returns>
        private static bool IsTvCommand(string command)
        {
            string trimmed = command.TrimStart();
            if (trimmed.StartsWith("TV", StringComparison.OrdinalIgnoreCase))
                return trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2]) || char.IsDigit(trimmed[2]) || trimmed[2] == ',';

            return trimmed.StartsWith("T.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Attempts to handle fx command.</summary>
        /// <param name="command">The command value.</param>
        /// <param name="cpu">The CPU.</param>
        /// <param name="returnFromOscli">The return from oscli value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Attempts to handle key command.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Queues the string assigned to a BBC soft key when the key is pressed.</summary>
        /// <param name="keyCode">The key code value.</param>
        private void InsertSoftKey(byte keyCode)
        {
            int key = keyCode >= 0x80 ? keyCode - 0x80 : keyCode;
            if (key < 0 || key >= softKeyStrings.Length || QueueKeyboardText is null)
                return;

            QueueKeyboardText(softKeyStrings[key]);
        }

        /// <summary>Decodes soft key string.</summary>
        /// <param name="value">The input value.</param>
        /// <returns>The resulting string.</returns>
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

        /// <summary>Parses OSCLI *FX numeric arguments into A, X, and Y values.</summary>
        /// <param name="text">The text.</param>
        /// <param name="a">The a value.</param>
        /// <param name="x">The low result byte value.</param>
        /// <param name="y">The high result byte value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Parses a decimal or ampersand-prefixed hexadecimal *FX argument.</summary>
        /// <param name="text">The text.</param>
        /// <param name="value">The input value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Finds a mounted host file by DFS-normalized name or host leaf name.</summary>
        /// <param name="requestedName">The requested name value.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Handles DFS directory selection commands for host-side file lookups.</summary>
        /// <param name="command">The OSCLI command text.</param>
        /// <returns>True when the command selected a directory.</returns>
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

        /// <summary>Checks whether a DFS name already includes a directory prefix.</summary>
        /// <param name="name">The file name.</param>
        /// <returns>True when the name has an explicit directory.</returns>
        private static bool HasExplicitDirectory(string name)
        {
            string cleanedName = CleanDfsName(name);
            int dot = cleanedName.IndexOf('.');
            return dot > 0 && dot < cleanedName.Length - 1;
        }

        /// <summary>Builds a non-truncating DFS catalogue comparison key.</summary>
        /// <param name="name">The DFS file name.</param>
        /// <returns>The canonical comparison key.</returns>
        private static string CanonicalDfsName(string name)
        {
            return CleanDfsName(name).ToUpperInvariant();
        }

        /// <summary>Removes quoting and shorthand run prefixes from a DFS name.</summary>
        /// <param name="name">The DFS file name.</param>
        /// <returns>The cleaned DFS file name.</returns>
        private static string CleanDfsName(string name)
        {
            string trimmed = name.Trim().Trim('"');
            if (trimmed.StartsWith('/'))
                trimmed = trimmed[1..].TrimStart();

            return trimmed;
        }

        /// <summary>Raises the host disc activity callback for mounted disc-image reads.</summary>
        private void NotifyDiscImageLoadActivity()
        {
            if (mountedDiscImage)
                DiscImageLoadActivity?.Invoke();
        }

        /// <summary>Writes a host filing diagnostic event when tracing is enabled.</summary>
        /// <param name="message">The diagnostic message.</param>
        private static void Trace(string message)
        {
            if (TraceEnabled)
                Console.WriteLine($"HOSTFS {message}");
        }

        /// <summary>Extracts the BBC leaf filename from a host or DFS-style path.</summary>
        /// <param name="name">The name value.</param>
        /// <returns>The normalized name.</returns>
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

        /// <summary>Attempts to parse run command.</summary>
        /// <param name="command">The command value.</param>
        /// <param name="fileName">The file name value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Attempts to parse load command.</summary>
        /// <param name="command">The command value.</param>
        /// <param name="fileName">The file name value.</param>
        /// <param name="loadAddress">The load address.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Attempts to match command name.</summary>
        /// <param name="command">The command value.</param>
        /// <param name="name">The name value.</param>
        /// <param name="rest">The rest value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Attempts to read command file name.</summary>
        /// <param name="rest">The rest value.</param>
        /// <param name="fileName">The file name value.</param>
        /// <param name="nextIndex">The next index value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Attempts to parse dfs address.</summary>
        /// <param name="text">The text.</param>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
        private static bool TryParseDfsAddress(string text, out ushort address)
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith('&'))
                trimmed = trimmed[1..];

            return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        /// <summary>Writes OSFILE catalogue metadata for a mounted host file into BBC memory.</summary>
        /// <param name="controlBlock">The control block value.</param>
        /// <param name="file">The file value.</param>
        private void WriteCatalogueInfo(ushort controlBlock, HostFile file)
        {
            WriteDword(controlBlock + 2, file.LoadAddress);
            WriteDword(controlBlock + 6, file.ExecutionAddress);
            WriteDword(controlBlock + 10, (uint)file.Data.Length);
            WriteDword(controlBlock + 14, 0);
        }

        /// <summary>Returns the CPU from an intercepted MOS subroutine call.</summary>
        /// <param name="cpu">The CPU.</param>
        private void ReturnFromSubroutine(CPU_6502 cpu)
        {
            byte lo = memory.Memory[0x0100 + ((cpu.registers.S + 1) & 0xFF)];
            byte hi = memory.Memory[0x0100 + ((cpu.registers.S + 2) & 0xFF)];
            cpu.registers.S += 2;
            cpu.registers.PC = (ushort)(((hi << 8) | lo) + 1);
        }

        /// <summary>Reads OS string from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The string read from emulated memory or host data.</returns>
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

        /// <summary>Reads a little-endian 16-bit value from BBC memory.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private ushort ReadWord(int address)
        {
            return (ushort)(memory.Memory[address & 0xFFFF] | (memory.Memory[(address + 1) & 0xFFFF] << 8));
        }

        /// <summary>Reads a little-endian 32-bit value from BBC memory.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
        private uint ReadDword(int address)
        {
            return (uint)(ReadWord(address) | (ReadWord(address + 2) << 16));
        }

        /// <summary>Writes a little-endian 32-bit value into BBC memory.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The input value.</param>
        private void WriteDword(int address, uint value)
        {
            memory.Memory[address & 0xFFFF] = (byte)value;
            memory.Memory[(address + 1) & 0xFFFF] = (byte)(value >> 8);
            memory.Memory[(address + 2) & 0xFFFF] = (byte)(value >> 16);
            memory.Memory[(address + 3) & 0xFFFF] = (byte)(value >> 24);
        }

        /// <summary>Checks whether host bytes and extension represent a sector-aligned DFS disc image.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="data">The data byte or buffer.</param>
        /// <returns>True when ssd image is true; otherwise, false.</returns>
        private static bool IsSsdImage(string path, byte[] data)
        {
            string extension = Path.GetExtension(path);
            return data.Length >= 512
                && data.Length % 256 == 0
                && (string.Equals(extension, ".ssd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".dsd", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Attempts to get sector offset.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="offset">The buffer or image offset.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
        private bool TryGetSectorOffset(int track, int sector, out int offset)
        {
            if (track < 0 || sector < 0 || sector >= SectorsPerTrack)
            {
                offset = 0;
                return false;
            }

            int logicalSector = (track * SectorsPerTrack) + sector;
            offset = logicalSector * SectorSize;
            return offset >= 0 && offset < mountedImage.Length;
        }

        /// <summary>Advances a DFS track/sector pair to the next sector, wrapping at the end of a track.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        private static void AdvanceSector(ref int track, ref int sector)
        {
            sector++;
            if (sector < SectorsPerTrack)
                return;

            sector = 0;
            track++;
        }

        /// <summary>Decodes the byte size represented by a DFS sector-size code.</summary>
        /// <param name="sectorSizeAndCount">The sector size and count value.</param>
        /// <returns>The computed value.</returns>
        private static int GetSectorSize(byte sectorSizeAndCount)
        {
            int sizeCode = sectorSizeAndCount >> 5;
            return sizeCode switch
            {
                0 => 128,
                1 => 256,
                2 => 512,
                _ => 1024
            };
        }

        /// <summary>Decodes the sector-count field from a DFS size/count byte.</summary>
        /// <param name="sectorSizeAndCount">The sector size and count value.</param>
        /// <returns>The computed value.</returns>
        private static int GetSectorCount(byte sectorSizeAndCount)
        {
            int count = sectorSizeAndCount & 0x1F;
            return count == 0 ? SectorsPerTrack : count;
        }

        /// <summary>Wraps a host file and optional .inf metadata as a BBC catalogue entry.</summary>
        /// <param name="path">The host file path.</param>
        /// <param name="data">The data byte or buffer.</param>
        /// <returns>The resulting value.</returns>
        private static HostFile ReadRawHostFile(string path, byte[] data)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            HostFileMetadata metadata = ReadInfMetadata(path);
            return new HostFile(NormalizeDiscName(name), metadata.LoadAddress, metadata.ExecutionAddress, data);
        }

        /// <summary>Reads visible DFS catalogue entries and file contents from an SSD image.</summary>
        /// <param name="image">The disc image data.</param>
        /// <returns>The resulting collection.</returns>
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
                result.Add(new HostFile(name, load, exec, data));
            }

            return result.ToArray();
        }

        /// <summary>Reads and normalizes a DFS catalogue filename from raw image bytes.</summary>
        /// <param name="image">The disc image data.</param>
        /// <param name="offset">The buffer or image offset.</param>
        /// <returns>The normalized name.</returns>
        private static string ReadDfsName(byte[] image, int offset)
        {
            string leaf = Encoding.ASCII.GetString(image, offset, 7).Trim();
            char directory = (char)(image[offset + 7] & 0x7F);
            return directory is >= '!' and <= '~' && directory != '$'
                ? $"{directory}.{leaf}"
                : leaf;
        }

        /// <summary>Converts a host metadata address into a 16-bit BBC CPU address.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="fallback">The fallback value.</param>
        /// <returns>The resulting value.</returns>
        private static ushort ToCpuAddress(uint address, ushort fallback)
        {
            return address == 0 ? fallback : (ushort)(address & 0xFFFF);
        }

        /// <summary>Reads optional .inf load and execution addresses for a host file.</summary>
        /// <param name="path">The host file path.</param>
        /// <returns>The resulting value.</returns>
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

        /// <summary>Parses a DFS hexadecimal address, falling back when parsing fails.</summary>
        /// <param name="text">The text.</param>
        /// <param name="fallback">The fallback value.</param>
        /// <returns>The resulting value.</returns>
        private static ushort ParseHex(string text, ushort fallback)
        {
            return ushort.TryParse(text.TrimStart('&'), System.Globalization.NumberStyles.HexNumber, null, out ushort value)
                ? value
                : fallback;
        }

        /// <summary>Builds a case-insensitive comparison key for DFS filenames.</summary>
        /// <param name="name">The name value.</param>
        /// <returns>The normalized name.</returns>
        private static string NormalizeName(string name)
        {
            return NormalizeDiscName(name).Replace(".", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        /// <summary>Converts a host name into a DFS-safe seven-character catalogue name.</summary>
        /// <param name="name">The name value.</param>
        /// <returns>The normalized name.</returns>
        private static string NormalizeDiscName(string name)
        {
            string safe = new string(name.Where(ch => ch is >= '!' and <= '~' && ch != '"').ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "HOST" : safe.Length > 7 ? safe[..7] : safe;
        }

        private readonly record struct HostFile(string Name, uint LoadAddress, uint ExecutionAddress, byte[] Data);
        private readonly record struct HostFileMetadata(uint LoadAddress, uint ExecutionAddress);
    }
}
