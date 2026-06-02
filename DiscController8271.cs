// ============================================================================
// Project:     BBC
// File:        DiscController8271.cs
// Description: Minimal Intel 8271-compatible controller backed by DFS SSD images.
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
    /// Provides a small 8271 FDC surface for Acorn DFS ROM access to SSD images.
    /// </summary>
    public sealed class DiscController8271
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 10;
        private const byte StatusDataRequest = 0x04;
        private const byte StatusInterrupt = 0x08;
        private const byte StatusResultFull = 0x10;
        private readonly byte[][] drives = [[], [], [], []];
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

        /// <summary>Gets whether a disc image is mounted in drive 0.</summary>
        public bool HasMountedDisc => drives[0].Length > 0;

        /// <summary>Gets the currently mounted host image path.</summary>
        public string? MountedPath => mountedPath;

        /// <summary>Gets the command that should be typed at BASIC after mounting.</summary>
        public string? AutoLoadCommand => HasMountedDisc && TryGetFirstCatalogueFile(out string? name) ? $"LOAD \"{name}\"" : null;

        /// <summary>Returns whether an address belongs to the 8271 FDC.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True for &amp;FE80-&amp;FE84.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= 0xFE80 and <= 0xFE84;
        }

        /// <summary>Mounts an SSD/DSD image in drive 0.</summary>
        /// <param name="path">The host image path.</param>
        public void Mount(string path)
        {
            string fullPath = Path.GetFullPath(path);
            byte[] image = File.ReadAllBytes(fullPath);

            if (image.Length < 512 || image.Length % SectorSize != 0)
                throw new InvalidOperationException($"'{fullPath}' is not a sector-aligned DFS image.");

            drives[0] = image;
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
        }

        /// <summary>Reads an 8271 register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The register value.</returns>
        public byte Read(ushort address)
        {
            return address switch
            {
                0xFE80 => ReadStatus(),
                0xFE81 => ReadResult(),
                0xFE84 => ReadData(),
                _ => 0x00
            };
        }

        /// <summary>Writes an 8271 register.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The value written by the CPU.</param>
        public void Write(ushort address, byte value)
        {
            switch (address)
            {
                case 0xFE80:
                    BeginCommand(value);
                    break;

                case 0xFE81:
                    WriteParameter(value);
                    break;

                case 0xFE82:
                    Reset();
                    break;

                case 0xFE84:
                    WriteData(value);
                    break;
            }
        }

        private byte ReadStatus()
        {
            byte status = 0;

            if (readData.Count > 0 || pendingWrite is not null)
                status |= StatusDataRequest;

            if (resultAvailable)
                status |= StatusInterrupt | StatusResultFull;

            return status;
        }

        private byte ReadResult()
        {
            resultAvailable = false;
            return result;
        }

        private byte ReadData()
        {
            if (readData.Count == 0)
                return 0x00;

            byte value = readData.Dequeue();
            if (readData.Count == 0)
                SetResult(0);

            return value;
        }

        private void BeginCommand(byte value)
        {
            command = value;
            parameters.Clear();
            resultAvailable = false;

            if (GetParameterCount(command) == 0)
                ExecuteCommand();
        }

        private void WriteParameter(byte value)
        {
            parameters.Add(value);

            if (parameters.Count >= GetParameterCount(command))
                ExecuteCommand();
        }

        private void WriteData(byte value)
        {
            if (pendingWrite is null)
                return;

            writeData.Add(value);

            if (writeData.Count >= pendingWrite.Value.Length)
            {
                WriteSectors(pendingWrite.Value, writeData);
                pendingWrite = null;
                writeData.Clear();
                SetResult(0);
            }
        }

        private void ExecuteCommand()
        {
            byte opcode = (byte)(command & 0x3F);

            switch (opcode)
            {
                case 0x0A:
                case 0x0E:
                    PrepareWrite(parameters[0], parameters[1], 128, 1);
                    break;

                case 0x0B:
                case 0x0F:
                    PrepareWrite(parameters[0], parameters[1], GetSectorSize(parameters[2]), GetSectorCount(parameters[2]));
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
                    SetResult(HasSector(parameters[0], parameters[1]) ? (byte)0 : (byte)0x18);
                    break;

                case 0x1F:
                    SetResult(HasSector(parameters[0], parameters[1]) ? (byte)0 : (byte)0x18);
                    break;

                case 0x29:
                    specialRegisters[0x12] = parameters[0];
                    specialRegisters[0x1A] = parameters[0];
                    SetResult(0);
                    break;

                case 0x2C:
                    result = HasMountedDisc ? (byte)0x45 : (byte)0x00;
                    resultAvailable = true;
                    break;

                case 0x35:
                    SetResult(0);
                    break;

                case 0x3A:
                    specialRegisters[parameters[0] & 0x3F] = parameters[1];
                    SetResult(0);
                    break;

                case 0x3D:
                    result = specialRegisters[parameters[0] & 0x3F];
                    resultAvailable = true;
                    break;

                default:
                    SetResult(0x18);
                    break;
            }
        }

        private void ReadSectors(int track, int sector, int sectorSize, int count)
        {
            if (!TryGetOffset(track, sector, out int offset) || offset + (sectorSize * count) > drives[selectedDrive].Length)
            {
                SetResult(0x18);
                return;
            }

            for (int i = 0; i < sectorSize * count; i++)
                readData.Enqueue(drives[selectedDrive][offset + i]);
        }

        private void PrepareWrite(int track, int sector, int sectorSize, int count)
        {
            if (!TryGetOffset(track, sector, out int offset) || offset + (sectorSize * count) > drives[selectedDrive].Length)
            {
                SetResult(0x18);
                return;
            }

            pendingWrite = new PendingWrite(offset, sectorSize * count);
            writeData.Clear();
        }

        private void WriteSectors(PendingWrite write, List<byte> bytes)
        {
            byte[] image = drives[selectedDrive];

            for (int i = 0; i < write.Length; i++)
                image[write.Offset + i] = bytes[i];
        }

        private void ReadSectorIds(int track, int count)
        {
            int sectorCount = count == 0 ? SectorsPerTrack : Math.Min(count, SectorsPerTrack);

            for (int sector = 0; sector < sectorCount; sector++)
            {
                readData.Enqueue((byte)track);
                readData.Enqueue(0);
                readData.Enqueue((byte)sector);
                readData.Enqueue(1);
            }
        }

        private bool HasSector(int track, int sector)
        {
            return TryGetOffset(track, sector, out int offset) && offset + SectorSize <= drives[selectedDrive].Length;
        }

        private bool TryGetOffset(int track, int sector, out int offset)
        {
            offset = ((track * SectorsPerTrack) + sector) * SectorSize;
            return HasMountedDisc && track >= 0 && sector >= 0 && sector < SectorsPerTrack && offset >= 0;
        }

        private void SetResult(byte value)
        {
            result = value;
            resultAvailable = true;
        }

        private bool TryGetFirstCatalogueFile(out string? name)
        {
            name = null;

            if (!HasMountedDisc || drives[0].Length < 512)
                return false;

            int fileCount = drives[0][0x107] / 8;
            if (fileCount <= 0)
                return false;

            name = ReadDfsName(drives[0], 8);
            return !string.IsNullOrWhiteSpace(name);
        }

        private static int GetParameterCount(byte command)
        {
            return (command & 0x3F) switch
            {
                0x0A or 0x0E or 0x12 or 0x16 or 0x1E => 2,
                0x0B or 0x0F or 0x13 or 0x17 or 0x1B or 0x1F => 3,
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

        private readonly record struct PendingWrite(int Offset, int Length);
    }
}
