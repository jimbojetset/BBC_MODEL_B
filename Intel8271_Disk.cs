// ============================================================================
// Project:     BBC
// File:        Intel8271_Disk.cs
// Description: Intel 8271-compatible floppy disc controller backed by DFS
//              SSD/DSD images, including command timing and drive activity.
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
    public sealed class Intel8271_Disk
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
        private volatile bool readLedActive;
        private bool busy;
        private bool imageDirty;
        private bool writeProtected;
        private StreamWriter? traceWriter;
        private string? tracePath;

        /// <summary>Initializes a new 8271-compatible disc controller.</summary>
        public Intel8271_Disk()
        {
        }

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public event Action? NmiRequested;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public bool HasMountedDisc => driveMounted[0];

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public string? MountedFileName => mountedFileName;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public bool ImageDirty => imageDirty;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public bool WriteProtected
        {
            get => writeProtected;
            set => writeProtected = value;
        }

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public bool TransferActive => readData.Count > 0 || pendingWrite is not null;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public bool ReadLedActive => readLedActive;

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        public string? AutoLoadCommand => TryGetAutoLoadCommand(out string? command) ? command : null;

        /// <summary>Gets whether 8271 diagnostic tracing is currently enabled.</summary>
        public bool TraceEnabled => traceWriter is not null;

        /// <summary>Starts writing 8271 diagnostic trace events to a host file.</summary>
        /// <param name="path">The trace file path.</param>
        public void StartTrace(string path)
        {
            StopTrace();
            tracePath = Path.GetFullPath(path);
            traceWriter = new StreamWriter(tracePath, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            Trace("TRACE START");
        }

        /// <summary>Stops the current 8271 diagnostic trace.</summary>
        /// <returns>The trace file path, when tracing had been enabled.</returns>
        public string? StopTrace()
        {
            if (traceWriter is null)
                return tracePath;

            Trace("TRACE STOP");
            traceWriter.Dispose();
            traceWriter = null;
            return tracePath;
        }

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

        /// <summary>Checks whether address is true for the current emulator state.</summary>
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
            string fileName = Path.GetFileName(fullPath);
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
            Array.Clear(currentTrack);
            Array.Clear(motorSpinning);
            Array.Clear(motorStartedAtCycle);
            motorIdleCycles = 0;
            mountedPath = fullPath;
            mountedFileName = fileName;
            imageDirty = false;
            Reset();
        }

        /// <summary>Writes dirty mounted disc image buffers back to the host file when writes are allowed.</summary>
        /// <returns>True when a flush was attempted, false when no mounted image was present.</returns>
        public bool Flush()
        {
            if (!imageDirty)
                return false;
            if (string.IsNullOrEmpty(mountedPath))
                return false;
            if (writeProtected)
            {
                imageDirty = false;
                return false;
            }

            try
            {
                byte[] combined;
                if (drives[2].Length > 0)
                {
                    combined = new byte[drives[0].Length + drives[2].Length];
                    Array.Copy(drives[0], 0, combined, 0, drives[0].Length);
                    Array.Copy(drives[2], 0, combined, drives[0].Length, drives[2].Length);
                }
                else
                {
                    combined = drives[0];
                }

                File.WriteAllBytes(mountedPath, combined);
                imageDirty = false;
                return true;
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

        /// <summary>Resets transient 8271 state.</summary>
        public void Reset()
        {
            readData.Clear();
            writeData.Clear();
            parameters.Clear();
            pendingWrite = null;
            readLedActive = false;
            command = 0;
            result = 0;
            resultAvailable = true;
            selectedDrive = 0;
            Array.Clear(specialRegisters);
            nmiPending = false;
            nmiDelayCycles = 0;
            busy = false;
        }

        /// <summary>Advances 8271 motor timing, delayed NMIs, and spin-down state.</summary>
        /// <param name="cycles">The elapsed 6502 cycles.</param>
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
                    Array.Clear(motorSpinning);
                }
            }

            if (nmiDelayCycles <= 0)
                return;

            nmiDelayCycles -= cycles;
            if (nmiDelayCycles > 0)
                return;

            nmiDelayCycles = 0;
            NmiRequested?.Invoke();
        }

        /// <summary>Reads  from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
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

        /// <summary>Writes  into emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value written by the CPU.</param>
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

        /// <summary>Builds the 8271 status byte from busy, result, interrupt, and data-request state.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadStatus()
        {
            byte status = 0;

            if (busy || (nmiDelayCycles > 0 && resultAvailable))
                status |= StatusBusy;

            if (nmiDelayCycles <= 0 && (readData.Count > 0 || pendingWrite is not null))
                status |= StatusDataRequest;

            if (nmiDelayCycles <= 0 && resultAvailable)
                status |= StatusInterrupt | StatusResultFull;

            return status;
        }

        /// <summary>Reads the current 8271 result byte and clears result-ready status.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadResult()
        {
            if (nmiDelayCycles > 0)
                return 0x00;

            byte value = result;
            resultAvailable = false;
            nmiPending = false;
            busy = readData.Count > 0 || pendingWrite is not null;
            Trace($"RESULT read ${value:X2} drive={selectedDrive} readBytes={readData.Count} pendingWrite={pendingWrite is not null}");
            return value;
        }

        /// <summary>Reads the next byte from the 8271 data FIFO and updates transfer status.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
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
                readLedActive = false;
                SetResult(ResultOk, NmiReassertDelayCycles);
            }
            else
            {
                RequestNmi(NmiReassertDelayCycles);
            }

            return value;
        }

        /// <summary>Begins command.</summary>
        /// <param name="value">The input value.</param>
        private void BeginCommand(byte value)
        {

            if (readData.Count > 0)
            {
               readData.Clear();
               readLedActive = false;
            }

            nmiPending = false;
            nmiDelayCycles = 0;
            command = value;
            selectedDrive = 0;
            parameters.Clear();
            resultAvailable = false;
            busy = true;
            Trace($"CMD ${command:X2} op=${command & 0x3F:X2} commandDrive={(command >> 6) & 0x03} selectedDrive={selectedDrive}");

            if (GetParameterCount(command) == 0)
                ExecuteCommand();
        }

        /// <summary>Collects an 8271 command parameter byte and executes once the command is complete.</summary>
        /// <param name="value">The input value.</param>
        private void WriteParameter(byte value)
        {
            if (!busy && command == 0)
                return;

            parameters.Add(value);
            Trace($"PARAM[{parameters.Count - 1}] ${value:X2}");

            if (parameters.Count >= GetParameterCount(command))
                ExecuteCommand();
        }

        /// <summary>Accepts a byte written to the 8271 data register during a pending write transfer.</summary>
        /// <param name="value">The input value.</param>
        private void WriteData(byte value)
        {
            if (pendingWrite is null)
            {
                return;
            }

            writeData.Add(value);

            if (writeData.Count >= pendingWrite.Value.Length)
            {
                int byteCount = writeData.Count;
                WriteSectors(pendingWrite.Value, writeData);
                pendingWrite = null;
                writeData.Clear();
                Trace($"WRITE complete bytes={byteCount}");
                SetResult(ResultOk);
            }
            else
            {
                RequestNmi(NmiReassertDelayCycles);
            }
        }

        /// <summary>Decodes the current 8271 command and dispatches the requested disc operation.</summary>
        private void ExecuteCommand()
        {
            byte opcode = (byte)(command & 0x3F);
            Trace($"EXEC op=${opcode:X2} drive={selectedDrive} params={string.Join(' ', parameters.Select(p => $"${p:X2}"))}");

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
                    VerifySector(parameters[0], parameters[1]);
                    break;

                case 0x1F:
                    VerifySector(parameters[0], parameters[1]);
                    break;

                case 0x23:
                    if (!IsDriveReady(selectedDrive))
                        SetResult(ResultDriveNotReady);
                    else
                        SetResult(ResultOk, BeginMediaAccess(parameters[0], 0));
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
                    Trace($"DRIVE STATUS result=${result:X2} commandDrive={(command >> 6) & 0x03} selectedDrive={selectedDrive}");
                    RequestNmi();
                    break;

                case 0x35:
                    SetResult(ResultOk);
                    break;

                case 0x3A:
                    specialRegisters[parameters[0] & 0x3F] = parameters[1];
                    Trace($"SPECIAL WRITE reg=${parameters[0] & 0x3F:X2} value=${parameters[1]:X2}");
                    SetResult(ResultOk);
                    break;

                case 0x3D:
                    int specialRegister = parameters[0] & 0x3F;
                    result = specialRegisters[specialRegister];
                    resultAvailable = true;
                    busy = false;
                    Trace($"SPECIAL READ reg=${specialRegister:X2} result=${result:X2}");
                    RequestNmi();
                    break;

                default:
                    SetResult(ResultCommandError);
                    break;
            }
        }

        /// <summary>Queues sector data from the mounted DFS image into the controller read FIFO.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="sectorSize">The sector size in bytes value.</param>
        /// <param name="count">The count value.</param>
        private void ReadSectors(int track, int sector, int sectorSize, int count)
        {
            if (!IsDriveReady(selectedDrive))
            {
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

            readLedActive = readData.Count > 0;
            Trace($"READ queued drive={selectedDrive} track={track} sector={sector} size={sectorSize} count={count} bytes={readData.Count}");
            RequestNmi(BeginMediaAccess(track, sector));
        }

        /// <summary>Simulates an 8271 scan command by validating the requested sector range.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="sectorSize">The sector size in bytes value.</param>
        /// <param name="count">The count value.</param>
        private void ScanSectors(int track, int sector, int sectorSize, int count)
        {

            if (!IsDriveReady(selectedDrive))
            {
                SetResult(ResultDriveNotReady);
                return;
            }

            int currentTrack = track;
            int currentSector = sector;
            for (int sectorIndex = 0; sectorIndex < count; sectorIndex++)
            {
                if (!TryGetOffset(selectedDrive, currentTrack, currentSector, out int offset) || offset + sectorSize > drives[selectedDrive].Length)
                {
                    SetResult(ResultSectorNotFound);
                    return;
                }

                AdvanceSector(ref currentTrack, ref currentSector);
            }

            SetResult(ResultOk, BeginMediaAccess(track, sector));
        }

        /// <summary>Collects target sector offsets and prepares the controller for a write-data transfer.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="sectorSize">The sector size in bytes value.</param>
        /// <param name="count">The count value.</param>
        private void PrepareWrite(int track, int sector, int sectorSize, int count)
        {

            if (!IsDriveReady(selectedDrive))
            {
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
                    SetResult(ResultSectorNotFound);
                    return;
                }

                offsets.Add(offset);
                AdvanceSector(ref currentTrack, ref currentSector);
            }

            pendingWrite = new PendingWrite(selectedDrive, offsets.ToArray(), sectorSize, sectorSize * count);
            writeData.Clear();
            Trace($"WRITE prepared drive={selectedDrive} track={track} sector={sector} size={sectorSize} count={count}");
            RequestNmi(BeginMediaAccess(track, sector));
        }

        /// <summary>Commits a completed write transfer into the mounted DFS image buffers.</summary>
        /// <param name="write">The write value.</param>
        /// <param name="bytes">The bytes value.</param>
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
        }

        /// <summary>Queues synthetic sector ID records for the requested DFS track.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="count">The count value.</param>
        private void ReadSectorIds(int track, int count)
        {

            if (!IsDriveReady(selectedDrive))
            {
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

            readLedActive = readData.Count > 0;
            Trace($"READID queued drive={selectedDrive} track={track} count={sectorCount} bytes={readData.Count}");
            RequestNmi(BeginMediaAccess(track, 0));
        }

        /// <summary>Checks whether the selected DFS sector exists in drive 0.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <returns>True when sector is available; otherwise, false.</returns>
        private bool HasSector(int track, int sector)
        {
            return TryGetOffset(selectedDrive, track, sector, out int offset) && offset + SectorSize <= drives[selectedDrive].Length;
        }

        /// <summary>Validates that a sector exists and returns the corresponding 8271 result code.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        private void VerifySector(int track, int sector)
        {
            if (!IsDriveReady(selectedDrive))
            {
                SetResult(ResultDriveNotReady);
                return;
            }

            SetResult(HasSector(track, sector) ? ResultOk : ResultSectorNotFound, BeginMediaAccess(track, sector));
        }

        /// <summary>Starts a timed disc access, including motor spin-up, seek, settle, and rotational latency.</summary>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <returns>The resulting value.</returns>
        private int BeginMediaAccess(int track, int sector)
        {
            int delayCycles = 0;

            if (!motorSpinning[selectedDrive])
            {
                motorSpinning[selectedDrive] = true;
                motorStartedAtCycle[selectedDrive] = elapsedCycles;
                delayCycles += MotorSpinUpCycles;
            }

            int seekTracks = Math.Abs(track - currentTrack[selectedDrive]);
            if (seekTracks > 0)
            {
                delayCycles += (seekTracks * TrackToTrackSeekCycles) + HeadSettleCycles;
                currentTrack[selectedDrive] = track;
            }

            motorIdleCycles = MotorSpinDownCycles;
            return delayCycles + GetRotationalLatencyCycles(selectedDrive, sector, elapsedCycles + delayCycles);
        }

        /// <summary>Computes the cycle delay until the requested sector rotates under the head.</summary>
        /// <param name="drive">The drive number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="readyAtCycle">The ready at cycle value.</param>
        /// <returns>The computed value.</returns>
        private int GetRotationalLatencyCycles(int drive, int sector, long readyAtCycle)
        {
            int physicalSector = Math.Clamp(sector, 0, SectorsPerTrack - 1);
            long phase = (readyAtCycle - motorStartedAtCycle[drive]) % RevolutionCycles;
            if (phase < 0)
                phase += RevolutionCycles;

            int targetPhase = physicalSector * SectorTransferCycles;
            return (int)((targetPhase - phase + RevolutionCycles) % RevolutionCycles);
        }

        /// <summary>Translates a DFS drive, track, and sector into an image byte offset.</summary>
        /// <param name="drive">The drive number value.</param>
        /// <param name="track">The disc track number value.</param>
        /// <param name="sector">The sector number value.</param>
        /// <param name="offset">The buffer or image offset.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Stores an 8271 command result and schedules the completion interrupt.</summary>
        /// <param name="value">The input value.</param>
        /// <param name="nmiDelayCycles">The NMI delay cycles value.</param>
        private void SetResult(byte value, int nmiDelayCycles = 0)
        {
            result = value;
            resultAvailable = true;
            busy = false;
            Trace($"RESULT ${value:X2} delay={nmiDelayCycles} drive={selectedDrive} readBytes={readData.Count} pendingWrite={pendingWrite is not null}");
            RequestNmi(nmiDelayCycles);
        }

        /// <summary>Schedules or raises a disc NMI to notify the CPU of controller progress.</summary>
        /// <param name="delayCycles">The delay cycles value.</param>
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

        /// <summary>Checks whether a mounted drive has spun up and is ready for media access.</summary>
        /// <param name="drive">The drive number value.</param>
        /// <returns>True when drive ready is true; otherwise, false.</returns>
        private bool IsDriveReady(int drive)
        {
            return drive >= 0 && drive < drives.Length && driveMounted[drive] && drives[drive].Length > 0;
        }

        /// <summary>Writes one controller diagnostic event when tracing is enabled.</summary>
        /// <param name="message">The diagnostic message.</param>
        private void Trace(string message)
        {
            traceWriter?.WriteLine($"{elapsedCycles,12} {message}");
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

        /// <summary>Infers the BASIC command that should auto-run the mounted DFS image.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Reads the DFS catalogue entries from the mounted drive 0 image.</summary>
        /// <param name="files">The files value.</param>
        /// <returns>True when the value was read or handled successfully; otherwise, false.</returns>
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

        /// <summary>Reads the DFS catalogue boot option from the mounted image.</summary>
        /// <returns>The computed value.</returns>
        private int GetBootOption()
        {
            if (!HasMountedDisc || drives[0].Length <= 0x106)
                return 0;

            return (drives[0][0x106] >> 4) & 0x03;
        }

        /// <summary>Checks catalogue metadata for a file shape that should be run from BASIC.</summary>
        /// <param name="file">The file value.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private bool LooksLikeBasicFile(DfsFile file)
        {
            int offset = file.StartSector * SectorSize;
            return file.LoadAddress == 0x1900
                || file.LoadAddress == 0x1D00
                || (offset + 2 < drives[0].Length && drives[0][offset] == 0x0D && drives[0][offset + 1] == 0x00);
        }

        /// <summary>Returns how many parameter bytes the current 8271 command expects.</summary>
        /// <param name="command">The command value.</param>
        /// <returns>The computed value.</returns>
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

        /// <summary>Decodes the byte size represented by an 8271 sector-size code.</summary>
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

        /// <summary>Decodes the sector-count field from an 8271 size/count parameter byte.</summary>
        /// <param name="sectorSizeAndCount">The sector size and count value.</param>
        /// <returns>The computed value.</returns>
        private static int GetSectorCount(byte sectorSizeAndCount)
        {
            int count = sectorSizeAndCount & 0x1F;
            return count == 0 ? 32 : count;
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

        private readonly record struct PendingWrite(int Drive, int[] Offsets, int SectorSize, int Length);

        private sealed record DfsFile(string Name, int LoadAddress, int ExecutionAddress, int Length, int StartSector);
    }
}
