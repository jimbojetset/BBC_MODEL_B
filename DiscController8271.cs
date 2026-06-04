// ============================================================================
// Project:     BBC
// File:        DiscController8271.cs
// Description: Intel 8271-compatible controller backed by DFS SSD/DSD images.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace BBC
{
    /// <summary>
    /// Provides an 8271 FDC surface for Acorn DFS ROM access to SSD/DSD images.
    /// </summary>
    public sealed class DiscController8271
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 10;
        private const int SingleSidedTracks = 80;
        private const int SingleSidedImageBytes = SingleSidedTracks * SectorsPerTrack * SectorSize;
        private const byte StatusBusy = 0x80;
        private const byte StatusDataRequest = 0x04;
        private const byte StatusInterrupt = 0x08;
        private const byte StatusResultFull = 0x10;
        private const byte ResultOk = 0x00;
        private const byte ResultCommandError = 0x10;
        private const byte ResultDriveNotReady = 0x12;
        private const byte ResultSectorNotFound = 0x18;
        private const int NmiReassertDelayCycles = 96;
        private readonly byte[][] drives = [[], [], [], []];
        private readonly bool[] driveMounted = new bool[4];
        private readonly byte[] specialRegisters = new byte[0x40];
        private readonly Queue<byte> readData = new Queue<byte>();
        private readonly List<byte> writeData = new List<byte>();
        private readonly List<byte> parameters = new List<byte>();
        private readonly bool traceEnabled = Environment.GetEnvironmentVariable("BBC_8271_TRACE") == "1";
        private readonly object traceLock = new object();
        private readonly string tracePath;
        private byte command;
        private byte result;
        private bool resultAvailable = true;
        private PendingWrite? pendingWrite;
        private int selectedDrive;
        private string? mountedPath;
        private bool nmiPending;
        private int nmiDelayCycles;
        private bool busy;
        private long traceSequence;
        private int currentReadBytesProvided;
        private int currentReadBytesConsumed;
        private int lastStatusTrace = -1;

        /// <summary>Initializes a new 8271-compatible disc controller.</summary>
        public DiscController8271()
        {
            tracePath = Path.Combine(Environment.CurrentDirectory, "bbc-8271-trace.log");

            if (traceEnabled)
            {
                File.WriteAllText(tracePath, $"BBC 8271 trace started {DateTimeOffset.Now:O}{Environment.NewLine}");
                Console.WriteLine($"8271 trace:  {tracePath}");
            }
        }

        /// <summary>Raised when the controller would assert the BBC disc NMI line.</summary>
        public event Action? NmiRequested;

        /// <summary>Gets whether a disc image is mounted in drive 0.</summary>
        public bool HasMountedDisc => driveMounted[0];

        /// <summary>Gets the currently mounted host image path.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Gets whether the controller is actively transferring bytes to or from the CPU.</summary>
        public bool TransferActive => readData.Count > 0 || pendingWrite is not null;

        /// <summary>Gets the command that should be typed at BASIC after mounting.</summary>
        public string? AutoLoadCommand => TryGetAutoLoadCommand(out string? command) ? command : null;

        /// <summary>Tries to read a DFS option-3 !BOOT script from the mounted disc.</summary>
        /// <param name="script">The script text when present.</param>
        /// <returns>True when the mounted disc requests an EXEC boot script.</returns>
        public bool TryGetBootExecScript(out string? script)
        {
            script = null;

            if (!TryReadCatalogue(out List<DfsFile> files) || GetBootOption() != 3)
                return false;

            DfsFile? bootFile = files.FirstOrDefault(file => string.Equals(file.Name, "!BOOT", StringComparison.OrdinalIgnoreCase));
            if (bootFile is null || bootFile.Length <= 0)
                return false;

            int offset = bootFile.StartSector * SectorSize;
            if (offset < 0 || offset >= drives[0].Length)
                return false;

            int length = Math.Min(bootFile.Length, drives[0].Length - offset);
            script = Encoding.ASCII.GetString(drives[0], offset, length).Replace('\0', '\r');
            return script.Length > 0;
        }

        /// <summary>Returns whether an address belongs to the 8271 FDC.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True for the BBC 8271 mirror window at &amp;FE80-&amp;FE9F.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE80 and <= 0xFE9F;
        }

        /// <summary>Mounts an SSD/DSD image in drive 0/2.</summary>
        /// <param name="path">The host image path.</param>
        public void Mount(string path)
        {
            string fullPath = Path.GetFullPath(path);
            byte[] image = File.ReadAllBytes(fullPath);

            if (image.Length < 512 || image.Length % SectorSize != 0)
                throw new InvalidOperationException($"'{fullPath}' is not a sector-aligned DFS image.");

            if (image.Length >= SingleSidedImageBytes * 2)
            {
                drives[0] = new byte[SingleSidedImageBytes];
                drives[2] = new byte[image.Length - SingleSidedImageBytes];
                Array.Copy(image, 0, drives[0], 0, drives[0].Length);
                Array.Copy(image, SingleSidedImageBytes, drives[2], 0, drives[2].Length);
                driveMounted[0] = true;
                driveMounted[2] = drives[2].Length > 0;
            }
            else
            {
                drives[0] = image;
                driveMounted[0] = true;
                drives[2] = [];
                driveMounted[2] = false;
            }

            drives[1] = [];
            drives[3] = [];
            driveMounted[1] = false;
            driveMounted[3] = false;
            mountedPath = fullPath;
            Reset();
        }

        /// <summary>Resets transient 8271 state.</summary>
        public void Reset()
        {
            readData.Clear();
            writeData.Clear();
            parameters.Clear();
            pendingWrite = null;
            command = 0;
            result = 0;
            resultAvailable = true;
            selectedDrive = 0;
            Array.Clear(specialRegisters);
            nmiPending = false;
            nmiDelayCycles = 0;
            busy = false;
            currentReadBytesProvided = 0;
            currentReadBytesConsumed = 0;
            lastStatusTrace = -1;
        }

        /// <summary>Advances delayed FDC NMI events by the supplied number of CPU cycles.</summary>
        /// <param name="cycles">The elapsed 6502 cycles.</param>
        public void Tick(int cycles)
        {
            if (nmiDelayCycles <= 0)
                return;

            nmiDelayCycles -= cycles;
            if (nmiDelayCycles > 0)
                return;

            nmiDelayCycles = 0;
            NmiRequested?.Invoke();
        }

        /// <summary>Reads an 8271 register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address, ushort pc = 0)
        {
            return address switch
            {
                _ when (address & 0x07) == 0 => ReadStatus(address, pc),
                _ when (address & 0x07) == 1 => ReadResult(address, pc),
                _ when (address & 0x07) == 4 => ReadData(address, pc),
                _ => 0x00
            };
        }

        /// <summary>Writes an 8271 register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value written by the CPU.</param>
        public void Write(ushort address, byte value, ushort pc = 0)
        {
            switch (address & 0x07)
            {
                case 0:
                    BeginCommand(value, address, pc);
                    break;

                case 1:
                    WriteParameter(value, address, pc);
                    break;

                case 2:
                    TraceAccess("WRITE RESET", address, value, pc);
                    Reset();
                    break;

                case 4:
                    WriteData(value, address, pc);
                    break;
            }
        }

        private byte ReadStatus(ushort address, ushort pc)
        {
            byte status = 0;

            if (busy)
                status |= StatusBusy;

            if (readData.Count > 0 || pendingWrite is not null)
                status |= StatusDataRequest;

            if (resultAvailable)
                status |= StatusInterrupt | StatusResultFull;

            if (traceEnabled && status != lastStatusTrace)
            {
                lastStatusTrace = status;
                TraceAccess("READ STATUS", address, status, pc);
            }

            return status;
        }

        private byte ReadResult(ushort address, ushort pc)
        {
            resultAvailable = false;
            nmiPending = false;
            busy = readData.Count > 0 || pendingWrite is not null;
            TraceAccess("READ RESULT", address, result, pc);
            return result;
        }

        private byte ReadData(ushort address, ushort pc)
        {
            nmiPending = false;

            if (readData.Count == 0)
            {
                TraceAccess("READ DATA EMPTY", address, 0, pc);
                return 0x00;
            }

            byte value = readData.Dequeue();
            currentReadBytesConsumed++;
            if (currentReadBytesConsumed == 1 || readData.Count == 0 || (currentReadBytesConsumed % SectorSize) == 0)
                Trace($"READ DATA pc=${pc:X4} value=${value:X2} consumed={currentReadBytesConsumed}/{currentReadBytesProvided} remaining={readData.Count}");

            if (readData.Count == 0)
                SetResult(ResultOk, NmiReassertDelayCycles);
            else
                RequestNmi(NmiReassertDelayCycles);

            return value;
        }

        private void BeginCommand(byte value, ushort address, ushort pc)
        {
            TraceAccess("WRITE COMMAND", address, value, pc);

            if (readData.Count > 0)
            {
                Trace($"COMMAND WITH STALE DATA remaining={readData.Count} consumed={currentReadBytesConsumed}/{currentReadBytesProvided}");
                readData.Clear();
                currentReadBytesProvided = 0;
                currentReadBytesConsumed = 0;
            }

            nmiPending = false;
            nmiDelayCycles = 0;
            command = value;
            selectedDrive = 0;
            parameters.Clear();
            resultAvailable = false;
            busy = true;
            currentReadBytesProvided = 0;
            currentReadBytesConsumed = 0;
            Trace($"COMMAND {command:X2} op={(command & 0x3F):X2} drive={selectedDrive}");

            if (GetParameterCount(command) == 0)
                ExecuteCommand();
        }

        private void WriteParameter(byte value, ushort address, ushort pc)
        {
            if (!busy && command == 0)
                return;

            parameters.Add(value);
            Trace($"PARAM pc=${pc:X4} addr=${address:X4} {parameters.Count}/{GetParameterCount(command)} {value:X2}");

            if (parameters.Count >= GetParameterCount(command))
                ExecuteCommand();
        }

        private void WriteData(byte value, ushort address, ushort pc)
        {
            if (pendingWrite is null)
            {
                TraceAccess("WRITE DATA IGNORED", address, value, pc);
                return;
            }

            writeData.Add(value);
            if (writeData.Count == 1 || writeData.Count == pendingWrite.Value.Length || (writeData.Count % SectorSize) == 0)
                Trace($"WRITE DATA pc=${pc:X4} value=${value:X2} consumed={writeData.Count}/{pendingWrite.Value.Length}");

            if (writeData.Count >= pendingWrite.Value.Length)
            {
                WriteSectors(pendingWrite.Value, writeData);
                pendingWrite = null;
                writeData.Clear();
                SetResult(ResultOk);
            }
            else
            {
                RequestNmi(NmiReassertDelayCycles);
            }
        }

        private void ExecuteCommand()
        {
            byte opcode = (byte)(command & 0x3F);

            switch (opcode)
            {
                case 0x00:
                case 0x04:
                    ScanSectors(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
                    break;

                case 0x0A:
                case 0x0E:
                    PrepareWrite(parameters[0], parameters[1], 128, 1);
                    break;

                case 0x0B:
                case 0x0F:
                    PrepareWrite(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
                    break;

                case 0x07:
                    ReadSectors(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
                    break;

                case 0x12:
                case 0x16:
                    ReadSectors(parameters[0], parameters[1], 128, 1);
                    break;

                case 0x13:
                case 0x17:
                    ReadSectors(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
                    break;

                case 0x1B:
                    ReadSectorIds(parameters[0], parameters[2]);
                    break;

                case 0x1E:
                    SetResult(HasSector(parameters[0], parameters[1]) ? ResultOk : ResultSectorNotFound);
                    break;

                case 0x1F:
                    SetResult(HasSector(parameters[0], parameters[1]) ? ResultOk : ResultSectorNotFound);
                    break;

                case 0x23:
                    // Format track. Games/loaders generally only use this to probe
                    // controller capability; accept the command without modifying SSDs.
                    Trace($"FORMAT T{parameters[0]:D2}");
                    SetResult(IsDriveReady(selectedDrive) ? ResultOk : ResultDriveNotReady);
                    break;

                case 0x2B:
                    result = IsDriveReady(selectedDrive) ? (byte)0x00 : ResultDriveNotReady;
                    resultAvailable = true;
                    busy = false;
                    RequestNmi();
                    break;

                case 0x29:
                    specialRegisters[0x12] = parameters[0];
                    specialRegisters[0x1A] = parameters[0];
                    SetResult(ResultOk);
                    break;

                case 0x2C:
                    result = IsDriveReady(selectedDrive) ? (byte)0x45 : (byte)0x00;
                    resultAvailable = true;
                    busy = false;
                    RequestNmi();
                    break;

                case 0x35:
                    SetResult(ResultOk);
                    break;

                case 0x3A:
                    specialRegisters[parameters[0] & 0x3F] = parameters[1];
                    SetResult(ResultOk);
                    break;

                case 0x3D:
                    result = specialRegisters[parameters[0] & 0x3F];
                    resultAvailable = true;
                    busy = false;
                    RequestNmi();
                    break;

                default:
                    Trace($"UNKNOWN COMMAND {command:X2}");
                    SetResult(ResultCommandError);
                    break;
            }
        }

        private void ReadSectors(int track, int sector, int sectorSize, int count)
        {
            Trace($"READ D{selectedDrive} T{track:D2}/S{sector:D2} size={sectorSize} count={count}");

            if (!IsDriveReady(selectedDrive))
            {
                Trace($"READ DRIVE NOT READY D{selectedDrive}");
                SetResult(ResultDriveNotReady);
                return;
            }

            byte[] image = drives[selectedDrive];
            List<int> offsets = new List<int>();
            int currentTrack = track;
            int currentSector = sector;

            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(selectedDrive, currentTrack, currentSector, out int offset) || offset + sectorSize > image.Length)
                {
                    Trace($"READ MISS D{selectedDrive} T{currentTrack:D2}/S{currentSector:D2}");
                    SetResult(ResultSectorNotFound);
                    return;
                }

                offsets.Add(offset);
                AdvanceSector(ref currentTrack, ref currentSector);
            }

            foreach (int offset in offsets)
            {
                for (int i = 0; i < sectorSize; i++)
                    readData.Enqueue(image[offset + i]);
            }

            currentReadBytesProvided = sectorSize * count;
            currentReadBytesConsumed = 0;
            RequestNmi();
        }

        private void ScanSectors(int track, int sector, int sectorSize, int count)
        {
            Trace($"SCAN D{selectedDrive} T{track:D2}/S{sector:D2} size={sectorSize} count={count}");

            if (!IsDriveReady(selectedDrive))
            {
                Trace($"SCAN DRIVE NOT READY D{selectedDrive}");
                SetResult(ResultDriveNotReady);
                return;
            }

            int currentTrack = track;
            int currentSector = sector;
            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(selectedDrive, currentTrack, currentSector, out int offset) || offset + sectorSize > drives[selectedDrive].Length)
                {
                    Trace($"SCAN MISS D{selectedDrive} T{currentTrack:D2}/S{currentSector:D2}");
                    SetResult(ResultSectorNotFound);
                    return;
                }

                AdvanceSector(ref currentTrack, ref currentSector);
            }

            SetResult(ResultOk);
        }

        private void PrepareWrite(int track, int sector, int sectorSize, int count)
        {
            Trace($"WRITE D{selectedDrive} T{track:D2}/S{sector:D2} size={sectorSize} count={count}");

            if (!IsDriveReady(selectedDrive))
            {
                Trace($"WRITE DRIVE NOT READY D{selectedDrive}");
                SetResult(ResultDriveNotReady);
                return;
            }

            byte[] image = drives[selectedDrive];
            List<int> offsets = new List<int>();
            int currentTrack = track;
            int currentSector = sector;

            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(selectedDrive, currentTrack, currentSector, out int offset) || offset + sectorSize > image.Length)
                {
                    Trace($"WRITE MISS D{selectedDrive} T{currentTrack:D2}/S{currentSector:D2}");
                    SetResult(ResultSectorNotFound);
                    return;
                }

                offsets.Add(offset);
                AdvanceSector(ref currentTrack, ref currentSector);
            }

            pendingWrite = new PendingWrite(selectedDrive, offsets.ToArray(), sectorSize, sectorSize * count);
            writeData.Clear();
            RequestNmi();
        }

        private void WriteSectors(PendingWrite write, List<byte> bytes)
        {
            byte[] image = drives[write.Drive];
            int source = 0;

            foreach (int offset in write.Offsets)
            {
                for (int i = 0; i < write.SectorSize; i++)
                    image[offset + i] = bytes[source++];
            }
        }

        private void ReadSectorIds(int track, int count)
        {
            Trace($"READ IDS D{selectedDrive} T{track:D2} count={count}");

            if (!IsDriveReady(selectedDrive))
            {
                Trace($"READ IDS DRIVE NOT READY D{selectedDrive}");
                SetResult(ResultDriveNotReady);
                return;
            }

            int sectorCount = count == 0 ? SectorsPerTrack : Math.Min(count, SectorsPerTrack);

            for (int sector = 0; sector < sectorCount; sector++)
            {
                readData.Enqueue((byte)track);
                readData.Enqueue(0);
                readData.Enqueue((byte)sector);
                readData.Enqueue(1);
            }

            RequestNmi();
        }

        private bool HasSector(int track, int sector)
        {
            return TryGetOffset(selectedDrive, track, sector, out int offset) && offset + SectorSize <= drives[selectedDrive].Length;
        }

        private bool TryGetOffset(int drive, int track, int sector, out int offset)
        {
            if (!IsDriveReady(drive) || track < 0 || sector < 0 || sector >= SectorsPerTrack)
            {
                offset = 0;
                return false;
            }

            int logicalSector = (track * SectorsPerTrack) + sector;
            offset = logicalSector * SectorSize;
            return offset >= 0 && offset < drives[drive].Length;
        }

        private void SetResult(byte value, int nmiDelayCycles = 0)
        {
            Trace($"RESULT {value:X2}");
            result = value;
            resultAvailable = true;
            busy = false;
            RequestNmi(nmiDelayCycles);
        }

        private void Trace(string message)
        {
            if (!traceEnabled)
                return;

            lock (traceLock)
                File.AppendAllText(tracePath, $"{++traceSequence:D8} 8271 {message}{Environment.NewLine}");
        }

        private void TraceAccess(string operation, ushort address, byte value, ushort pc)
        {
            Trace($"{operation} pc=${pc:X4} addr=${address:X4} value=${value:X2}");
        }

        private void RequestNmi(int delayCycles = 0)
        {
            if (nmiPending)
                return;

            nmiPending = true;
            if (delayCycles <= 0)
            {
                NmiRequested?.Invoke();
                return;
            }

            nmiDelayCycles = delayCycles;
        }

        private bool IsDriveReady(int drive)
        {
            return drive >= 0 && drive < drives.Length && driveMounted[drive] && drives[drive].Length > 0;
        }

        private static void AdvanceSector(ref int track, ref int sector)
        {
            sector++;
            if (sector < SectorsPerTrack)
                return;

            sector = 0;
            track++;
        }

        private bool TryGetAutoLoadCommand(out string? command)
        {
            command = null;

            if (!TryReadCatalogue(out List<DfsFile> files))
                return false;

            DfsFile? loadFile = files.FirstOrDefault(file => string.Equals(file.Name, "LOAD", StringComparison.OrdinalIgnoreCase));
            if (loadFile is not null)
            {
                command = LooksLikeBasicFile(loadFile)
                    ? $"CH. \"{loadFile.Name}\""
                    : $"*EXEC {loadFile.Name}";
                return true;
            }

            DfsFile? bootFile = files.FirstOrDefault(file => string.Equals(file.Name, "!BOOT", StringComparison.OrdinalIgnoreCase));
            if (bootFile is not null)
            {
                command = $"*EXEC {bootFile.Name}";
                return true;
            }

            DfsFile? basicFile = files.FirstOrDefault(file => LooksLikeBasicFile(file));
            if (basicFile is not null)
            {
                command = $"CH. \"{basicFile.Name}\"";
                return true;
            }

            DfsFile firstFile = files[0];
            command = firstFile.LoadAddress == 0 && firstFile.ExecutionAddress == 0
                ? $"*EXEC {firstFile.Name}"
                : $"*RUN {firstFile.Name}";
            return true;
        }

        private bool TryReadCatalogue(out List<DfsFile> files)
        {
            files = new List<DfsFile>();

            if (!HasMountedDisc || drives[0].Length < 512)
                return false;

            int fileCount = drives[0][0x105] / 8;
            for (int i = 0; i < fileCount && i < 31; i++)
            {
                int nameOffset = 8 + (i * 8);
                int infoOffset = 0x108 + (i * 8);
                string name = ReadDfsName(drives[0], nameOffset);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                int packed = drives[0][infoOffset + 6];
                int loadAddress = drives[0][infoOffset] | (drives[0][infoOffset + 1] << 8) | ((packed & 0x0C) << 14);
                int executionAddress = drives[0][infoOffset + 2] | (drives[0][infoOffset + 3] << 8) | ((packed & 0xC0) << 10);
                int length = drives[0][infoOffset + 4] | (drives[0][infoOffset + 5] << 8) | ((packed & 0x30) << 12);
                int startSector = drives[0][infoOffset + 7] | ((packed & 0x03) << 8);
                files.Add(new DfsFile(name, loadAddress, executionAddress, length, startSector));
            }

            return files.Count > 0;
        }

        private int GetBootOption()
        {
            if (!HasMountedDisc || drives[0].Length <= 0x106)
                return 0;

            return (drives[0][0x106] >> 4) & 0x03;
        }

        private bool LooksLikeBasicFile(DfsFile file)
        {
            int offset = file.StartSector * SectorSize;
            return file.LoadAddress == 0x1900
                || file.LoadAddress == 0x1D00
                || (offset + 2 < drives[0].Length && drives[0][offset] == 0x0D && drives[0][offset + 1] == 0x00);
        }

        private static int GetParameterCount(byte command)
        {
            return (command & 0x3F) switch
            {
                0x00 or 0x04 => 3,
                0x07 => 3,
                0x0A or 0x0E or 0x12 or 0x16 or 0x1E => 2,
                0x0B or 0x0F or 0x13 or 0x17 or 0x1B or 0x1F => 3,
                0x2B => 0,
                0x23 => 5,
                0x29 or 0x3D => 1,
                0x2C => 0,
                0x35 => 4,
                0x3A => 2,
                _ => 0
            };
        }

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

        private static int GetSectorCount(byte sectorSizeAndCount)
        {
            int count = sectorSizeAndCount & 0x1F;
            return count == 0 ? 32 : count;
        }

        private static string ReadDfsName(byte[] image, int offset)
        {
            string leaf = Encoding.ASCII.GetString(image, offset, 7).Trim();
            char directory = (char)(image[offset + 7] & 0x7F);
            return directory is >= '!' and <= '~' && directory != '$'
                ? $"{directory}.{leaf}"
                : leaf;
        }

        private readonly record struct PendingWrite(int Drive, int[] Offsets, int SectorSize, int Length);

        private sealed record DfsFile(string Name, int LoadAddress, int ExecutionAddress, int Length, int StartSector);
    }
}
