// ============================================================================
// Project:     BBC
// File:        WD1770_Disk.cs
// Description: Acorn Model B WD1770 disc interface using DFS SSD/DSD images.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// ============================================================================

namespace BBC
{
    /// <summary>
    /// Acorn's 1770 upgrade maps its drive latch at &FE80-&FE83 and the
    /// WD1770 registers at &FE84-&FE87. DRQ and INTRQ are ORed onto the BBC NMI line.
    /// </summary>
    public sealed class WD1770_Disk : IDiscController
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 10;
        private const int FmTrackBytes = 3_125;
        private const int MfmTrackBytes = 6_250;
        private const int CpuClockHz = 2_000_000;
        private const int MfmByteDelayCycles = 64;
        private const int FmByteDelayCycles = 128;
        private const int CommandDelayCycles = CpuClockHz / 500;
        private const int HeadSettleCycles = CpuClockHz * 30 / 1000;
        private const int RecordSearchCycles = IndexPeriodCycles * 5;
        private const int MotorIdleCycles = CpuClockHz * 3;
        private const int IndexPeriodCycles = CpuClockHz / 5;
        private const int IndexPulseCycles = CpuClockHz / 250;
        private const byte StatusMotorOn = 0x80;
        private const byte StatusNotReady = 0x80;
        private const byte StatusWriteProtected = 0x40;
        private const byte StatusRecordType = 0x20;
        private const byte StatusSpinUpComplete = 0x20;
        private const byte StatusRecordNotFound = 0x10;
        private const byte StatusCrcError = 0x08;
        private const byte StatusTrackZero = 0x04;
        private const byte StatusLostData = 0x04;
        private const byte StatusDrq = 0x02;
        private const byte StatusBusy = 0x01;
        private const byte ControlReset = 0x20;
        private const byte ControlDensity = 0x08;
        private const byte ControlSide = 0x04;
        private const byte ControlDrive1 = 0x02;
        private const byte ControlDrive0 = 0x01;

        private readonly Intel8271_Disk media = new Intel8271_Disk();
        private readonly Queue<byte> readData = new Queue<byte>();
        private readonly List<byte> writeData = new List<byte>(SectorSize * SectorsPerTrack);
        private readonly HashSet<int> deletedSectors = new HashSet<int>();
        private byte control;
        private byte status;
        private byte track;
        private byte sector = 1;
        private byte data;
        private byte command;
        private bool interruptRequest;
        private bool dataRequest;
        private int requestDelayCycles;
        private int dataRequestDeadlineCycles;
        private int commandCompletionCycles;
        private byte pendingCompletionError;
        private readonly int[] motorIdleCycles = new int[2];
        private readonly int[] cyclesUntilIndex = [IndexPeriodCycles, IndexPeriodCycles];
        private readonly int[] physicalTrack = new int[2];
        private int lastStepDirection = 1;
        private bool interruptOnIndex;
        private bool interruptOnReadyLoss;
        private bool interruptOnReadyGain;
        private bool forceInterruptReady;
        private bool readTransferActive;
        private int transferBytes;
        private PendingWrite? pendingWrite;
        private bool writeTrackActive;
        private int trackTransferLength;
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_1770_TRACE") == "1";

        public WD1770_Disk()
        {
            media.AdfImagesEnabled = true;
            Reset();
        }

        public bool HasMountedDisc => media.HasMountedDisc;
        public string? MountedFileName => media.MountedFileName;
        public bool ImageDirty => media.ImageDirty;
        public bool MountedMediaIsAdfs => media.MountedMediaIsAdfs;
        public string MountedDriveSummary => media.MountedDriveSummary;
        public bool NmiLineAsserted => interruptRequest || dataRequest;
        public bool TickRequired => requestDelayCycles > 0 || dataRequestDeadlineCycles > 0
            || commandCompletionCycles > 0 || motorIdleCycles.Any(value => value > 0)
            || interruptOnReadyLoss || interruptOnReadyGain;

        public event Action<int>? DriveMotorStarted;
        public event Action<int>? DriveMotorStopped;
        public event Action<int, int>? DriveSeek;

        private int SelectedPhysicalDrive => (control & ControlDrive0) != 0 ? 0 : (control & ControlDrive1) != 0 ? 1 : -1;
        private int SelectedImageSide => SelectedPhysicalDrive < 0 ? -1 : SelectedPhysicalDrive + ((control & ControlSide) != 0 ? 2 : 0);
        private bool ControllerReleasedFromReset => (control & ControlReset) != 0;
        private bool MultiSector => (command & 0x10) != 0;
        private int ByteDelayCycles => (control & ControlDensity) != 0 ? FmByteDelayCycles : MfmByteDelayCycles;
        private int CurrentSectorsPerTrack => media.GetRawSectorsPerTrack(SelectedImageSide);

        public static bool IsAddress(ushort address) => address is >= 0xFE80 and <= 0xFE87;

        public void Mount(string path, int drive = 0)
        {
            ClearDeletedSectors(drive);
            media.Mount(path, drive);
            UpdateReadyInterrupt();
        }

        public void MountImage(byte[] image, int drive, string? sourcePath, string displayName, bool readOnly)
        {
            ClearDeletedSectors(drive);
            media.MountImage(image, drive, sourcePath, displayName, readOnly);
            UpdateReadyInterrupt();
        }

        public void EjectPhysicalDrive(int drive)
        {
            ClearDeletedSectors(drive);
            media.EjectPhysicalDrive(drive);
            UpdateReadyInterrupt();
        }
        public bool Flush() => media.Flush();
        public bool IsPhysicalDriveMounted(int drive) => media.IsPhysicalDriveMounted(drive);
        public bool IsPhysicalDriveActivityLedActive(int drive) => media.IsPhysicalDriveActivityLedActive(drive);
        public bool IsPhysicalDriveDoubleSided(int drive) => media.IsPhysicalDriveDoubleSided(drive);
        public string? GetPhysicalDriveLabel(int drive) => media.GetPhysicalDriveLabel(drive);
        public bool TryGetBootExecScript(out string? script) => media.TryGetBootExecScript(out script);

        public void Reset()
        {
            readData.Clear();
            writeData.Clear();
            pendingWrite = null;
            writeTrackActive = false;
            trackTransferLength = 0;
            control = 0;
            status = 0;
            sector = 1;
            command = 0;
            interruptRequest = false;
            dataRequest = false;
            requestDelayCycles = 0;
            dataRequestDeadlineCycles = 0;
            commandCompletionCycles = 0;
            pendingCompletionError = 0;
            Array.Clear(motorIdleCycles);
            Array.Fill(cyclesUntilIndex, IndexPeriodCycles);
            Array.Clear(physicalTrack);
            lastStepDirection = 1;
            interruptOnIndex = false;
            interruptOnReadyLoss = false;
            interruptOnReadyGain = false;
            forceInterruptReady = false;
            readTransferActive = false;
            transferBytes = 0;
            media.SetRawActivityLed(0, false);
            media.SetRawActivityLed(1, false);
        }

        public void PowerOff()
        {
            for (int drive = 0; drive < motorIdleCycles.Length; drive++)
            {
                if (motorIdleCycles[drive] > 0)
                    DriveMotorStopped?.Invoke(drive);
            }
            Reset();
        }

        public byte Read(ushort address)
        {
            return (address & 7) switch
            {
                4 => ReadStatus(),
                5 => track,
                6 => sector,
                7 => ReadData(),
                _ => 0xFE
            };
        }

        public void Write(ushort address, byte value)
        {
            switch (address & 7)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    WriteControl(value);
                    break;
                case 4:
                    if (ControllerReleasedFromReset)
                        BeginCommand(value);
                    break;
                case 5:
                    track = value;
                    break;
                case 6:
                    if (ControllerReleasedFromReset)
                        sector = value;
                    break;
                case 7:
                    WriteData(value);
                    break;
            }
        }

        private byte ReadStatus()
        {
            interruptRequest = false;
            byte value = status;
            bool typeOneStatus = (command & 0x80) == 0 || (command & 0xF0) == 0xD0;
            if (typeOneStatus)
            {
                int drive = SelectedPhysicalDrive;
                value &= unchecked((byte)~(StatusMotorOn | StatusWriteProtected
                    | StatusSpinUpComplete | StatusTrackZero | StatusDrq));
                if (drive >= 0 && motorIdleCycles[drive] > 0 && cyclesUntilIndex[drive] <= IndexPulseCycles)
                    value |= StatusDrq;
                if (drive >= 0 && physicalTrack[drive] == 0)
                    value |= StatusTrackZero;
                if (drive >= 0 && motorIdleCycles[drive] > 0)
                    value |= StatusMotorOn | StatusSpinUpComplete;
                if (media.RawWriteProtected)
                    value |= StatusWriteProtected;
            }
            else
            {
                if (!IsReady(SelectedImageSide))
                    value |= StatusNotReady;
                if (media.RawWriteProtected)
                    value |= StatusWriteProtected;
            }
            return value;
        }

        private byte ReadData()
        {
            if (!dataRequest || !readTransferActive)
                return data;

            dataRequest = false;
            dataRequestDeadlineCycles = 0;
            status &= unchecked((byte)~StatusDrq);
            transferBytes++;
            if (MultiSector && transferBytes % SectorSize == 0)
                sector++;
            if (readData.Count > 0)
                requestDelayCycles = ByteDelayCycles;
            else
                commandCompletionCycles = ByteDelayCycles;
            return data;
        }

        private void WriteControl(byte value)
        {
            control = value;
            if (TraceEnabled)
                Console.WriteLine($"1770 control ${value:X2} drive {SelectedPhysicalDrive} side {((value & ControlSide) != 0 ? 1 : 0)}");

            if (!ControllerReleasedFromReset)
            {
                readData.Clear();
                writeData.Clear();
                pendingWrite = null;
                writeTrackActive = false;
                status = 0;
                sector = 1;
                interruptRequest = false;
                dataRequest = false;
                requestDelayCycles = 0;
                dataRequestDeadlineCycles = 0;
                commandCompletionCycles = 0;
                pendingCompletionError = 0;
                for (int drive = 0; drive < motorIdleCycles.Length; drive++)
                {
                    if (motorIdleCycles[drive] > 0)
                        DriveMotorStopped?.Invoke(drive);
                }
                Array.Clear(motorIdleCycles);
            }
            UpdateReadyInterrupt();
        }

        private void BeginCommand(byte value)
        {
            if (TraceEnabled)
                Console.WriteLine($"1770 command ${value:X2} track {track} sector {sector} data {data} status ${status:X2}");
            if ((value & 0xF0) == 0xD0)
            {
                command = value;
                AbortCommand(value);
                return;
            }

            if ((status & StatusBusy) != 0)
                return;

            command = value;
            interruptOnIndex = false;
            interruptOnReadyLoss = false;
            interruptOnReadyGain = false;
            interruptRequest = false;
            dataRequest = false;
            requestDelayCycles = 0;
            dataRequestDeadlineCycles = 0;
            commandCompletionCycles = 0;
            pendingCompletionError = 0;
            readTransferActive = false;
            transferBytes = 0;
            status = StatusBusy;
            StartMotor();

            switch (value & 0xF0)
            {
                case 0x00:
                    SeekTo(0, updateTrackRegister: true);
                    break;
                case 0x10:
                    SeekTo(data, updateTrackRegister: true);
                    break;
                case 0x20:
                case 0x30:
                    SeekTo(GetSelectedPhysicalTrack() + lastStepDirection, (value & 0x10) != 0);
                    break;
                case 0x40:
                case 0x50:
                    lastStepDirection = 1;
                    SeekTo(GetSelectedPhysicalTrack() + 1, (value & 0x10) != 0);
                    break;
                case 0x60:
                case 0x70:
                    lastStepDirection = -1;
                    SeekTo(GetSelectedPhysicalTrack() - 1, (value & 0x10) != 0);
                    break;
                case 0x80:
                case 0x90:
                    BeginReadSector();
                    break;
                case 0xA0:
                case 0xB0:
                    BeginWriteSector();
                    break;
                case 0xC0:
                    BeginReadAddress();
                    break;
                case 0xE0:
                    BeginReadTrack();
                    break;
                case 0xF0:
                    BeginWriteTrack();
                    break;
                default:
                    FailCommand(StatusRecordNotFound);
                    break;
            }
        }

        private void SeekTo(int destination, bool updateTrackRegister)
        {
            int drive = SelectedPhysicalDrive;
            int currentPhysicalTrack = drive >= 0 ? physicalTrack[drive] : 0;
            int bounded = Math.Clamp(destination, 0, 79);
            int delta = bounded - currentPhysicalTrack;
            if (delta != 0)
            {
                if (TraceEnabled)
                    Console.WriteLine($"1770 seek drive {drive} {currentPhysicalTrack}->{bounded}, rate {GetStepRateCycles() * 1000 / CpuClockHz}ms");
                if (drive >= 0)
                    DriveSeek?.Invoke(drive, delta);
            }
            if (drive >= 0)
                physicalTrack[drive] = bounded;
            if (updateTrackRegister)
                track = (byte)bounded;
            int seekCycles = Math.Max(CommandDelayCycles, Math.Abs(delta) * GetStepRateCycles());
            if ((command & 0x04) != 0)
            {
                bool trackIdMatches = drive >= 0 && IsReady(SelectedImageSide) && track == physicalTrack[drive]
                    && media.TryReadRawSector(SelectedImageSide, physicalTrack[drive], 0, new byte[SectorSize]);
                pendingCompletionError = trackIdMatches ? (byte)0 : StatusRecordNotFound;
                seekCycles += trackIdMatches ? HeadSettleCycles : RecordSearchCycles;
            }
            commandCompletionCycles = seekCycles;
        }

        private int GetStepRateCycles()
        {
            int milliseconds = (command & 0x03) switch
            {
                0 => 6,
                1 => 12,
                2 => 20,
                _ => 30
            };
            return milliseconds * (CpuClockHz / 1000);
        }

        private void BeginReadSector()
        {
            int imageDrive = SelectedImageSide;
            if (!IsReady(imageDrive))
            {
                FailCommand(StatusNotReady);
                return;
            }

            byte[] buffer = new byte[SectorSize];
            int finalSector = MultiSector ? CurrentSectorsPerTrack - 1 : sector;
            for (int current = sector; current <= finalSector; current++)
            {
                if (!media.TryReadRawSector(imageDrive, track, current, buffer))
                {
                    BeginRecordSearchFailure();
                    return;
                }
                foreach (byte value in buffer)
                    readData.Enqueue(value);
                if (deletedSectors.Contains(GetSectorKey(imageDrive, track, current)))
                    status |= StatusRecordType;
            }

            media.SetRawActivityLed(SelectedPhysicalDrive, true);
            readTransferActive = true;
            transferBytes = 0;
            requestDelayCycles = GetRecordStartDelayCycles();
        }

        private void BeginWriteSector()
        {
            int imageDrive = SelectedImageSide;
            if (!IsReady(imageDrive))
            {
                FailCommand(StatusNotReady);
                return;
            }
            if (media.RawWriteProtected)
            {
                FailCommand(StatusWriteProtected);
                return;
            }
            if (!media.TryReadRawSector(imageDrive, track, sector, new byte[SectorSize]))
            {
                BeginRecordSearchFailure();
                return;
            }

            int count = MultiSector ? CurrentSectorsPerTrack - sector : 1;
            pendingWrite = new PendingWrite(imageDrive, track, sector, Math.Max(1, count), (command & 0x01) != 0);
            writeData.Clear();
            readTransferActive = false;
            transferBytes = 0;
            media.SetRawActivityLed(SelectedPhysicalDrive, true);
            requestDelayCycles = GetRecordStartDelayCycles();
        }

        private void BeginReadAddress()
        {
            if (!IsReady(SelectedImageSide))
            {
                FailCommand(StatusNotReady);
                return;
            }

            readData.Enqueue(track);
            readData.Enqueue((byte)((control & ControlSide) != 0 ? 1 : 0));
            readData.Enqueue(sector < CurrentSectorsPerTrack ? sector : (byte)0);
            readData.Enqueue(1); // 256-byte sector length code.
            readData.Enqueue(0);
            readData.Enqueue(0);
            readTransferActive = true;
            transferBytes = 0;
            sector = track;
            requestDelayCycles = GetRecordStartDelayCycles();
        }

        private void BeginReadTrack()
        {
            int imageDrive = SelectedImageSide;
            if (!IsReady(imageDrive))
            {
                FailCommand(StatusNotReady);
                return;
            }

            int side = (control & ControlSide) != 0 ? 1 : 0;
            int requiredLength = GetTrackTransferLength();
            AddRepeated(readData, 0xFF, 40);
            byte[] sectorData = new byte[SectorSize];
            for (int currentSector = 0; currentSector < CurrentSectorsPerTrack; currentSector++)
            {
                if (!media.TryReadRawSector(imageDrive, track, currentSector, sectorData))
                {
                    FailCommand(StatusRecordNotFound);
                    return;
                }

                AddRepeated(readData, 0x00, 6);
                byte[] id = [(byte)0xFE, track, (byte)side, (byte)currentSector, 1];
                foreach (byte value in id)
                    readData.Enqueue(value);
                ushort idCrc = CalculateCrc(id);
                readData.Enqueue((byte)(idCrc >> 8));
                readData.Enqueue((byte)idCrc);
                AddRepeated(readData, 0xFF, 11);
                AddRepeated(readData, 0x00, 6);
                byte dataMark = deletedSectors.Contains(GetSectorKey(imageDrive, track, currentSector))
                    ? (byte)0xF8 : (byte)0xFB;
                readData.Enqueue(dataMark);
                foreach (byte value in sectorData)
                    readData.Enqueue(value);
                byte[] crcData = new byte[SectorSize + 1];
                crcData[0] = dataMark;
                sectorData.CopyTo(crcData, 1);
                ushort dataCrc = CalculateCrc(crcData);
                readData.Enqueue((byte)(dataCrc >> 8));
                readData.Enqueue((byte)dataCrc);
                AddRepeated(readData, 0xFF, 19);
            }
            AddRepeated(readData, 0xFF, Math.Max(0, requiredLength - readData.Count));
            while (readData.Count > requiredLength)
                readData.Dequeue();

            media.SetRawActivityLed(SelectedPhysicalDrive, true);
            readTransferActive = true;
            transferBytes = 0;
            requestDelayCycles = GetRecordStartDelayCycles();
        }

        private void BeginWriteTrack()
        {
            int imageDrive = SelectedImageSide;
            if (!IsReady(imageDrive))
            {
                FailCommand(StatusNotReady);
                return;
            }
            if (media.RawWriteProtected)
            {
                FailCommand(StatusWriteProtected);
                return;
            }

            writeTrackActive = true;
            trackTransferLength = GetTrackTransferLength();
            writeData.Clear();
            readTransferActive = false;
            transferBytes = 0;
            media.SetRawActivityLed(SelectedPhysicalDrive, true);
            requestDelayCycles = GetRecordStartDelayCycles();
        }

        private void WriteData(byte value)
        {
            data = value;
            if (!dataRequest)
                return;

            dataRequest = false;
            dataRequestDeadlineCycles = 0;
            status &= unchecked((byte)~StatusDrq);

            AcceptWriteByte(value);
        }

        private void AcceptWriteByte(byte value)
        {

            if (writeTrackActive)
            {
                writeData.Add(value);
                if (writeData.Count < trackTransferLength)
                {
                    requestDelayCycles = ByteDelayCycles;
                    return;
                }

                FinishWriteTrack();
                return;
            }

            if (pendingWrite is null)
                return;

            writeData.Add(value);
            transferBytes++;
            if (MultiSector && transferBytes % SectorSize == 0)
                sector++;
            int required = pendingWrite.Value.Count * SectorSize;
            if (writeData.Count < required)
            {
                requestDelayCycles = ByteDelayCycles;
                return;
            }

            PendingWrite write = pendingWrite.Value;
            for (int i = 0; i < write.Count; i++)
            {
                if (!media.TryWriteRawSector(write.Drive, write.Track, write.FirstSector + i,
                        writeData.GetRange(i * SectorSize, SectorSize).ToArray()))
                {
                    FailCommand(StatusWriteProtected);
                    return;
                }
                int key = GetSectorKey(write.Drive, write.Track, write.FirstSector + i);
                if (write.Deleted)
                    deletedSectors.Add(key);
                else
                    deletedSectors.Remove(key);
            }
            media.Flush();
            commandCompletionCycles = ByteDelayCycles;
        }

        private void FinishWriteTrack()
        {
            int imageDrive = SelectedImageSide;
            int sectorsWritten = 0;
            bool crcError = false;
            for (int i = 0; i + 7 < writeData.Count; i++)
            {
                if (writeData[i] != 0xFE)
                    continue;

                int idTrack = writeData[i + 1];
                int idSide = writeData[i + 2];
                int idSector = writeData[i + 3];
                int sizeCode = writeData[i + 4];
                if (idTrack != track || idSide != ((control & ControlSide) != 0 ? 1 : 0)
                    || idSector is < 0 || idSector >= CurrentSectorsPerTrack || sizeCode != 1)
                    continue;
                if (!TrackFieldCrcIsValid(i, 5))
                {
                    crcError = true;
                    continue;
                }

                int marker = -1;
                int searchEnd = Math.Min(writeData.Count, i + 96);
                for (int j = i + 7; j < searchEnd; j++)
                {
                    if (writeData[j] is 0xF8 or 0xF9 or 0xFA or 0xFB)
                    {
                        marker = j;
                        break;
                    }
                }
                if (marker < 0 || marker + 1 + SectorSize > writeData.Count)
                    continue;
                if (!TrackFieldCrcIsValid(marker, SectorSize + 1))
                {
                    crcError = true;
                    continue;
                }

                byte[] sectorData = writeData.GetRange(marker + 1, SectorSize).ToArray();
                if (media.TryWriteRawSector(imageDrive, idTrack, idSector, sectorData))
                {
                    sectorsWritten++;
                    int key = GetSectorKey(imageDrive, idTrack, idSector);
                    if (writeData[marker] == 0xF8)
                        deletedSectors.Add(key);
                    else
                        deletedSectors.Remove(key);
                }
                i = marker + SectorSize;
            }

            writeTrackActive = false;
            trackTransferLength = 0;
            if (sectorsWritten == 0)
            {
                FailCommand(crcError ? StatusCrcError : StatusRecordNotFound);
                return;
            }

            if (crcError)
                status |= StatusCrcError;
            media.Flush();
            commandCompletionCycles = ByteDelayCycles;
        }

        private int GetTrackTransferLength() => (control & ControlDensity) != 0 ? FmTrackBytes : MfmTrackBytes;

        private int GetRecordStartDelayCycles() => CommandDelayCycles
            + ((command & 0x04) != 0 ? HeadSettleCycles : 0);

        private void BeginRecordSearchFailure()
        {
            pendingCompletionError = StatusRecordNotFound;
            commandCompletionCycles = RecordSearchCycles;
            media.SetRawActivityLed(SelectedPhysicalDrive, true);
        }

        private static void AddRepeated(Queue<byte> destination, byte value, int count)
        {
            for (int i = 0; i < count; i++)
                destination.Enqueue(value);
        }

        private static ushort CalculateCrc(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0xFFFF;
            foreach (byte value in bytes)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        private bool TrackFieldCrcIsValid(int marker, int fieldLength)
        {
            int crcOffset = marker + fieldLength;
            if (crcOffset >= writeData.Count)
                return false;
            if (writeData[crcOffset] == 0xF7)
                return true; // WRITE TRACK uses F7 to ask the WD1770 to emit the current CRC.
            if (crcOffset + 1 >= writeData.Count)
                return false;
            // Acorn FORM40 reserves two zero bytes for CRC fields in its FM track template.
            // The physical controller fills these while writing the address and data fields.
            if (writeData[crcOffset] == 0 && writeData[crcOffset + 1] == 0)
                return true;

            ushort expected = CalculateCrc(writeData.GetRange(marker, fieldLength).ToArray());
            bool valid = writeData[crcOffset] == (byte)(expected >> 8)
                && writeData[crcOffset + 1] == (byte)expected;
            if (!valid && TraceEnabled)
                Console.WriteLine($"1770 CRC mismatch at track offset {marker}: "
                    + $"${writeData[crcOffset]:X2}${writeData[crcOffset + 1]:X2}, expected ${expected:X4}");
            return valid;
        }

        private static int GetSectorKey(int drive, int track, int sector) => (drive << 16) | (track << 8) | sector;

        private void ClearDeletedSectors(int physicalDrive)
        {
            deletedSectors.RemoveWhere(key => ((key >> 16) & 1) == physicalDrive);
        }

        private bool IsReady(int imageDrive) => imageDrive >= 0
            && media.IsPhysicalDriveMounted(imageDrive & 1)
            && (imageDrive < 2 || media.IsPhysicalDriveDoubleSided(imageDrive & 1));

        private int GetSelectedPhysicalTrack()
        {
            int drive = SelectedPhysicalDrive;
            return drive >= 0 ? physicalTrack[drive] : 0;
        }

        private void UpdateReadyInterrupt()
        {
            if (!interruptOnReadyLoss && !interruptOnReadyGain)
                return;

            bool ready = IsReady(SelectedImageSide);
            if ((forceInterruptReady && !ready && interruptOnReadyLoss)
                || (!forceInterruptReady && ready && interruptOnReadyGain))
            {
                interruptRequest = true;
                interruptOnReadyLoss = false;
                interruptOnReadyGain = false;
            }
            forceInterruptReady = ready;
        }

        private void StartMotor()
        {
            int drive = SelectedPhysicalDrive;
            if (drive < 0)
                return;
            if (motorIdleCycles[drive] <= 0)
            {
                if (TraceEnabled)
                    Console.WriteLine($"1770 motor start drive {drive}");
                DriveMotorStarted?.Invoke(drive);
            }
            motorIdleCycles[drive] = MotorIdleCycles;
        }

        private void CompleteCommand()
        {
            status |= pendingCompletionError;
            pendingCompletionError = 0;
            status &= unchecked((byte)~(StatusBusy | StatusDrq));
            dataRequest = false;
            dataRequestDeadlineCycles = 0;
            pendingWrite = null;
            writeTrackActive = false;
            trackTransferLength = 0;
            readTransferActive = false;
            transferBytes = 0;
            media.SetRawActivityLed(0, false);
            media.SetRawActivityLed(1, false);
            interruptRequest = true;
            if (TraceEnabled)
                Console.WriteLine($"1770 complete status ${status:X2} track {track} sector {sector}");
        }

        private void FailCommand(byte error)
        {
            status = error;
            readData.Clear();
            writeData.Clear();
            CompleteCommand();
        }

        private void AbortCommand(byte forceCommand)
        {
            readData.Clear();
            writeData.Clear();
            pendingWrite = null;
            requestDelayCycles = 0;
            dataRequestDeadlineCycles = 0;
            commandCompletionCycles = 0;
            pendingCompletionError = 0;
            status &= unchecked((byte)~(StatusBusy | StatusDrq));
            dataRequest = false;
            readTransferActive = false;
            transferBytes = 0;
            interruptRequest = (forceCommand & 0x08) != 0;
            interruptOnIndex = (forceCommand & 0x04) != 0;
            interruptOnReadyLoss = (forceCommand & 0x02) != 0;
            interruptOnReadyGain = (forceCommand & 0x01) != 0;
            forceInterruptReady = IsReady(SelectedImageSide);
            media.SetRawActivityLed(0, false);
            media.SetRawActivityLed(1, false);
        }

        public void Tick(int cycles)
        {
            UpdateReadyInterrupt();
            bool transferAdvanced = false;
            if (requestDelayCycles > 0)
            {
                requestDelayCycles -= cycles;
                if (requestDelayCycles <= 0)
                {
                    requestDelayCycles = 0;
                    if (readTransferActive && readData.Count > 0)
                        data = readData.Dequeue();
                    dataRequest = true;
                    status |= StatusDrq;
                    // A sector image has no following address mark with which to time an abandoned
                    // read. DFS can deliberately leave bytes unread before starting another command.
                    dataRequestDeadlineCycles = readTransferActive || writeTrackActive ? 0 : ByteDelayCycles;
                    transferAdvanced = true;
                }
            }

            if (!transferAdvanced && dataRequestDeadlineCycles > 0)
            {
                dataRequestDeadlineCycles -= cycles;
                if (dataRequestDeadlineCycles <= 0 && dataRequest)
                {
                    dataRequestDeadlineCycles = 0;
                    dataRequest = false;
                    status = (byte)((status & ~StatusDrq) | StatusLostData);
                    AcceptWriteByte(0);
                    transferAdvanced = true;
                }
            }

            if (!transferAdvanced && commandCompletionCycles > 0)
            {
                commandCompletionCycles -= cycles;
                if (commandCompletionCycles <= 0)
                {
                    commandCompletionCycles = 0;
                    CompleteCommand();
                }
            }

            for (int drive = 0; drive < motorIdleCycles.Length; drive++)
            {
                if (motorIdleCycles[drive] <= 0)
                    continue;

                cyclesUntilIndex[drive] -= cycles;
                while (cyclesUntilIndex[drive] <= 0)
                {
                    cyclesUntilIndex[drive] += IndexPeriodCycles;
                    if (interruptOnIndex && drive == SelectedPhysicalDrive)
                    {
                        interruptRequest = true;
                        interruptOnIndex = false;
                    }
                }

                motorIdleCycles[drive] -= cycles;
                if (motorIdleCycles[drive] <= 0)
                {
                    motorIdleCycles[drive] = 0;
                    cyclesUntilIndex[drive] = IndexPeriodCycles;
                    if (TraceEnabled)
                        Console.WriteLine($"1770 motor stop drive {drive}");
                    DriveMotorStopped?.Invoke(drive);
                }
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            media.SaveState(writer);
            writer.Write(control);
            writer.Write(status);
            writer.Write(track);
            writer.Write(sector);
            writer.Write(data);
            writer.Write(command);
            writer.Write(interruptRequest);
            writer.Write(dataRequest);
            writer.Write(requestDelayCycles);
            writer.Write(dataRequestDeadlineCycles);
            writer.Write(commandCompletionCycles);
            writer.Write(pendingCompletionError);
            for (int drive = 0; drive < 2; drive++)
            {
                writer.Write(motorIdleCycles[drive]);
                writer.Write(cyclesUntilIndex[drive]);
                writer.Write(physicalTrack[drive]);
            }
            writer.Write(lastStepDirection);
            writer.Write(interruptOnIndex);
            writer.Write(interruptOnReadyLoss);
            writer.Write(interruptOnReadyGain);
            writer.Write(forceInterruptReady);
            writer.Write(readTransferActive);
            writer.Write(transferBytes);
            writer.Write(readData.Count);
            foreach (byte value in readData)
                writer.Write(value);
            writer.Write(writeData.Count);
            foreach (byte value in writeData)
                writer.Write(value);
            writer.Write(pendingWrite.HasValue);
            if (pendingWrite.HasValue)
            {
                writer.Write(pendingWrite.Value.Drive);
                writer.Write(pendingWrite.Value.Track);
                writer.Write(pendingWrite.Value.FirstSector);
                writer.Write(pendingWrite.Value.Count);
                writer.Write(pendingWrite.Value.Deleted);
            }
            writer.Write(writeTrackActive);
            writer.Write(trackTransferLength);
            writer.Write(deletedSectors.Count);
            foreach (int key in deletedSectors)
                writer.Write(key);
        }

        public void LoadState(BinaryReader reader)
        {
            media.LoadState(reader);
            control = reader.ReadByte();
            status = reader.ReadByte();
            track = reader.ReadByte();
            sector = reader.ReadByte();
            data = reader.ReadByte();
            command = reader.ReadByte();
            interruptRequest = reader.ReadBoolean();
            dataRequest = reader.ReadBoolean();
            requestDelayCycles = reader.ReadInt32();
            dataRequestDeadlineCycles = reader.ReadInt32();
            commandCompletionCycles = reader.ReadInt32();
            pendingCompletionError = reader.ReadByte();
            for (int drive = 0; drive < 2; drive++)
            {
                motorIdleCycles[drive] = reader.ReadInt32();
                cyclesUntilIndex[drive] = reader.ReadInt32();
                physicalTrack[drive] = reader.ReadInt32();
            }
            lastStepDirection = reader.ReadInt32();
            interruptOnIndex = reader.ReadBoolean();
            interruptOnReadyLoss = reader.ReadBoolean();
            interruptOnReadyGain = reader.ReadBoolean();
            forceInterruptReady = reader.ReadBoolean();
            readTransferActive = reader.ReadBoolean();
            transferBytes = reader.ReadInt32();
            readData.Clear();
            int readCount = reader.ReadInt32();
            for (int i = 0; i < readCount; i++)
                readData.Enqueue(reader.ReadByte());
            writeData.Clear();
            int writeCount = reader.ReadInt32();
            for (int i = 0; i < writeCount; i++)
                writeData.Add(reader.ReadByte());
            pendingWrite = reader.ReadBoolean()
                ? new PendingWrite(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean())
                : null;
            writeTrackActive = reader.ReadBoolean();
            trackTransferLength = reader.ReadInt32();
            deletedSectors.Clear();
            int deletedCount = reader.ReadInt32();
            for (int i = 0; i < deletedCount; i++)
                deletedSectors.Add(reader.ReadInt32());
        }

        public void SaveMediaState(BinaryWriter writer) => media.SaveState(writer);

        public void LoadMediaState(BinaryReader reader) => media.LoadState(reader);

        private readonly record struct PendingWrite(int Drive, int Track, int FirstSector, int Count, bool Deleted);
    }
}
