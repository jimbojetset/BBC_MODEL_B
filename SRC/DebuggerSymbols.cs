// ============================================================================
// Project:     BBC
// File:        DebuggerSymbols.cs
// Description: BBC MOS, hardware, and user-loaded debugger symbols.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Globalization;
using System.Text.RegularExpressions;

namespace BBC
{
    public sealed class DebuggerSymbols
    {
        private readonly Dictionary<string, ushort> builtInByName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, string> builtInByAddress = new Dictionary<ushort, string>();
        private readonly Dictionary<string, ushort> externalByName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, string> externalByAddress = new Dictionary<ushort, string>();

        public DebuggerSymbols()
        {
            AddMosSymbols();
            AddHardwareSymbols();
        }

        public int BuiltInCount => builtInByName.Count;
        public int ExternalCount => externalByName.Count;

        public int Load(string path)
        {
            Dictionary<string, ushort> names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            Dictionary<ushort, string> addresses = new Dictionary<ushort, string>();
            string content = File.ReadAllText(path);
            if (content.TrimStart().StartsWith("[{", StringComparison.Ordinal))
            {
                ParseBeebAsmLabels(content, names, addresses);
            }
            else
            {
                int lineNumber = 0;
                using StringReader reader = new StringReader(content);
                while (reader.ReadLine() is string sourceLine)
                {
                    lineNumber++;
                    string line = RemoveComment(sourceLine).Trim();
                    if (line.Length == 0)
                        continue;

                    if (!TryParseLine(line, out string name, out ushort address))
                        throw new ArgumentException($"Invalid symbol at line {lineNumber}: {sourceLine.Trim()}");

                    names[name] = address;
                    addresses[address] = name;
                }
            }

            if (names.Count == 0)
                throw new ArgumentException("Symbol file contains no recognised symbols");

            externalByName.Clear();
            externalByAddress.Clear();
            foreach ((string name, ushort address) in names)
                externalByName[name] = address;
            foreach ((ushort address, string name) in addresses)
                externalByAddress[address] = name;
            return names.Count;
        }

        public void Unload()
        {
            externalByName.Clear();
            externalByAddress.Clear();
        }

        public bool TryAddress(string name, out ushort address) =>
            externalByName.TryGetValue(name, out address) || builtInByName.TryGetValue(name, out address);

        public bool TryExactName(ushort address, out string name)
        {
            if (externalByAddress.TryGetValue(address, out string? externalName))
            {
                name = externalName;
                return true;
            }
            if (builtInByAddress.TryGetValue(address, out string? builtInName))
            {
                name = builtInName;
                return true;
            }
            name = string.Empty;
            return false;
        }

        public string? FormatAddress(ushort address, bool nearest)
        {
            if (TryExactName(address, out string name))
                return name;
            if (!nearest)
                return null;

            ushort bestAddress = 0;
            string? bestName = null;
            foreach ((ushort candidateAddress, string candidateName) in externalByAddress)
            {
                if (candidateAddress <= address && address - candidateAddress <= 0xFF && (bestName is null || candidateAddress >= bestAddress))
                {
                    bestAddress = candidateAddress;
                    bestName = candidateName;
                }
            }
            return bestName is null ? null : $"{bestName}+${address - bestAddress:X}";
        }

        public IEnumerable<(string Name, ushort Address, bool External)> Find(string? filter)
        {
            string match = filter ?? string.Empty;
            foreach ((string name, ushort address) in externalByName
                .Where(symbol => symbol.Key.Contains(match, StringComparison.OrdinalIgnoreCase))
                .OrderBy(symbol => symbol.Value).ThenBy(symbol => symbol.Key))
                yield return (name, address, true);

            foreach ((string name, ushort address) in builtInByName
                .Where(symbol => !externalByName.ContainsKey(symbol.Key) && symbol.Key.Contains(match, StringComparison.OrdinalIgnoreCase))
                .OrderBy(symbol => symbol.Value).ThenBy(symbol => symbol.Key))
                yield return (name, address, false);
        }

        private void AddMosSymbols()
        {
            string[] vectors = ["USERV", "BRKV", "IRQ1V", "IRQ2V", "CLIV", "BYTEV", "WORDV", "WRCHV", "RDCHV", "FILEV", "ARGSV", "BGETV", "BPUTV", "GBPBV", "FINDV", "FSCV", "EVNTV", "UPTV", "NETV", "VDUV", "KEYV", "INSV", "REMV", "CNPV", "IND1V", "IND2V", "IND3V"];
            for (int vector = 0; vector < vectors.Length; vector++)
                AddBuiltIn(vectors[vector], (ushort)(0x0200 + vector * 2));

            AddBuiltIn("OSFIND", 0xFFCE);
            AddBuiltIn("OSGBPB", 0xFFD1);
            AddBuiltIn("OSBPUT", 0xFFD4);
            AddBuiltIn("OSBGET", 0xFFD7);
            AddBuiltIn("OSARGS", 0xFFDA);
            AddBuiltIn("OSFILE", 0xFFDD);
            AddBuiltIn("OSRDCH", 0xFFE0);
            AddBuiltIn("OSASCI", 0xFFE3);
            AddBuiltIn("OSNEWL", 0xFFE7);
            AddBuiltIn("OSWRCH", 0xFFEE);
            AddBuiltIn("OSWORD", 0xFFF1);
            AddBuiltIn("OSBYTE", 0xFFF4);
            AddBuiltIn("OSCLI", 0xFFF7);
        }

        private void AddHardwareSymbols()
        {
            AddBuiltIn("CRTC_ADDRESS", 0xFE00);
            AddBuiltIn("CRTC_DATA", 0xFE01);
            AddBuiltIn("VIDEO_ULA_CONTROL", 0xFE20);
            AddBuiltIn("VIDEO_ULA_PALETTE", 0xFE21);
            AddViaSymbols("SYSVIA", 0xFE40);
            AddViaSymbols("USERVIA", 0xFE60);
            AddBuiltIn("DISC_FE80", 0xFE80);
            AddBuiltIn("DISC_FE81", 0xFE81);
            AddBuiltIn("DISC_FE84", 0xFE84);
            AddBuiltIn("DISC_FE85", 0xFE85);
            AddBuiltIn("DISC_FE86", 0xFE86);
            AddBuiltIn("DISC_FE87", 0xFE87);
            AddBuiltIn("INTEL8271_STATUS", 0xFE80);
            AddBuiltIn("INTEL8271_RESULT", 0xFE81);
            AddBuiltIn("INTEL8271_DATA", 0xFE84);
            AddBuiltIn("WD1770_CONTROL", 0xFE80);
            AddBuiltIn("WD1770_COMMAND", 0xFE84);
            AddBuiltIn("WD1770_STATUS", 0xFE84);
            AddBuiltIn("WD1770_TRACK", 0xFE85);
            AddBuiltIn("WD1770_SECTOR", 0xFE86);
            AddBuiltIn("WD1770_DATA", 0xFE87);
            AddBuiltIn("TUBE_R1_STATUS", 0xFEE0);
            AddBuiltIn("TUBE_R1_DATA", 0xFEE1);
            AddBuiltIn("TUBE_R2_STATUS", 0xFEE2);
            AddBuiltIn("TUBE_R2_DATA", 0xFEE3);
            AddBuiltIn("TUBE_R3_STATUS", 0xFEE4);
            AddBuiltIn("TUBE_R3_DATA", 0xFEE5);
            AddBuiltIn("TUBE_R4_STATUS", 0xFEE6);
            AddBuiltIn("TUBE_R4_DATA", 0xFEE7);
        }

        private void AddViaSymbols(string prefix, ushort address)
        {
            string[] registers = ["ORB", "ORA", "DDRB", "DDRA", "T1CL", "T1CH", "T1LL", "T1LH", "T2CL", "T2CH", "SR", "ACR", "PCR", "IFR", "IER", "ORA_NO_HANDSHAKE"];
            for (int register = 0; register < registers.Length; register++)
                AddBuiltIn($"{prefix}_{registers[register]}", (ushort)(address + register));
        }

        private void AddBuiltIn(string name, ushort address)
        {
            builtInByName[name] = address;
            builtInByAddress.TryAdd(address, name);
        }

        private static string RemoveComment(string line)
        {
            int semicolon = line.IndexOf(';');
            int backslash = line.IndexOf('\\');
            int comment = semicolon < 0 ? backslash : backslash < 0 ? semicolon : Math.Min(semicolon, backslash);
            return comment < 0 ? line : line[..comment];
        }

        private static void ParseBeebAsmLabels(string content, Dictionary<string, ushort> names, Dictionary<ushort, string> addresses)
        {
            MatchCollection matches = Regex.Matches(content, "['\"](?<name>[^'\"]+)['\"]\\s*:\\s*(?<address>-?\\d+(?:\\.\\d+)?)L?");
            foreach (Match match in matches)
            {
                string name = match.Groups["name"].Value;
                if (!double.TryParse(match.Groups["address"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || value < 0 || value > 0xFFFF || value != Math.Truncate(value))
                    throw new ArgumentException($"Invalid BeebAsm symbol address for {name}");
                ushort address = (ushort)value;
                names[name] = address;
                addresses[address] = name;
            }
        }

        private static bool TryParseLine(string line, out string name, out ushort address)
        {
            int equals = line.IndexOf('=');
            if (equals >= 0)
            {
                name = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                address = 0;
                return IsSymbolName(name) && TryHex(value, out address);
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && TryHex(parts[0], out address) && IsSymbolName(parts[1]))
            {
                name = parts[1];
                return true;
            }
            if (parts.Length >= 2 && IsSymbolName(parts[0]) && TryHex(parts[1], out address))
            {
                name = parts[0];
                return true;
            }
            name = string.Empty;
            address = 0;
            return false;
        }

        private static bool TryHex(string value, out ushort address)
        {
            string text = value.Trim().TrimEnd(',');
            if (text.StartsWith('$') || text.StartsWith('&')) text = text[1..];
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        private static bool IsSymbolName(string name)
        {
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] is '_' or '.'))
                return false;
            return name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');
        }
    }
}
