// ============================================================================
// Project:     BBC
// File:        Intel8271_Disk.cs
// Description: Intel 8271 floppy controller surface for Acorn DFS, backed by
//              SSD/DSD images with BBC-style command timing and NMIs.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace BBC
{

    /// <summary>
    /// Acorn DFS talks to an Intel 8271 FDC through SHEILA registers and expects
    /// completion NMIs, result bytes, motor spin-up, and DFS sector geometry.
    /// </summary>
    public sealed class Intel8271_Disk : IDiscController
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 10;
        private const int SingleSidedTracks = 80;
        private const int TrackBytes = SectorsPerTrack * SectorSize;
        private const int SingleSidedImageBytes = SingleSidedTracks * SectorsPerTrack * SectorSize;
        private const byte StatusBusy = 0x80;
        private const byte StatusDataRequest = 0x04;
        private const byte StatusInterrupt = 0x08;
        private const byte StatusResultFull = 0x10;
        private const byte ResultOk = 0x00;
        private const byte ResultCommandError = 0x10;
        private const byte ResultDriveNotReady = 0x12;
        private const byte ResultSectorNotFound = 0x18;
        private const int CpuClockHz = 2_000_000;
        private const int DiscRotationsPerMinute = 300;
        private const int DiscRotationsPerSecond = DiscRotationsPerMinute / 60;
        private const int CyclesPerMillisecond = CpuClockHz / 1000;
        private const int MotorSpinUpCycles = 500 * CyclesPerMillisecond;
        private const int MotorSpinDownCycles = 3000 * CyclesPerMillisecond;
        private const int TrackToTrackSeekCycles = 6 * CyclesPerMillisecond;
        private const int HeadSettleCycles = 15 * CyclesPerMillisecond;
        private const int RevolutionCycles = CpuClockHz / DiscRotationsPerSecond;
        private const int SectorTransferCycles = CpuClockHz / (DiscRotationsPerSecond * SectorsPerTrack);
        private const int NmiReassertDelayCycles = SectorTransferCycles / SectorSize;
        private readonly byte[][] drives = [[], [], [], []];
        private readonly bool[] driveMounted = new bool[4];
        private readonly bool[] driveActivityLedActive = new bool[2];
        private readonly string?[] mountedPaths = new string?[4];
        private readonly string?[] mountedFileNames = new string?[4];
        private readonly bool[] imageDirtyByDrive = new bool[4];
        private readonly int[] currentTrack = new int[4];
        private readonly bool[] motorSpinning = new bool[4];
        private readonly long[] motorStartedAtCycle = new long[4];
        private readonly byte[] specialRegisters = new byte[0x40];
        private readonly Queue<byte> readData = new Queue<byte>();
        private readonly List<byte> writeData = new List<byte>();
        private readonly List<byte> parameters = new List<byte>();
        private byte command;
        private byte result;
        private bool resultAvailable = true;
        private PendingWrite? pendingWrite;
        private int selectedDrive;
        private string? mountedPath;
        private string? mountedFileName;
        private long elapsedCycles;
        private bool nmiPending;
        private int nmiDelayCycles;
        private int motorIdleCycles;
        private bool busy;
        private bool imageDirty;
        private bool writeProtected;

        public Intel8271_Disk()
        {
        }

        public bool HasMountedDisc => AnyDriveMounted;

        public string? MountedPath => mountedPath;

        public string? MountedFileName => mountedFileName;

        public bool ImageDirty => imageDirty;

        public string MountedDriveSummary => string.Join(", ",
            Enumerable.Range(0, drives.Length)
                .Where(drive => driveMounted[drive])
                .Select(drive => $"{drive}:{mountedFileNames[drive] ?? "disc"}"));

        public bool WriteProtected
        {
            get => writeProtected;
            set => writeProtected = value;
        }

        public bool TransferActive => readData.Count > 0 || pendingWrite is not null;

        public bool TickRequired =>
            AnyDriveMounted
            || motorIdleCycles > 0
            || nmiDelayCycles > 0
            || busy
            || nmiPending
            || readData.Count > 0
            || pendingWrite is not null;

        private bool AnyDriveMounted =>
            driveMounted[0]
            || driveMounted[1]
            || driveMounted[2]
            || driveMounted[3];

        public bool ReadLedActive => driveActivityLedActive.Any(active => active);

        public bool IsPhysicalDriveMounted(int drive)
        {
            return drive is >= 0 and <= 1 && (driveMounted[drive] || driveMounted[drive + 2]);
        }

        public bool IsPhysicalDriveActivityLedActive(int drive)
        {
            return drive is >= 0 and <= 1 && driveActivityLedActive[drive];
        }

        public bool IsPhysicalDriveDoubleSided(int drive)
        {
            return drive is >= 0 and <= 1 && driveMounted[drive] && driveMounted[drive + 2];
        }

        public string? GetPhysicalDriveLabel(int drive)
        {
            if (drive is < 0 or > 1)
                return null;

            return mountedFileNames[drive] ?? mountedFileNames[drive + 2];
        }

        public string? AutoLoadCommand => TryGetBootExecScript(out string? script) && script is not null
            ? "*EXEC !BOOT"
            : null;

        public bool NmiLineAsserted => nmiPending && nmiDelayCycles <= 0;

        public event Action<int>? DriveMotorStarted;

        public event Action<int>? DriveMotorStopped;

        public event Action<int, int>? DriveSeek;

        private int ActiveDrive => selectedDrive + ((specialRegisters[0x23] & 0x20) != 0 ? 2 : 0);

        /// <summary>DFS option 3 boots by EXECing !BOOT, not by CHAINing it.</summary>
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

        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE80 and <= 0xFE9F;
        }

        public void Mount(string path, int drive = 0)
        {
            string fullPath = Path.GetFullPath(path);
            string fileName = Path.GetFileName(fullPath);
            byte[] image = File.ReadAllBytes(fullPath);

            MountImage(image, drive, fullPath, fileName, readOnly: false);
        }

        public void MountImage(byte[] image, int drive, string? sourcePath, string displayName, bool readOnly)
        {
            if (image.Length == 0)
                throw new InvalidOperationException($"'{displayName}' is empty.");

            if (drive < 0 || drive >= drives.Length)
                throw new ArgumentOutOfRangeException(nameof(drive), "DFS drive must be 0-3.");

            if (image.Length < 512 || image.Length % SectorSize != 0)
                throw new InvalidOperationException($"'{displayName}' is not a sector-aligned DFS image.");

            if (image.Length >= SingleSidedImageBytes * 2)
            {
                if (drive >= 2)
                    throw new InvalidOperationException("Double-sided DFS images must be mounted as physical drive 0 or 1.");

                int reverseSideDrive = drive + 2;
                drives[drive] = new byte[SingleSidedImageBytes];
                drives[reverseSideDrive] = new byte[SingleSidedImageBytes];
                DeinterleaveDsdImage(image, drives[drive], drives[reverseSideDrive]);
                driveMounted[drive] = true;
                driveMounted[reverseSideDrive] = true;
                mountedPaths[drive] = readOnly ? null : sourcePath;
                mountedFileNames[drive] = displayName;
                mountedPaths[reverseSideDrive] = readOnly ? null : sourcePath;
                mountedFileNames[reverseSideDrive] = displayName;
                imageDirtyByDrive[drive] = false;
                imageDirtyByDrive[reverseSideDrive] = false;
            }
            else
            {
                drives[drive] = image;
                driveMounted[drive] = true;
                mountedPaths[drive] = readOnly ? null : sourcePath;
                mountedFileNames[drive] = displayName;
                imageDirtyByDrive[drive] = false;
            }

            Array.Clear(currentTrack);
            Array.Clear(motorSpinning);
            Array.Clear(motorStartedAtCycle);
            Array.Clear(driveActivityLedActive);
            motorIdleCycles = 0;
            mountedPath = mountedPaths[0] ?? sourcePath;
            mountedFileName = mountedFileNames[0] ?? displayName;
            imageDirty = false;
            writeProtected = readOnly;
            Reset();
        }

        public void EjectPhysicalDrive(int drive)
        {
            if (drive is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(drive), "Physical DFS drive must be 0 or 1.");

            int reverseSideDrive = drive + 2;
            if (!driveMounted[drive] && !driveMounted[reverseSideDrive])
                return;

            FlushPhysicalDrive(drive);

            ClearDrive(drive);
            ClearDrive(reverseSideDrive);

            if (selectedDrive == drive || selectedDrive == reverseSideDrive)
                selectedDrive = 0;

            mountedPath = mountedPaths.FirstOrDefault(path => path is not null);
            mountedFileName = mountedFileNames.FirstOrDefault(fileName => fileName is not null);
            imageDirty = imageDirtyByDrive.Any(dirty => dirty);
            Reset();
        }

        /// <summary>8271 writes alter the mounted DFS image only when the host file is not write-protected.</summary>
        public bool Flush()
        {
            bool anyDirty = imageDirty || imageDirtyByDrive.Any(dirty => dirty);
            if (!anyDirty)
                return false;
            if (writeProtected)
            {
                imageDirty = false;
                Array.Clear(imageDirtyByDrive);
                return false;
            }

            try
            {
                bool flushed = FlushPhysicalDrive(0) | FlushPhysicalDrive(1);
                for (int drive = 2; drive < drives.Length; drive++)
                {
                    if (!imageDirtyByDrive[drive] || string.IsNullOrEmpty(mountedPaths[drive]))
                        continue;

                    File.WriteAllBytes(mountedPaths[drive]!, drives[drive]);
                    imageDirtyByDrive[drive] = false;
                    flushed = true;
                }

                imageDirty = false;
                return flushed;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Disc image flush failed: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Disc image flush failed: {ex.Message}");
                return false;
            }
        }

        public void Reset()
        {
            readData.Clear();
            writeData.Clear();
            parameters.Clear();
            Array.Clear(driveActivityLedActive);
            pendingWrite = null;
            command = 0;
            result = 0;
            resultAvailable = true;
            selectedDrive = 0;
            Array.Clear(specialRegisters);
            nmiPending = false;
            nmiDelayCycles = 0;
            busy = false;
        }

        public void PowerOff()
        {
            bool stopped = motorSpinning.Any(spinning => spinning);
            Array.Clear(motorSpinning);
            Array.Clear(motorStartedAtCycle);
            motorIdleCycles = 0;

            if (stopped)
                DriveMotorStopped?.Invoke(selectedDrive);

            Reset();
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(drives.Length);
            for (int i = 0; i < drives.Length; i++)
            {
                writer.Write(drives[i].Length);
                writer.Write(drives[i]);
                writer.Write(driveMounted[i]);
                WriteString(writer, mountedPaths[i]);
                WriteString(writer, mountedFileNames[i]);
                writer.Write(imageDirtyByDrive[i]);
                writer.Write(currentTrack[i]);
                writer.Write(motorSpinning[i]);
                writer.Write(motorStartedAtCycle[i]);
            }

            writer.Write(driveActivityLedActive.Length);
            foreach (bool active in driveActivityLedActive)
                writer.Write(active);

            writer.Write(specialRegisters.Length);
            writer.Write(specialRegisters);
            WriteByteQueue(writer, readData);
            WriteByteList(writer, writeData);
            WriteByteList(writer, parameters);
            writer.Write(command);
            writer.Write(result);
            writer.Write(resultAvailable);
            WritePendingWrite(writer, pendingWrite);
            writer.Write(selectedDrive);
            WriteString(writer, mountedPath);
            WriteString(writer, mountedFileName);
            writer.Write(elapsedCycles);
            writer.Write(nmiPending);
            writer.Write(nmiDelayCycles);
            writer.Write(motorIdleCycles);
            writer.Write(busy);
            writer.Write(imageDirty);
            writer.Write(writeProtected);
        }

        public void LoadState(BinaryReader reader)
        {
            int driveCount = reader.ReadInt32();
            if (driveCount != drives.Length)
                throw new InvalidDataException("Save state has an incompatible 8271 drive block.");

            for (int i = 0; i < drives.Length; i++)
            {
                int imageLength = reader.ReadInt32();
                drives[i] = reader.ReadBytes(imageLength);
                if (drives[i].Length != imageLength)
                    throw new EndOfStreamException();

                driveMounted[i] = reader.ReadBoolean();
                mountedPaths[i] = ReadString(reader);
                mountedFileNames[i] = ReadString(reader);
                imageDirtyByDrive[i] = reader.ReadBoolean();
                currentTrack[i] = reader.ReadInt32();
                motorSpinning[i] = reader.ReadBoolean();
                motorStartedAtCycle[i] = reader.ReadInt64();
            }

            int ledCount = reader.ReadInt32();
            if (ledCount != driveActivityLedActive.Length)
                throw new InvalidDataException("Save state has an incompatible 8271 LED block.");

            for (int i = 0; i < driveActivityLedActive.Length; i++)
                driveActivityLedActive[i] = reader.ReadBoolean();

            ReadBytes(reader, specialRegisters, "8271 special register");
            ReadByteQueue(reader, readData);
            ReadByteList(reader, writeData);
            ReadByteList(reader, parameters);
            command = reader.ReadByte();
            result = reader.ReadByte();
            resultAvailable = reader.ReadBoolean();
            pendingWrite = ReadPendingWrite(reader);
            selectedDrive = reader.ReadInt32();
            mountedPath = ReadString(reader);
            mountedFileName = ReadString(reader);
            elapsedCycles = reader.ReadInt64();
            nmiPending = reader.ReadBoolean();
            nmiDelayCycles = reader.ReadInt32();
            motorIdleCycles = reader.ReadInt32();
            busy = reader.ReadBoolean();
            imageDirty = reader.ReadBoolean();
            writeProtected = reader.ReadBoolean();
        }

        public void SaveMediaState(BinaryWriter writer) => SaveState(writer);

        public void LoadMediaState(BinaryReader reader) => LoadState(reader);

        private static void WriteString(BinaryWriter writer, string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
                writer.Write(value);
        }

        private static string? ReadString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        private static void WriteByteQueue(BinaryWriter writer, Queue<byte> queue)
        {
            writer.Write(queue.Count);
            foreach (byte value in queue)
                writer.Write(value);
        }

        private static void ReadByteQueue(BinaryReader reader, Queue<byte> queue)
        {
            queue.Clear();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                queue.Enqueue(reader.ReadByte());
        }

        private static void WriteByteList(BinaryWriter writer, List<byte> list)
        {
            writer.Write(list.Count);
            foreach (byte value in list)
                writer.Write(value);
        }

        private static void ReadByteList(BinaryReader reader, List<byte> list)
        {
            list.Clear();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                list.Add(reader.ReadByte());
        }

        private static void WritePendingWrite(BinaryWriter writer, PendingWrite? write)
        {
            writer.Write(write is not null);
            if (write is null)
                return;

            writer.Write(write.Value.Drive);
            writer.Write(write.Value.Offsets.Length);
            foreach (int offset in write.Value.Offsets)
                writer.Write(offset);

            writer.Write(write.Value.SectorSize);
            writer.Write(write.Value.Length);
        }

        private static PendingWrite? ReadPendingWrite(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
                return null;

            int drive = reader.ReadInt32();
            int offsetCount = reader.ReadInt32();
            int[] offsets = new int[offsetCount];
            for (int i = 0; i < offsets.Length; i++)
                offsets[i] = reader.ReadInt32();

            return new PendingWrite(drive, offsets, reader.ReadInt32(), reader.ReadInt32());
        }

        private static void ReadBytes(BinaryReader reader, byte[] destination, string name)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"Save state has an incompatible {name} block.");

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();

            bytes.CopyTo(destination, 0);
        }

        /// <summary>DFS code polls the 8271 around motor spin-up and command-complete NMI timing.</summary>
        public void Tick(int cycles)
        {
            if (cycles <= 0)
                return;

            elapsedCycles += cycles;

            if (motorIdleCycles > 0)
            {
                motorIdleCycles -= cycles;
                if (motorIdleCycles <= 0)
                {
                    motorIdleCycles = 0;
                    bool stopped = motorSpinning.Any(spinning => spinning);
                    if (stopped)
                        DriveMotorStopped?.Invoke(selectedDrive);

                    Array.Clear(motorSpinning);
                }
            }

            if (nmiDelayCycles <= 0)
                return;

            nmiDelayCycles -= cycles;
            if (nmiDelayCycles > 0)
                return;

            nmiDelayCycles = 0;
        }

        public byte Read(ushort address)
        {
            return address switch
            {
                _ when (address & 0x07) == 0 => ReadStatus(),
                _ when (address & 0x07) == 1 => ReadResult(),
                _ when (address & 0x07) == 4 => ReadData(),
                _ => 0x00
            };
        }

        public void Write(ushort address, byte value)
        {
            switch (address & 0x07)
            {
                case 0:
                    BeginCommand(value);
                    break;

                case 1:
                    WriteParameter(value);
                    break;

                case 2:
                    Reset();
                    break;

                case 4:
                    WriteData(value);
                    break;
            }
        }

        private byte ReadStatus()
        {
            byte status = 0;

            if (busy || (nmiDelayCycles > 0 && resultAvailable))
                status |= StatusBusy;

            if (nmiDelayCycles <= 0 && (readData.Count > 0 || pendingWrite is not null))
                status |= StatusDataRequest;

            if (NmiLineAsserted)
                status |= StatusInterrupt;

            if (NmiLineAsserted && resultAvailable)
                status |= StatusResultFull;

            return status;
        }

        private byte ReadResult()
        {
            if (nmiDelayCycles > 0)
                return 0x00;

            byte value = result;
            resultAvailable = false;
            nmiPending = false;
            busy = readData.Count > 0 || pendingWrite is not null;

            return value;
        }

        private byte ReadData()
        {
            if (nmiDelayCycles > 0)
                return 0x00;

            nmiPending = false;

            if (readData.Count == 0)
            {
                return 0x00;
            }

            byte value = readData.Dequeue();

            if (readData.Count == 0)
            {
                ClearDriveActivityLed();
                SetResult(ResultOk, NmiReassertDelayCycles);
            }
            else
            {
                RequestNmi(NmiReassertDelayCycles);
            }

            return value;
        }

        private void BeginCommand(byte value)
        {

            if (readData.Count > 0)
            {
               readData.Clear();
               ClearDriveActivityLed();
            }

            nmiPending = false;
            nmiDelayCycles = 0;
            command = value;
            selectedDrive = (command & 0x80) != 0 ? 1 : 0;
            parameters.Clear();
            resultAvailable = false;
            busy = true;

            if (GetParameterCount(command) == 0)
                ExecuteCommand();
        }

        private void WriteParameter(byte value)
        {
            if (!busy && command == 0)
                return;

            parameters.Add(value);

            if (parameters.Count >= GetParameterCount(command))
                ExecuteCommand();
        }

        private void WriteData(byte value)
        {
            if (pendingWrite is null)
            {
                return;
            }

            nmiPending = false;
            writeData.Add(value);

            if (writeData.Count >= pendingWrite.Value.Length)
            {
                int byteCount = writeData.Count;
                WriteSectors(pendingWrite.Value, writeData);
                SetDriveActivityLed(pendingWrite.Value.Drive, false);
                pendingWrite = null;
                writeData.Clear();
                Flush();

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
                    PrepareWrite(parameters[0], parameters[1], SectorSize, 1);
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
                    ReadSectors(parameters[0], parameters[1], SectorSize, 1);
                    break;

                case 0x13:
                case 0x17:
                    ReadSectors(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
                    break;

                case 0x1B:
                    ReadSectorIds(parameters[0], parameters[2]);
                    break;

                case 0x1E:
                    VerifySector(parameters[0], parameters[1]);
                    break;

                case 0x1F:
                    VerifySector(parameters[0], parameters[1]);
                    break;

                case 0x23:
                    if (!IsDriveReady(ActiveDrive))
                        SetResult(ResultSectorNotFound);
                    else
                        SetResult(ResultOk, BeginMediaAccess(parameters[0], 0));
                    break;

                case 0x2B:
                    SetPolledResult(IsDriveReady(ActiveDrive) ? (byte)0x00 : ResultDriveNotReady);
                    break;

                case 0x29:
                    specialRegisters[0x12] = parameters[0];
                    specialRegisters[0x1A] = parameters[0];
                    SetResult(ResultOk);
                    break;

                case 0x2C:
                    // Report the selected emulated drive as present even when it has no
                    // image mounted.  Returning zero here makes Acorn DFS wait forever
                    // for the drive to become ready, so it never issues the sector read
                    // which completes with the normal "Disk fault 18" result.
                    SetPolledResult(0x45);

                    break;

                case 0x35:
                    SetPolledResult(ResultOk);
                    break;

                case 0x3A:
                    specialRegisters[parameters[0] & 0x3F] = parameters[1];

                    SetPolledResult(ResultOk);
                    break;

                case 0x3D:
                    int specialRegister = parameters[0] & 0x3F;
                    SetPolledResult(specialRegisters[specialRegister]);

                    break;

                default:
                    SetResult(ResultCommandError);
                    break;
            }
        }

        private void ReadSectors(int track, int sector, int sectorSize, int count)
        {
            int drive = ActiveDrive;
            if (!IsDriveReady(drive))
            {
                SetResult(ResultSectorNotFound);
                return;
            }

            byte[] image = drives[drive];
            List<int> offsets = new List<int>();
            int currentTrack = track;
            int currentSector = sector;

            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(drive, currentTrack, currentSector, out int offset) || offset + sectorSize > image.Length)
                {
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

            SetDriveActivityLed(drive, readData.Count > 0);

            RequestNmi(BeginMediaAccess(track, sector));
        }

        private void ScanSectors(int track, int sector, int sectorSize, int count)
        {

            int drive = ActiveDrive;
            if (!IsDriveReady(drive))
            {
                SetResult(ResultSectorNotFound);
                return;
            }

            int currentTrack = track;
            int currentSector = sector;
            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(drive, currentTrack, currentSector, out int offset) || offset + sectorSize > drives[drive].Length)
                {
                    SetResult(ResultSectorNotFound);
                    return;
                }

                AdvanceSector(ref currentTrack, ref currentSector);
            }

            SetResult(ResultOk, BeginMediaAccess(track, sector));
        }

        private void PrepareWrite(int track, int sector, int sectorSize, int count)
        {

            int drive = ActiveDrive;
            if (!IsDriveReady(drive))
            {
                SetResult(ResultSectorNotFound);
                return;
            }

            byte[] image = drives[drive];
            List<int> offsets = new List<int>();
            int currentTrack = track;
            int currentSector = sector;

            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(drive, currentTrack, currentSector, out int offset) || offset + sectorSize > image.Length)
                {
                    SetResult(ResultSectorNotFound);
                    return;
                }

                offsets.Add(offset);
                AdvanceSector(ref currentTrack, ref currentSector);
            }

            pendingWrite = new PendingWrite(drive, offsets.ToArray(), sectorSize, sectorSize * count);
            writeData.Clear();
            SetDriveActivityLed(drive, true);

            RequestNmi(BeginMediaAccess(track, sector));
        }

        private void WriteSectors(PendingWrite write, List<byte> bytes)
        {
            if (writeProtected)
                return;

            byte[] image = drives[write.Drive];
            int source = 0;

            foreach (int offset in write.Offsets)
            {
                for (int i = 0; i < write.SectorSize; i++)
                    image[offset + i] = bytes[source++];
            }

            imageDirty = true;
            imageDirtyByDrive[write.Drive] = true;
        }

        private void ReadSectorIds(int track, int count)
        {

            int drive = ActiveDrive;
            if (!IsDriveReady(drive))
            {
                SetResult(ResultSectorNotFound);
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

            SetDriveActivityLed(drive, readData.Count > 0);

            RequestNmi(BeginMediaAccess(track, 0));
        }

        private bool HasSector(int track, int sector)
        {
            int drive = ActiveDrive;
            return TryGetOffset(drive, track, sector, out int offset) && offset + SectorSize <= drives[drive].Length;
        }

        private void VerifySector(int track, int sector)
        {
            if (!IsDriveReady(ActiveDrive))
            {
                SetResult(ResultSectorNotFound);
                return;
            }

            SetResult(HasSector(track, sector) ? ResultOk : ResultSectorNotFound, BeginMediaAccess(track, sector));
        }

        private int BeginMediaAccess(int track, int sector)
        {
            int drive = ActiveDrive;
            int delayCycles = 0;

            if (!motorSpinning[drive])
            {
                motorSpinning[drive] = true;
                motorStartedAtCycle[drive] = elapsedCycles;
                DriveMotorStarted?.Invoke(drive);
                delayCycles += MotorSpinUpCycles;
            }

            int trackDelta = track - currentTrack[drive];
            int seekTracks = Math.Abs(trackDelta);
            if (seekTracks > 0)
            {
                DriveSeek?.Invoke(drive, trackDelta);
                delayCycles += (seekTracks * TrackToTrackSeekCycles) + HeadSettleCycles;
                currentTrack[drive] = track;
            }

            motorIdleCycles = MotorSpinDownCycles;
            return delayCycles + GetRotationalLatencyCycles(drive, sector, elapsedCycles + delayCycles);
        }

        private void SetDriveActivityLed(int drive, bool active)
        {
            int physicalDrive = drive & 1;
            if (physicalDrive < driveActivityLedActive.Length)
                driveActivityLedActive[physicalDrive] = active;
        }

        private void ClearDriveActivityLed()
        {
            Array.Clear(driveActivityLedActive);
        }

        private int GetRotationalLatencyCycles(int drive, int sector, long readyAtCycle)
        {
            int physicalSector = Math.Clamp(sector, 0, SectorsPerTrack - 1);
            long phase = (readyAtCycle - motorStartedAtCycle[drive]) % RevolutionCycles;
            if (phase < 0)
                phase += RevolutionCycles;

            int targetPhase = physicalSector * SectorTransferCycles;
            return (int)((targetPhase - phase + RevolutionCycles) % RevolutionCycles);
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

        internal bool TryReadRawSector(int drive, int track, int sector, Span<byte> destination)
        {
            if (destination.Length < SectorSize || !TryGetOffset(drive, track, sector, out int offset)
                || offset + SectorSize > drives[drive].Length)
                return false;

            drives[drive].AsSpan(offset, SectorSize).CopyTo(destination);
            return true;
        }

        internal bool TryWriteRawSector(int drive, int track, int sector, ReadOnlySpan<byte> source)
        {
            if (writeProtected || source.Length < SectorSize || !TryGetOffset(drive, track, sector, out int offset)
                || offset + SectorSize > drives[drive].Length)
                return false;

            source[..SectorSize].CopyTo(drives[drive].AsSpan(offset, SectorSize));
            imageDirty = true;
            imageDirtyByDrive[drive] = true;
            return true;
        }

        internal void SetRawActivityLed(int drive, bool active) => SetDriveActivityLed(drive, active);

        internal bool RawWriteProtected => writeProtected;

        private void SetResult(byte value, int nmiDelayCycles = 0)
        {
            result = value;
            resultAvailable = true;
            busy = false;

            RequestNmi(nmiDelayCycles);
        }

        private void SetPolledResult(byte value)
        {
            result = value;
            resultAvailable = true;
            busy = false;
            nmiPending = false;
            nmiDelayCycles = 0;
        }

        private void RequestNmi(int delayCycles = 0)
        {
            if (nmiPending)
                return;

            nmiPending = true;

            if (delayCycles > 0)
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

        private static void DeinterleaveDsdImage(byte[] image, byte[] side0, byte[] side1)
        {
            int tracks = Math.Min(side0.Length, side1.Length) / TrackBytes;
            for (int track = 0; track < tracks; track++)
            {
                int source = track * TrackBytes * 2;
                int target = track * TrackBytes;
                Array.Copy(image, source, side0, target, TrackBytes);
                Array.Copy(image, source + TrackBytes, side1, target, TrackBytes);
            }
        }

        private static void InterleaveDsdImage(byte[] side0, byte[] side1, byte[] image)
        {
            int tracks = Math.Min(side0.Length, side1.Length) / TrackBytes;
            for (int track = 0; track < tracks; track++)
            {
                int source = track * TrackBytes;
                int target = track * TrackBytes * 2;
                Array.Copy(side0, source, image, target, TrackBytes);
                Array.Copy(side1, source, image, target + TrackBytes, TrackBytes);
            }
        }

        private bool FlushPhysicalDrive(int drive)
        {
            int reverseSideDrive = drive + 2;
            if (!imageDirtyByDrive[drive] && !imageDirtyByDrive[reverseSideDrive])
                return false;

            string? path = mountedPaths[drive];
            if (string.IsNullOrEmpty(path))
                return false;

            if (driveMounted[reverseSideDrive]
                && drives[reverseSideDrive].Length > 0
                && string.Equals(mountedPaths[drive], mountedPaths[reverseSideDrive], StringComparison.Ordinal))
            {
                byte[] combined = new byte[drives[drive].Length + drives[reverseSideDrive].Length];
                InterleaveDsdImage(drives[drive], drives[reverseSideDrive], combined);
                File.WriteAllBytes(path, combined);
                imageDirtyByDrive[drive] = false;
                imageDirtyByDrive[reverseSideDrive] = false;
                return true;
            }

            File.WriteAllBytes(path, drives[drive]);
            imageDirtyByDrive[drive] = false;
            return true;
        }

        private void ClearDrive(int drive)
        {
            drives[drive] = Array.Empty<byte>();
            driveMounted[drive] = false;
            mountedPaths[drive] = null;
            mountedFileNames[drive] = null;
            imageDirtyByDrive[drive] = false;
            currentTrack[drive] = 0;
            motorSpinning[drive] = false;
            motorStartedAtCycle[drive] = 0;

            if (drive < driveActivityLedActive.Length)
                driveActivityLedActive[drive] = false;
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
