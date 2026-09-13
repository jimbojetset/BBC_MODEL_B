using BBC.CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;

const string TestDataBaseUrl = "https://raw.githubusercontent.com/SingleStepTests/65x02/main/rockwell65c02/v1/";

using HttpClient httpClient = new HttpClient();
string? selectedOpcode = null;
var remainingArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--opcode")
    {
        if (++i >= args.Length || !byte.TryParse(args[i], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out byte opcode))
            throw new ArgumentException("--opcode requires a hexadecimal byte, such as ea.");
        selectedOpcode = opcode.ToString("x2");
    }
    else remainingArgs.Add(args[i]);
}
string? testDataDirectory = ParseTestDataDirectory(remainingArgs.ToArray());

// https://github.com/SingleStepTests/65x02/blob/main/rockwell65c02/v1

Dictionary<string, string[]> testDictionary = new Dictionary<string, string[]>();

// Rockwell vectors cover every opcode, including CMOS NOPs and bit operations.
testDictionary.Add("Rockwell65C02", Enumerable.Range(0, 256)
    .Select(opcode => opcode.ToString("x2")).ToArray());

if (selectedOpcode is not null)
{
    testDictionary.Clear();
    testDictionary.Add("Selected opcode", [selectedOpcode]);
}

int testCount = 0;
int testCountTotal = 0;
int opcodes = 0;
int success = 0;
int failure = 0;
int totalSuccess = 0;
int totalFailure = 0;
int totalOpcodeCount = 0;

foreach (KeyValuePair<string, string[]> testPlan in testDictionary)
    foreach (string test in testPlan.Value)
        totalOpcodeCount++;

Console.WriteLine("Starting Tests...");
var watch = System.Diagnostics.Stopwatch.StartNew();
string failedOpcodes = "";
foreach (KeyValuePair<string, string[]> testPlan in testDictionary)
{
    foreach (string test in testPlan.Value)
    {
        opcodes++;

        Console.Write("\r{0}   ", "Opcode " + opcodes + " of " + totalOpcodeCount);

        string testData = await LoadTestDataAsync(test, testDataDirectory, httpClient);

        List<Data>? testList = JsonSerializer.Deserialize<List<Data>>(testData);

        ulong outPC = 0;
        ulong outS = 0;
        ulong outP = 0;
        ulong outA = 0;
        ulong outX = 0;
        ulong outY = 0;
        List<List<int>>? ram = new List<List<int>>();

        testCount = 0;
        success = 0;
        failure = 0;

        foreach (Data? data in testList!)
        {
            testCount++;
            testCountTotal++;

            // Pending interrupts and instruction state must not leak into another vector.
            FlatMemoryBus bus = new FlatMemoryBus();
            CPU_65C02 cpu = new CPU_65C02(bus);

            // load the CPU
            cpu.registers.PC = data.initial!.pc;
            cpu.registers.P = data.initial!.p;
            cpu.registers.A = data.initial!.a;
            cpu.registers.X = data.initial!.x;
            cpu.registers.Y = data.initial!.y;
            cpu.registers.S = data.initial!.s;
            foreach (List<int> ramData in data.initial.ram!)
                bus.WriteByte((ulong)ramData[0], (byte)ramData[1]);

            // assertion values
            outPC = data.final!.pc;
            outP = data.final!.p;
            outA = data.final!.a;
            outX = data.final!.x;
            outY = data.final!.y;
            outS = data.final!.s;
            ram = data.final!.ram;

            // Complete the instruction boundary, including deferred SEI/CLI/PLP flag updates.
            cpu.StepInstruction();

            bool pass = true;
            foreach (List<int> ramData in ram!)
                if (ramData[1] != bus.ReadByte((ulong)ramData[0]))
                    pass = false;

            // check asserted values
            if (outPC != cpu.registers.PC ||
                 outP != cpu.registers.P ||
                 outA != cpu.registers.A ||
                 outX != cpu.registers.X ||
                 outY != cpu.registers.Y ||
                 outS != cpu.registers.S ||
                 pass != true)

            {

                //if (!pass)
                //{
                //    Console.WriteLine();
                //    foreach (List<int> ramData in ram!)
                //    {
                //        Console.WriteLine(ramData[0] + " " + ramData[1] + " " + bus.ReadByte((ulong)ramData[0]));
                //    }
               // }

                if (failure == 0)
                    Console.WriteLine($"\n{test.ToUpper()} {data.name}: expected PC={outPC:X4} A={outA:X2} X={outX:X2} Y={outY:X2} S={outS:X2} P={outP:X2}; actual PC={cpu.registers.PC:X4} A={cpu.registers.A:X2} X={cpu.registers.X:X2} Y={cpu.registers.Y:X2} S={cpu.registers.S:X2} P={cpu.registers.P:X2}; RAM match={pass}");
                if (!failedOpcodes.Contains(test))
                    failedOpcodes += test + " ";
                failure++;
                totalFailure++;
            }
            else
            {
                success++;
                totalSuccess++;
            }
        }
    }
}
watch.Stop();
Console.WriteLine();
Console.WriteLine("Total Tests Run: " + testCountTotal);
Console.WriteLine("Total Pass: " + totalSuccess + " tests");
Console.WriteLine("Total Fail: " + totalFailure + " tests");
Console.WriteLine("Failed Opcodes = " + failedOpcodes.ToUpper());
Console.WriteLine("Time Taken: " + watch.ElapsedMilliseconds / 1000 + " Seconds");
return totalFailure == 0 ? 0 : 1;

static string? ParseTestDataDirectory(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (arg.Equals("--test-dir", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException("Missing value for --test-dir.");

            return args[i + 1];
        }

        const string switchPrefix = "--test-dir=";
        if (arg.StartsWith(switchPrefix, StringComparison.OrdinalIgnoreCase))
            return arg[switchPrefix.Length..];

        if (!arg.StartsWith("-", StringComparison.Ordinal))
            return arg;
    }

    return null;
}

static async Task<string> LoadTestDataAsync(string opcode, string? testDataDirectory, HttpClient httpClient)
{
    string fileName = opcode + ".json";
    if (!string.IsNullOrWhiteSpace(testDataDirectory))
    {
        string localPath = Path.Combine(testDataDirectory, fileName);
        return await File.ReadAllTextAsync(localPath);
    }

    string url = TestDataBaseUrl + fileName;
    try
    {
        return await httpClient.GetStringAsync(url);
    }
    catch (HttpRequestException ex)
    {
        string localHint = string.IsNullOrWhiteSpace(testDataDirectory)
            ? "No local test directory was provided."
            : $"Local test file was not found in '{testDataDirectory}'.";
        throw new InvalidOperationException($"{localHint} Failed to download required test data from {url}.", ex);
    }
}

internal class Data
{
    public string? name { get; set; }
    public CpuState? initial { get; set; }
    public CpuState? final { get; set; }
    public List<List<object>>? cycles { get; set; }
}

internal class CpuState
{
    public ulong pc { get; set; }
    public byte s { get; set; }
    public byte a { get; set; }
    public byte x { get; set; }
    public byte y { get; set; }
    public byte p { get; set; }
    public List<List<int>>? ram { get; set; }
}
