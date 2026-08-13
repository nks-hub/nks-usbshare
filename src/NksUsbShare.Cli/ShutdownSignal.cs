using System.Runtime.InteropServices;

namespace NksUsbShare.Cli;

/// <summary>
/// A shutdown signal that works whether or not there is a terminal attached.
/// Console.CancelKeyPress only fires for an interactive Ctrl+C, so a process started by a service
/// manager or with nohup would otherwise never hear a stop request and would have to be killed,
/// leaving devices attached behind it.
/// </summary>
internal sealed class ShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly List<PosixSignalRegistration> _registrations = [];

    public ShutdownSignal()
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        foreach (var signal in (PosixSignal[])[PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT])
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    _source.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists on every platform; the others still cover shutdown.
            }
        }
    }

    public CancellationToken Token => _source.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        _source.Cancel();
    }

    public async Task WaitAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _source.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        foreach (var registration in _registrations) registration.Dispose();
        _source.Dispose();
    }
}
