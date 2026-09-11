namespace BBC
{
    internal enum TurtlePenColour : byte { Black, Red, Blue, Green }
    internal readonly record struct TurtleMark(float X, float Y, bool Start, TurtlePenColour Colour = TurtlePenColour.Black);
    internal sealed record TurtleDrawing(double X, double Y, double Heading, bool PenDown, TurtleMark[] Marks, TurtlePenColour Colour = TurtlePenColour.Black);

    internal sealed class JessopTurtle
    {
        // Jessop's 65 mm wheels move 1.750 mm per encoder edge. At the documented
        // nominal 100 mm/s this is 35,000 cycles of the BBC's 2 MHz CPU.
        internal const int WheelEdgeCycles = 35_000;
        // The pen cam has no documented speed; use a repeatable 200 ms half-turn.
        internal const int PenEdgeCycles = 400_000;
        private readonly object sync = new();
        private readonly List<TurtleMark> marks = new();
        private double x;
        private double y;
        private double heading;
        private bool strokeStarted;
        private TurtlePenColour penColour;
        private int leftPhase;
        private int rightPhase;
        private int penPhase;
        private byte sensors = 0x60;
        private byte outputs = 0xFF;
        private byte direction;

        internal long LeftEdges { get; private set; }
        internal long RightEdges { get; private set; }
        internal bool PenDown => (sensors & 0x80) != 0;
        internal bool HooterActive => (direction & 0x80) != 0 && (outputs & 0x80) == 0;
        internal byte Sensors => sensors;

        internal void WritePort(byte value, byte dataDirection)
        {
            direction = dataDirection;
            // Undriven control pins idle high; the interface inverts PB0-PB4.
            outputs = (byte)(value | ~dataDirection);
        }

        internal void Tick(int cycles)
        {
            lock (sync)
            {
                // Integrate differential wheel travel continuously, independently of
                // encoder edges, so the visible turtle does not jump between pulses.
                double left = (outputs & 0x10) == 0 ? cycles * 0.00005 * ((outputs & 8) != 0 ? 1 : -1) : 0;
                double right = (outputs & 4) == 0 ? cycles * 0.00005 * ((outputs & 2) != 0 ? 1 : -1) : 0;
                double angle = (left - right) / 200.0; // Jessop wheel spacing: 200 mm.
                double distance = (left + right) / 2;
                if (PenDown && !strokeStarted)
                {
                    marks.Add(new TurtleMark((float)x, (float)y, true, penColour));
                    strokeStarted = true;
                }
                double middle = heading + angle / 2;
                x += distance * Math.Sin(middle);
                y -= distance * Math.Cos(middle);
                heading = (heading + angle) % (2 * Math.PI);
                if (strokeStarted && marks.Count > 0)
                {
                    TurtleMark last = marks[^1];
                    if (Math.Abs(x - last.X) + Math.Abs(y - last.Y) >= 0.5 || !PenDown)
                        marks.Add(new TurtleMark((float)x, (float)y, false, penColour));
                }
                if (!PenDown) strokeStarted = false;
                TickSensors(cycles);
            }
        }

        private void TickSensors(int cycles)
        {
            if ((outputs & 0x10) == 0)
            {
                leftPhase += cycles;
                while (leftPhase >= WheelEdgeCycles)
                {
                    leftPhase -= WheelEdgeCycles;
                    sensors ^= 0x40;
                    LeftEdges += (outputs & 0x08) != 0 ? 1 : -1;
                }
            }
            if ((outputs & 0x04) == 0)
            {
                rightPhase += cycles;
                while (rightPhase >= WheelEdgeCycles)
                {
                    rightPhase -= WheelEdgeCycles;
                    sensors ^= 0x20;
                    RightEdges += (outputs & 0x02) != 0 ? 1 : -1;
                }
            }
            if ((outputs & 0x01) == 0)
            {
                penPhase += cycles;
                while (penPhase >= PenEdgeCycles)
                {
                    penPhase -= PenEdgeCycles;
                    sensors ^= 0x80;
                }
            }
        }

        internal TurtleDrawing CaptureDrawing()
        {
            lock (sync)
                return new TurtleDrawing(x, y, heading, PenDown, marks.ToArray(), penColour);
        }

        internal void SetPenColour(TurtlePenColour colour)
        {
            lock (sync)
            {
                if (penColour == colour) return;
                // Finish the old ink exactly where the pen is changed, even between
                // encoder edges, then start the new colour at that same position.
                if (strokeStarted)
                    marks.Add(new TurtleMark((float)x, (float)y, false, penColour));
                penColour = colour;
                strokeStarted = false;
                if (PenDown)
                {
                    marks.Add(new TurtleMark((float)x, (float)y, true, penColour));
                    strokeStarted = true;
                }
            }
        }

        internal void ClearDrawing()
        {
            lock (sync)
            {
                marks.Clear();
                strokeStarted = false;
            }
        }

        internal void SaveState(BinaryWriter writer)
        {
            writer.Write(leftPhase);
            writer.Write(rightPhase);
            writer.Write(penPhase);
            writer.Write(sensors);
            writer.Write(outputs);
            writer.Write(direction);
            writer.Write(LeftEdges);
            writer.Write(RightEdges);
            writer.Write(x);
            writer.Write(y);
            writer.Write(heading);
            writer.Write(strokeStarted);
            writer.Write(marks.Count);
            foreach (TurtleMark mark in marks)
            {
                writer.Write(mark.X);
                writer.Write(mark.Y);
                writer.Write(mark.Start);
                writer.Write((byte)mark.Colour);
            }
            writer.Write((byte)penColour);
        }

        private static TurtlePenColour ReadPenColour(BinaryReader reader)
        {
            byte colour = reader.ReadByte();
            if (colour > (byte)TurtlePenColour.Green)
                throw new InvalidDataException("Invalid turtle pen colour.");
            return (TurtlePenColour)colour;
        }

        internal void LoadState(BinaryReader reader, bool hasDrawing = true, bool hasColours = true)
        {
            leftPhase = reader.ReadInt32();
            rightPhase = reader.ReadInt32();
            penPhase = reader.ReadInt32();
            sensors = reader.ReadByte();
            outputs = reader.ReadByte();
            direction = reader.ReadByte();
            LeftEdges = reader.ReadInt64();
            RightEdges = reader.ReadInt64();
            marks.Clear();
            x = y = heading = 0;
            strokeStarted = false;
            penColour = TurtlePenColour.Black;
            if (hasDrawing)
            {
                x = reader.ReadDouble();
                y = reader.ReadDouble();
                heading = reader.ReadDouble();
                strokeStarted = reader.ReadBoolean();
                int count = reader.ReadInt32();
                if (count < 0 || count > 10_000_000)
                    throw new InvalidDataException("Invalid turtle drawing size.");
                for (int i = 0; i < count; i++)
                    marks.Add(new TurtleMark(reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean(),
                        hasColours ? ReadPenColour(reader) : TurtlePenColour.Black));
                if (hasColours) penColour = ReadPenColour(reader);
            }
        }
    }
}
