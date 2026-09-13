// Teletext Level 1 packets from the public TTI page format.
// Packet layout: ETS 300 706; TTI control-bit mapping as documented by VBIT2.
using System.Globalization;
using System.Numerics;

namespace BBC;

internal sealed class TeletextPage
{
    internal int Number;
    internal int Subcode;
    internal int Status = 0x8000;
    internal int CycleFields = 400;
    internal readonly byte[][] Rows = new byte[26][];
    internal int[]? Links;
    private static readonly byte[] Hamming = [0x15, 0x02, 0x49, 0x5e, 0x64, 0x73, 0x38, 0x2f, 0xd0, 0xc7, 0x8c, 0x9b, 0xa1, 0xb6, 0xfd, 0xea];

    internal static TeletextPage[] Parse(string tti)
    {
        List<TeletextPage> pages = [];
        TeletextPage? page = null;
        foreach (string line in tti.Split('\n'))
        {
            string s = line.TrimEnd('\r');
            if (s.Length < 3 || s[2] != ',') continue;
            string value = s[3..];
            switch (s[..2])
            {
                case "PN":
                    page = null;
                    if (value.Length >= 3 && int.TryParse(value[..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number)
                        && number is >= 0x100 and <= 0x8ff)
                    {
                        page = new TeletextPage { Number = number };
                        pages.Add(page);
                    }
                    break;
                case "SC" when page is not null:
                    if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int sub)) page.Subcode = sub & 0x3f7f;
                    break;
                case "PS" when page is not null:
                    if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int status)) page.Status = status;
                    break;
                case "CT" when page is not null:
                    if (int.TryParse(value.Split(',')[0], out int seconds)) page.CycleFields = Math.Clamp(seconds, 1, 3600) * 50;
                    break;
                case "OL" when page is not null:
                    int comma = value.IndexOf(',');
                    if (comma > 0 && int.TryParse(value[..comma], out int row) && row is >= 1 and <= 25)
                    {
                        byte[] text = Enumerable.Repeat((byte)0x20, 40).ToArray();
                        string input = value[(comma + 1)..];
                        for (int i = 0, col = 0; i < input.Length && col < 40; i++, col++)
                        {
                            int c = input[i];
                            if (c == 0x1b && i + 1 < input.Length) c = input[++i] & 0x1f;
                            text[col] = (byte)(c & 0x7f);
                        }
                        page.Rows[row] = text;
                    }
                    break;
                case "FL" when page is not null:
                    string[] links = value.Split(',');
                    if (links.Length >= 6)
                    {
                        page.Links = new int[6];
                        for (int i = 0; i < 6; i++)
                            page.Links[i] = int.TryParse(links[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int link)
                                && link is >= 0x100 and <= 0x8ff ? link : 0x8ff;
                    }
                    break;
            }
        }
        return pages.Where(p => (p.Status & 0x8000) != 0).ToArray();
    }

    internal byte[][] Packets(DateTime time)
    {
        List<byte[]> packets = [];
        byte[] header = Packet(0);
        header[2] = Hamming[Number & 15];
        header[3] = Hamming[(Number >> 4) & 15];
        header[4] = Hamming[Subcode & 15];
        header[5] = Hamming[((Subcode >> 4) & 7) | ((Status & 0x4000) != 0 ? 8 : 0)];
        header[6] = Hamming[(Subcode >> 8) & 15];
        header[7] = Hamming[((Subcode >> 12) & 3) | ((Status & 3) << 2)];
        header[8] = Hamming[(Status >> 2) & 15];
        // Pages are sent one after another across magazines, so signal serial mode (C11).
        header[9] = Hamming[1 | ((Status >> 6) & 14)];
        string title = $"CEEFAX {Number:X3} {time.ToString("ddd dd MMM", CultureInfo.InvariantCulture)} ";
        string text = title.PadRight(24)[..24] + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        for (int i = 0; i < 32; i++) header[10 + i] = OddParity((byte)text[i]);
        packets.Add(header);
        ushort crc = Crc(0, header.AsSpan(10, 24));
        // Include blank rows: they clear old page contents and contribute to the page CRC.
        for (int row = 1; row <= 25; row++)
        {
            byte[] packet = Packet(row);
            for (int col = 0; col < 40; col++) packet[col + 2] = OddParity(Rows[row]?[col] ?? (byte)0x20);
            crc = Crc(crc, packet.AsSpan(2));
            packets.Add(packet);
        }
        if (Links is not null)
        {
            byte[] packet = Packet(27);
            packet[2] = Hamming[0];
            for (int i = 0; i < 6; i++)
            {
                int link = Links[i], mag = ((link >> 8) ^ (Number >> 8)) & 7, offset = 3 + i * 6;
                packet[offset] = Hamming[link & 15];
                packet[offset + 1] = Hamming[(link >> 4) & 15];
                packet[offset + 2] = Hamming[15];
                packet[offset + 3] = Hamming[7 | ((mag & 1) << 3)];
                packet[offset + 4] = Hamming[15];
                packet[offset + 5] = Hamming[3 | ((mag & 6) << 1)];
            }
            packet[39] = Hamming[15];
            packet[40] = (byte)(crc >> 8);
            packet[41] = (byte)crc;
            packets.Add(packet);
        }
        return packets.ToArray();
    }

    private byte[] Packet(int row)
    {
        byte[] packet = new byte[42];
        packet[0] = Hamming[((Number >> 8) & 7) | ((row & 1) << 3)];
        packet[1] = Hamming[row >> 1];
        return packet;
    }

    private static byte OddParity(byte value) => (byte)(value | ((BitOperations.PopCount((uint)value) & 1) == 0 ? 0x80 : 0));

    private static ushort Crc(ushort crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            for (int bit = 7; bit >= 0; bit--)
            {
                int next = ((value >> bit) ^ (crc >> 6) ^ (crc >> 8) ^ (crc >> 11) ^ (crc >> 15)) & 1;
                crc = (ushort)((crc << 1) | next);
            }
        return crc;
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(Number); writer.Write(Subcode); writer.Write(Status); writer.Write(CycleFields);
        for (int row = 1; row <= 25; row++)
        {
            writer.Write(Rows[row] is not null);
            if (Rows[row] is not null) writer.Write(Rows[row]);
        }
        writer.Write(Links is not null);
        if (Links is not null) foreach (int link in Links) writer.Write(link);
    }

    internal static TeletextPage LoadState(BinaryReader reader)
    {
        TeletextPage page = new() { Number = reader.ReadInt32(), Subcode = reader.ReadInt32(), Status = reader.ReadInt32(), CycleFields = reader.ReadInt32() };
        if (page.Number is < 0x100 or > 0x8ff || page.Subcode is < 0 or > 0x3f7f || page.CycleFields is < 50 or > 180000)
            throw new InvalidDataException("Invalid Teletext page in save state.");
        for (int row = 1; row <= 25; row++)
            if (reader.ReadBoolean())
            {
                page.Rows[row] = reader.ReadBytes(40);
                if (page.Rows[row].Length != 40) throw new EndOfStreamException();
            }
        if (reader.ReadBoolean())
        {
            page.Links = new int[6];
            for (int i = 0; i < 6; i++) page.Links[i] = reader.ReadInt32();
        }
        return page;
    }
}
