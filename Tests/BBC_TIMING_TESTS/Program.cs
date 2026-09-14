using BBC;

string? filter = null;
bool list = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list") list = true;
    else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
    else
    {
        Console.Error.WriteLine("Usage: BBC_TIMING_TESTS [--list] [--filter name]");
        return 2;
    }
}

// Host tuning overrides must not alter the hardware expectations of this run.
foreach (string name in new[] { "BBC_USER_VIA_T1_RELOAD_EXTRA", "BBC_USER_VIA_T1_LOAD_EXTRA",
    "BBC_USER_VIA_T2_LOAD_EXTRA", "BBC_USER_VIA_TIMER_RELOAD_EXTRA", "BBC_USER_VIA_TIMER_LOAD_EXTRA" })
    Environment.SetEnvironmentVariable(name, null);

var tests = new List<(string Name, Action Body)>();
CpuTimingTests.Add(tests);
BusTimingTests.Add(tests);
DeviceTimingTests.Add(tests);
var selected = tests.Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
if (selected.Length == 0)
{
    Console.Error.WriteLine("No tests matched the filter.");
    return 2;
}
if (list)
{
    foreach (var test in selected) Console.WriteLine(test.Name);
    return 0;
}
int failed = 0;
foreach (var test in selected)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine($"\nTotal: {selected.Length}; Passed: {selected.Length - failed}; Failed: {failed}");
return failed == 0 ? 0 : 1;
