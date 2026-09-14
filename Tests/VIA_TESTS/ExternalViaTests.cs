using System.Text.Json;

internal static class ExternalViaTests
{
    public static void Add(List<(string Name, Action Body)> tests)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "External", "via-cases.json");
        var cases = JsonSerializer.Deserialize<ViaCase[]>(File.ReadAllText(path))!;
        foreach (var test in cases)
            tests.Add(($"SourcedBBC/jsbeeb/{test.Name}", () =>
            {
                using var machine = new BbcProgram();
                machine.Run(Convert.FromHexString(test.Code));
                byte[] actual = machine.Ram.AsSpan(0x100, test.Expected.Length).ToArray();
                Check.True(test.Expected.SequenceEqual(actual.Select(b => (int)b)),
                    $"via.js:{test.SourceLine}: real-BBC expected [{string.Join(",", test.Expected)}], actual [{string.Join(",", actual)}]");
            }));
    }

    private sealed record ViaCase(string Name, int SourceLine, string Code, int[] Expected);
}
