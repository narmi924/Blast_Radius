namespace Blast;

// One subscription lives only for one Run call. The console already delivers a
// keyboard Ctrl+C to attached children; this handler only keeps Blast alive to
// observe the direct child and persist an honest final state.
internal sealed class RunCancellation : IDisposable
{
    private readonly ConsoleCancelEventHandler? consoleHandler;
    private readonly CancellationTokenRegistration tokenRegistration;
    private readonly object gate = new();
    private int requests;
    private int disposed;
    private static int activeConsoleHandlers;

    internal string ConsoleControlMode { get; }
    internal int Requests => Volatile.Read(ref requests);
    internal static int ActiveConsoleHandlersForTest => Volatile.Read(ref activeConsoleHandlers);

    internal RunCancellation(CancellationToken token)
    {
        ConsoleControlMode = Console.IsInputRedirected ? "redirected_input_no_keyboard_control" :
            "attached_console_input";
        if (ConsoleControlMode == "attached_console_input")
        {
            consoleHandler = (_, e) =>
            {
                e.Cancel = true;
                Request();
            };
            Console.CancelKeyPress += consoleHandler;
            Interlocked.Increment(ref activeConsoleHandlers);
        }
        tokenRegistration = token.Register(Request);
    }

    private void Request()
    {
        lock (gate)
            if (disposed == 0) requests++;
    }

    internal void ThrowIfRequested()
    {
        if (Requests > 0) throw new OperationCanceledException("Run cancellation requested.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            disposed = 1;
        }
        Unsubscribe();
    }

    internal bool TryFinish(int expectedRequests)
    {
        lock (gate)
        {
            if (disposed != 0 || requests != expectedRequests) return false;
            disposed = 1;
        }
        Unsubscribe();
        return true;
    }

    private void Unsubscribe()
    {
        tokenRegistration.Dispose();
        if (consoleHandler is not null)
        {
            Console.CancelKeyPress -= consoleHandler;
            Interlocked.Decrement(ref activeConsoleHandlers);
        }
    }
}
