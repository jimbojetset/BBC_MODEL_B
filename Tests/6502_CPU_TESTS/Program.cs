using BBC.CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;

const string TestDataBaseUrl = "https://raw.githubusercontent.com/SingleStepTests/65x02/main/6502/v1/";

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

// https://github.com/SingleStepTests/65x02/blob/main/6502/v1

Dictionary<string, string[]> testDictionary = new Dictionary<string, string[]>();

testDictionary.Add("NOP_Test", ["ea",]);
testDictionary.Add("LD_Tests", ["a9", "ad", "bd", "b9", "a5", "b5", "a1", "b1", "a2", "ae", "be", "a6", "b6", "a0", "ac", "bc", "a4", "b4"]);
testDictionary.Add("ST_Tests", ["8d", "9d", "99", "85", "95", "81", "91", "8e", "86", "96", "8c", "84", "94"]);
testDictionary.Add("T__Tests", ["aa", "a8", "ba", "8a", "9a", "98"]);
testDictionary.Add("SE_Tests", ["38", "f8", "78"]);
testDictionary.Add("PH_Tests", ["48", "08"]);
testDictionary.Add("PL_Tests", ["68", "28"]);
testDictionary.Add("CL_Tests", ["18", "d8", "58", "b8"]);
testDictionary.Add("DE_Tests", ["ce", "de", "c6", "d6", "ca","88"]);
testDictionary.Add("IX_Tests", ["ee", "fe", "e6", "f6", "e8", "c8"]);
testDictionary.Add("CM_Tests", ["c9", "cd", "dd", "d9", "c5", "d5", "c1", "d1"]);
testDictionary.Add("CP_Tests", ["e0", "ec", "e4", "c0", "cc", "c4"]);
testDictionary.Add("ADC_Tests", ["69","6d","7d","79","65","75","61","71"]);
testDictionary.Add("SBC_Tests", ["e9","ed","fd","f9","e5","f5","e1","f1"]);
testDictionary.Add("EOR_Tests", ["49","4d","5d","59","45","55","41","51"]);
testDictionary.Add("ORA_Tests", ["09", "0d", "1d", "19", "05", "15", "01", "11"]);
testDictionary.Add("AND_Tests", ["29", "2d", "3d", "39", "25", "35", "21", "31"]);
testDictionary.Add("BIT_Tests", ["2c", "24"]);
testDictionary.Add("ASL_Tests", ["0a", "0e", "1e", "06", "16"]);
testDictionary.Add("LSR_Tests", ["4a", "4e", "5e", "46", "56"]);
testDictionary.Add("ROL_Tests", ["2a", "2e", "3e", "26", "36"]);
testDictionary.Add("ROR_Tests", ["6a", "6e", "7e", "66", "76"]);
testDictionary.Add("BRANCH_Tests", ["10","00","90", "b0", "f0", "30", "d0",  "50", "70"]);
testDictionary.Add("J__Tests", ["4c", "6c", "20"]);
testDictionary.Add("RT_Tests", ["40", "60"]);
testDictionary.Add("Illegal_Opcodes", [
    "02", "03", "04", "07", "0b", "0c", "0f", "12", "13", "14", "17", "1a", "1b", "1c", "1f",
    "22", "23", "27", "2b", "2f", "32", "33", "34", "37", "3a", "3b", "3c", "3f", "42", "43", "44",
    "47", "4b", "4f", "52", "53", "54", "57", "5a", "5b", "5c", "5f", "62", "63", "64", "67",
    "6b", "6f", "72", "73", "74", "77", "7a", "7b", "7c", "7f", "80", "82", "83", "87", "89",
    "8b", "8f", "92", "93", "97", "9b", "9c", "9e", "9f", "a3", "a7", "ab", "af", "b3", "b7",
    "bb", "bf", "b2", "c2", "c3", "c7", "cb", "cf", "d2", "d3", "d4", "d7", "da", "db", "dc",
    "df", "e2", "e3", "e7", "eb", "ef", "f2", "f3", "f4", "f7", "fa", "fb", "fc", "ff"
    ]);

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

            // A previous JAM or deferred interrupt flag must not leak into another vector.
            FlatMemoryBus bus = new FlatMemoryBus();
            CPU_6502 cpu = new CPU_6502(bus);

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
