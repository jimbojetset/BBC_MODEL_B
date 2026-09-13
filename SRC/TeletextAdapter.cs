// Acorn ANE01 on the 1MHz bus. Register and field timing reference:
// BeebEm Teletext.cpp, Jon Welch / Alistair Cree (GPL-2.0-or-later).
namespace BBC;

internal sealed class TeletextAdapter
{
    private readonly byte[] ram = new byte[16 * 64];
    private byte control;
    private byte status = 0x0f;
    private int row, column, phase, cyclesUntilPhase = 128;
    private long fields;
    private DateTime clockStart = DateTime.Now;
    private TeletextPage[][] pages = [];
    private int[] subpages = [];
    private long[] nextSubpage = [];
    private int pageIndex, packetIndex;
    private byte[][] packets = [];

    internal bool Enabled { get; set; }
    internal bool IrqAsserted => Enabled && (control & 8) != 0 && (status & 0x80) != 0;
    // Only preset 1 is tuned to NMS Ceefax. Other presets have no signal.
    private bool Signal => (control & 3) == 0 && pages.Length != 0;
    internal static bool IsAddress(ushort address) => address is >= 0xfc10 and <= 0xfc13;

    internal void SetPages(TeletextPage[] newPages)
    {
        pages = newPages.GroupBy(p => p.Number).OrderBy(g => g.Key).Select(g => g.OrderBy(p => p.Subcode).ToArray()).ToArray();
        subpages = new int[pages.Length];
        nextSubpage = new long[pages.Length];
        pageIndex = packetIndex = 0;
        packets = [];
    }

    internal void Reset()
    {
        control = 0; status = 0x0f;
        row = column = phase = 0;
        cyclesUntilPhase = 128;
        Array.Clear(ram);
    }

    internal byte Read(ushort address)
    {
        if (!Enabled) return 0;
        switch (address & 3)
        {
            case 0: return (byte)(status | (phase == 2 && Signal ? 0x20 : 0));
            case 2:
                byte result = ram[row * 64 + column];
                column = (column + 1) & 63;
                return result;
            case 3: status &= 0x2f; break;
        }
        return 0;
    }

    internal void Write(ushort address, byte value)
    {
        if (!Enabled) return;
        switch (address & 3)
        {
            case 0: control = (byte)(value & 0x3f); break;
            case 1: row = value & 15; column = 0; break;
            case 2: ram[row * 64 + column] = value; column = (column + 1) & 63; break;
            case 3: status &= 0x2f; break;
        }
    }

    internal void Tick(int cycles)
    {
        if (!Enabled) return;
        cyclesUntilPhase -= cycles;
        while (cyclesUntilPhase <= 0)
        {
            switch (phase)
            {
                case 0:
                    if (Signal) status |= 0x10;
                    phase = 1;
                    cyclesUntilPhase += (fields & 1) == 0 ? 580 : 640;
                    break;
                case 1:
                    if (Signal) status = (byte)((status & ~0x40) | ((status & 0x80) >> 1));
                    phase = 2;
                    cyclesUntilPhase += 2176;
                    ReceiveField();
                    break;
                case 2:
                    if (Signal) status |= 0x80;
                    phase = 0;
                    cyclesUntilPhase += (fields & 1) == 0 ? 37244 : 37184;
                    fields++;
                    break;
            }
        }
    }

    private void ReceiveField()
    {
        // Twelve teletext lines per 50Hz field: enough time for original BBC ROMs
        // to copy and decode their receive RAM before the next field arrives.
        bool receiving = Signal && (control & 4) != 0;
        if (receiving) Array.Clear(ram);
        for (int line = 0; line < 12 && pages.Length != 0; line++)
        {
            if (packetIndex == packets.Length)
            {
                if (line != 0) break;
                int index = pageIndex;
                if (nextSubpage[index] != 0 && fields >= nextSubpage[index])
                    subpages[index] = (subpages[index] + 1) % pages[index].Length;
                TeletextPage page = pages[index][subpages[index]];
                if (nextSubpage[index] == 0 || fields >= nextSubpage[index]) nextSubpage[index] = fields + page.CycleFields;
                packets = page.Packets(clockStart.AddMilliseconds(fields * 20));
                packetIndex = 0;
                pageIndex = (pageIndex + 1) % pages.Length;
            }
            byte[] packet = packets[packetIndex++];
            if (receiving)
            {
                ram[line * 64] = 0x27; // SAA5030 framing code preceding the two MRAG bytes.
                packet.CopyTo(ram, line * 64 + 1);
            }
            // A page header needs a 20ms erasure interval before its first row.
            // ATS uses that field to allocate/clear the selected page buffer.
            if (packetIndex == 1) break;
        }
        if (receiving) row = column = 0;
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(Enabled); writer.Write(control); writer.Write(status); writer.Write(ram);
        writer.Write(row); writer.Write(column); writer.Write(phase); writer.Write(cyclesUntilPhase);
        writer.Write(fields); writer.Write(clockStart.ToBinary());
        writer.Write(pages.Length);
        for (int i = 0; i < pages.Length; i++)
        {
            writer.Write(pages[i].Length);
            foreach (TeletextPage page in pages[i]) page.SaveState(writer);
            writer.Write(subpages[i]); writer.Write(nextSubpage[i]);
        }
        writer.Write(pageIndex); writer.Write(packetIndex); writer.Write(packets.Length);
        foreach (byte[] packet in packets) writer.Write(packet);
    }

    internal void LoadState(BinaryReader reader)
    {
        Enabled = reader.ReadBoolean(); control = reader.ReadByte(); status = reader.ReadByte();
        reader.BaseStream.ReadExactly(ram);
        row = reader.ReadInt32(); column = reader.ReadInt32(); phase = reader.ReadInt32(); cyclesUntilPhase = reader.ReadInt32();
        fields = reader.ReadInt64(); clockStart = DateTime.FromBinary(reader.ReadInt64());
        int count = reader.ReadInt32();
        if (count is < 0 or > 2048 || row is < 0 or > 15 || column is < 0 or > 63 || phase is < 0 or > 2 || cyclesUntilPhase is < 1 or > 40000 || fields < 0)
            throw new InvalidDataException("Invalid Teletext adapter state.");
        pages = new TeletextPage[count][]; subpages = new int[count]; nextSubpage = new long[count];
        for (int i = 0; i < count; i++)
        {
            int subcount = reader.ReadInt32();
            if (subcount is < 1 or > 256) throw new InvalidDataException("Invalid Teletext subpage count.");
            pages[i] = new TeletextPage[subcount];
            for (int s = 0; s < subcount; s++) pages[i][s] = TeletextPage.LoadState(reader);
            subpages[i] = reader.ReadInt32(); nextSubpage[i] = reader.ReadInt64();
            if (subpages[i] < 0 || subpages[i] >= subcount) throw new InvalidDataException("Invalid Teletext subpage position.");
        }
        pageIndex = reader.ReadInt32(); packetIndex = reader.ReadInt32();
        int packetCount = reader.ReadInt32();
        if (pageIndex < 0 || pageIndex >= Math.Max(1, count) || packetCount is < 0 or > 27 || packetIndex < 0 || packetIndex > packetCount)
            throw new InvalidDataException("Invalid Teletext carousel position.");
        packets = new byte[packetCount][];
        for (int i = 0; i < packetCount; i++)
        {
            packets[i] = new byte[42];
            reader.BaseStream.ReadExactly(packets[i]);
        }
    }
}
