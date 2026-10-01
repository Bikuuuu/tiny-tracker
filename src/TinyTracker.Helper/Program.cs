using System.IO.Pipes;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.WinGet.Elevation;

namespace TinyTracker.Helper;

// The admin helper (spec §5.1): serves one app over its pipe, then exits. It writes nothing to disk but a self-update's Setup.
// Exit codes: 0 served, 2 not its command line, 3 another program took the pipe's name first.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (HelperArguments.Parse(args) is not { } arguments) return 2;
        NamedPipeServerStream pipe;
        try
        {
            pipe = HelperPipe.Create(arguments.Pipe, arguments.User);
        }
        catch (UnauthorizedAccessException)
        {
            return 3;
        }
        await using (pipe)
        {
            var version = SelfVersion.Parse(typeof(Program).Assembly.GetName().Version?.ToString(3));
            await new HelperServer(Work(arguments), AwakeTime.Instance, version).RunAsync(pipe, CancellationToken.None);
        }
        return 0;
    }

    private static IHelperWork Work(HelperArguments arguments) =>
#if DEBUG
        arguments.Demo ? new DemoHelperWork() : new WinGetHelperWork(arguments.User);
#else
        new WinGetHelperWork(arguments.User);
#endif
}
