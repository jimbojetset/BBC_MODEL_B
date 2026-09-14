using BBC;

string? filter = null;
bool list = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list") list = true;
    else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
    else
    {
        Console.Error.WriteLine("Usage: VIA_TESTS [--list] [--filter name]");
        return 2;
    }
}

// Host tuning overrides must not alter the hardware expectations of this run.
foreach (string name in new[] { "BBC_USER_VIA_T1_RELOAD_EXTRA", "BBC_USER_VIA_T1_LOAD_EXTRA",
    "BBC_USER_VIA_T2_LOAD_EXTRA", "BBC_USER_VIA_TIMER_RELOAD_EXTRA", "BBC_USER_VIA_TIMER_LOAD_EXTRA" })
    Environment.SetEnvironmentVariable(name, null);

var tests = new List<(string Name, Action Body)>();
foreach (bool system in new[] { true, false })
{
    string prefix = system ? "System" : "User";
    void Add(string name, Action<Via> body) => tests.Add(($"{prefix}/{name}", () =>
    {
        using var via = new Via(system);
        body(via);
    }));
    CommonTests.Add(Add);
    if (system) SystemTests.Add(Add);
    else UserTests.Add(Add);
}
// These original assertions have no independently verified BBC result data.
for (int i = 0; i < tests.Count; i++)
    tests[i] = ("Unverified/" + tests[i].Name, tests[i].Body);
ExternalViaTests.Add(tests);
Console.WriteLine("Provenance: SourcedBBC = adapted upstream BBC tests; Unverified = local assertions, not hardware evidence. See PROVENANCE.md.");
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
int sourcedFailed = 0;
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
        if (test.Name.StartsWith("SourcedBBC/")) sourcedFailed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine($"\nTotal: {selected.Length}; Passed: {selected.Length - failed}; Failed: {failed}");
int sourced = selected.Count(t => t.Name.StartsWith("SourcedBBC/"));
Console.WriteLine($"Sourced BBC cases: {sourced}; passed: {sourced - sourcedFailed}; failed: {sourcedFailed}");
Console.WriteLine($"Unverified supplementary cases: {selected.Length - sourced}; passing these does not establish BBC hardware accuracy.");
return failed == 0 ? 0 : 1;
