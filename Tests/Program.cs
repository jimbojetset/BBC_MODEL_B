using BBC;

const ushort Control = 0xFE80;
const ushort Command = 0xFE84;
const ushort Data = 0xFE87;

Run("WD1770 keeps an independent head position for each drive", IndependentDriveHeads);
Run("WD1770 keeps independent spindle timers for each drive", IndependentDriveMotors);
Run("WD1770 Force Interrupt supports immediate interrupt", ForceImmediate);
Run("WD1770 Force Interrupt supports ready transitions", ForceReadyTransitions);
Run("WD1770 Force Interrupt supports the next index pulse", ForceIndex);

Console.WriteLine("All BBC Model B regression tests passed.");

static void IndependentDriveHeads()
{
    WD1770_Disk controller = NewControllerWithTwoDiscs();
    List<(int Drive, int Delta)> seeks = [];
    controller.DriveSeek += (drive, delta) => seeks.Add((drive, delta));

    SelectDrive(controller, 0);
    Seek(controller, 10);
    SelectDrive(controller, 1);
    Seek(controller, 20);
    SelectDrive(controller, 0);
    controller.Write(Command, 0x50); // STEP IN and update the track register.
    controller.Tick(100_000);

    Equal((0, 10), seeks[0], "drive 0 initial seek");
    Equal((1, 20), seeks[1], "drive 1 must start at track zero");
    Equal((0, 1), seeks[2], "drive 0 must resume from track ten");
}

static void IndependentDriveMotors()
{
    WD1770_Disk controller = NewControllerWithTwoDiscs();
    List<int> stopped = [];
    controller.DriveMotorStopped += drive => stopped.Add(drive);

    SelectDrive(controller, 0);
    controller.Write(Command, 0x00);
    controller.Tick(10_000);
    SelectDrive(controller, 1);
    controller.Write(Command, 0x00);
    controller.Tick(10_000);
    controller.Tick(5_980_000);

    Equal(1, stopped.Count, "only the first spindle should have timed out");
    Equal(0, stopped[0], "drive 0 should stop first");
    controller.Tick(10_000);
    Equal(1, stopped[1], "drive 1 should retain its later timeout");
}

static void ForceImmediate()
{
    WD1770_Disk controller = new();
    SelectDrive(controller, 0);
    controller.Write(Command, 0xD8);
    True(controller.NmiLineAsserted, "immediate Force Interrupt should assert INTRQ");
}

static void ForceReadyTransitions()
{
    WD1770_Disk readyGain = new();
    SelectDrive(readyGain, 0);
    readyGain.Write(Command, 0xD1);
    readyGain.MountImage(BlankDfsDisc(), 0, null, "ready.ssd", readOnly: true);
    True(readyGain.NmiLineAsserted, "not-ready to ready should assert INTRQ");

    WD1770_Disk readyLoss = NewControllerWithDisc();
    SelectDrive(readyLoss, 0);
    readyLoss.Write(Command, 0xD2);
    readyLoss.EjectPhysicalDrive(0);
    True(readyLoss.NmiLineAsserted, "ready to not-ready should assert INTRQ");
}

static void ForceIndex()
{
    WD1770_Disk controller = NewControllerWithDisc();
    SelectDrive(controller, 0);
    controller.Write(Command, 0x00); // RESTORE starts the spindle.
    controller.Tick(10_000);
    controller.Read(Command); // Clear the command-complete interrupt.
    controller.Write(Command, 0xD4);
    controller.Tick(400_000);
    True(controller.NmiLineAsserted, "next index pulse should assert INTRQ");
}

static WD1770_Disk NewControllerWithDisc()
{
    WD1770_Disk controller = new();
    controller.MountImage(BlankDfsDisc(), 0, null, "test.ssd", readOnly: true);
    return controller;
}

static WD1770_Disk NewControllerWithTwoDiscs()
{
    WD1770_Disk controller = NewControllerWithDisc();
    controller.MountImage(BlankDfsDisc(), 1, null, "test1.ssd", readOnly: true);
    return controller;
}

static byte[] BlankDfsDisc() => new byte[80 * 10 * 256];

static void SelectDrive(WD1770_Disk controller, int drive) =>
    controller.Write(Control, drive == 0 ? (byte)0x21 : (byte)0x22);

static void Seek(WD1770_Disk controller, byte track)
{
    controller.Write(Data, track);
    controller.Write(Command, 0x10);
    controller.Tick(1_000_000);
}

static void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        Environment.ExitCode = 1;
    }
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}
