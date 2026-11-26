using System.Runtime.InteropServices;
using System.Text;

namespace MangaCli;

internal static class Program
{
    private static int _sigintCount;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Processes spawned by agent harnesses can inherit the "ignore CTRL_C" console
        // attribute, which would silently disable the graceful-interrupt path below.
        // Passing NULL as the handler resets Ctrl+C processing to the default (enabled).
        if (OperatingSystem.IsWindows())
        {
            _ = SetConsoleCtrlHandler(IntPtr.Zero, false);
        }

        using var interrupt = new CancellationTokenSource();
        var registrations = RegisterSignals(interrupt);
        try
        {
            var env = new CliEnvironment();
            return await CliApp.RunAsync(args, env, interrupt.Token);
        }
        finally
        {
            foreach (var reg in registrations)
            {
                reg.Dispose();
            }
        }
    }

    /// <summary>
    /// First signal: cancel gracefully (command stops the session and verifies). Second SIGINT:
    /// warn that the cloud may still be billing and exit immediately with code 7.
    /// </summary>
    private static List<PosixSignalRegistration> RegisterSignals(CancellationTokenSource interrupt)
    {
        var regs = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGTERM })
        {
            try
            {
                regs.Add(PosixSignalRegistration.Create(signal, e =>
                {
                    e.Cancel = true;
                    var count = Interlocked.Increment(ref _sigintCount);
                    if (count >= 2 && signal == PosixSignal.SIGINT)
                    {
                        Console.Error.WriteLine("强制退出，云端可能仍在计费，请执行 MangaCli stop");
                        Environment.Exit(ExitCodes.StopUnverified);
                    }

                    interrupt.Cancel();
                }));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
            {
                // Signal type not supported on this platform; skip it.
            }
        }

        return regs;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);
}
