using System.Reflection;
using System.Text.Json;
using TinyTracker.WinGet.Cli;

namespace TinyTracker.WinGet.Tests.Cli;

// A copy of the fake winget in a temp folder of its own, with the scenario it plays (tests/TinyTracker.FakeWinGet).
internal sealed class FakeWinGet : IDisposable
{
    private static readonly string Built = typeof(FakeWinGet).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "FakeWinGetFolder").Value!;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));

    // exe: the name it's copied as, such as winget.exe.
    public FakeWinGet(object scenario, string exe = "TinyTracker.FakeWinGet.exe")
    {
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.EnumerateFiles(Built, "TinyTracker.FakeWinGet.*"))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(_root, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : name));
        }
        File.WriteAllText(Path.Combine(_root, "scenario.json"), JsonSerializer.Serialize(scenario, Json));
        Exe = Path.Combine(_root, exe);
    }

    public string Exe { get; }

    // Trusted as winget, as the tests need.
    public WinGetCli Cli => new(Exe, _ => true);

    public bool Ran => File.Exists(Path.Combine(_root, "ran.txt"));

    public bool Touched(string file) => File.Exists(Path.Combine(_root, file));

    public IReadOnlyList<string> Arguments => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_root, "args.json")))!;

    // Its installer may still be exiting.
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(250);
            }
        }
    }

    public static object Say(string line) => new { say = line };
    public static object Sleep(int ms) => new { sleep = ms };
    // stubborn: when it fails, the fake says so and hangs on instead of ending.
    public static object Download(string url, int pace = 0, bool stubborn = false) => new { download = url, pace, stubborn };
    // As winget reads its sources, with nothing printed.
    public static object Fetch(string url, int pace = 0) => new { fetch = url, pace };
    public static object Installer(int ms) => new { installer = ms };
    public static object Touch(string file) => new { touch = file };
    public static object Scenario(int exit, params object[] steps) => new { steps, exit };
}
