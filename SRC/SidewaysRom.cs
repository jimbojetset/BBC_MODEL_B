// ============================================================================
// Project:     BBC
// File:        SidewaysRom.cs
// Description: Sideways ROM bank descriptions and BBC ROM header inspection.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text;
using System.Text.Json;

namespace BBC
{
    public sealed record SidewaysRomSlot(
        int Bank,
        string? Path,
        string DisplayName,
        string Title,
        string Copyright,
        string RomType,
        ushort? LanguageEntry,
        ushort? ServiceEntry,
        bool Missing,
        bool Writable)
    {
        public bool Occupied => Path is not null || Writable;
    }

    public readonly record struct HostRomAction(HostRomActionKind Kind, int Bank, int TargetBank, string Path);

    public enum HostRomActionKind
    {
        Add,
        AddRam,
        Remove,
        Move,
        ImportLayout,
        ExportLayout
    }

    public sealed class SidewaysRomLayoutFile
    {
        public int Version { get; set; } = 2;

        public List<SidewaysRomLayoutBank> Banks { get; set; } = new List<SidewaysRomLayoutBank>();

        public static SidewaysRomLayoutFile FromSlots(IReadOnlyList<string?> romPaths, IReadOnlyList<bool> writableBanks)
        {
            SidewaysRomLayoutFile layout = new SidewaysRomLayoutFile();
            for (int bank = 0; bank < romPaths.Count; bank++)
            {
                if (!string.IsNullOrWhiteSpace(romPaths[bank]))
                    layout.Banks.Add(new SidewaysRomLayoutBank { Bank = bank, Path = romPaths[bank]! });
                else if (writableBanks[bank])
                    layout.Banks.Add(new SidewaysRomLayoutBank { Bank = bank, Ram = true });
            }

            return layout;
        }

        public static SidewaysRomLayoutFile Load(string path)
        {
            string json = File.ReadAllText(path);
            SidewaysRomLayoutFile? layout = JsonSerializer.Deserialize<SidewaysRomLayoutFile>(json, JsonOptions);
            if (layout is null || layout.Version is < 1 or > 2)
                throw new InvalidDataException("Unsupported sideways memory layout.");

            layout.Banks ??= new List<SidewaysRomLayoutBank>();
            return layout;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public sealed class SidewaysRomLayoutBank
    {
        public int Bank { get; set; }

        public string Path { get; set; } = string.Empty;

        public bool Ram { get; set; }
    }

    public static class SidewaysRomHeader
    {
        public static SidewaysRomSlot Inspect(int bank, string? path, byte[]? rom, bool writable = false)
        {
            if (writable && path is null)
                return new SidewaysRomSlot(bank, null, "RAM", "Sideways RAM", string.Empty, "16K RAM", null, null, false, true);
            if (path is null)
                return new SidewaysRomSlot(bank, null, "EMPTY", string.Empty, string.Empty, "Empty", null, null, false, false);

            string fallbackName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fallbackName))
                fallbackName = $"ROM {bank}";

            if (rom is null || rom.Length == 0)
            {
                return new SidewaysRomSlot(
                    bank,
                    path,
                    fallbackName,
                    fallbackName,
                    string.Empty,
                    "Missing",
                    null,
                    null,
                    true,
                    writable);
            }

            string title = ReadNullTerminatedAscii(rom, 9, 64);
            if (string.IsNullOrWhiteSpace(title))
                title = fallbackName;

            string copyright = string.Empty;
            if (rom.Length > 7 && rom[7] < rom.Length)
                copyright = ReadNullTerminatedAscii(rom, rom[7], 96);

            ushort? languageEntry = rom.Length >= 3 && rom[0] == 0x4C
                ? (ushort)(rom[1] | (rom[2] << 8))
                : null;

            ushort? serviceEntry = rom.Length >= 6 && rom[3] == 0x4C
                ? (ushort)(rom[4] | (rom[5] << 8))
                : null;

            string romType = DecodeRomType(rom);

            return new SidewaysRomSlot(
                bank,
                path,
                ShortDisplayName(title),
                title,
                copyright,
                romType,
                languageEntry,
                serviceEntry,
                false,
                writable);
        }

        private static string DecodeRomType(byte[] rom)
        {
            if (rom.Length <= 6)
                return "Unknown";

            byte type = rom[6];
            bool language = (type & 0x40) != 0;
            bool service = (type & 0x80) != 0;

            if (language && service)
                return "Language + service";
            if (language)
                return "Language";
            if (service)
                return "Service";

            return "Unknown";
        }

        private static string ShortDisplayName(string title)
        {
            string trimmed = title.Trim();
            if (trimmed.Length <= 18)
                return trimmed;

            return trimmed[..18].TrimEnd();
        }

        private static string ReadNullTerminatedAscii(byte[] bytes, int offset, int maxLength)
        {
            if (offset < 0 || offset >= bytes.Length)
                return string.Empty;

            StringBuilder builder = new StringBuilder();
            int end = Math.Min(bytes.Length, offset + maxLength);
            for (int i = offset; i < end; i++)
            {
                byte value = bytes[i];
                if (value == 0 || value == 0xFF)
                    break;

                if (value >= 32 && value <= 126)
                    builder.Append((char)value);
            }

            return builder.ToString().Trim();
        }
    }
}
