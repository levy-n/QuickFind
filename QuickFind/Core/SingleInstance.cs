using System.IO;
using System.IO.Pipes;

namespace QuickFind.Core;

// Named-pipe handshake so a second launch wakes the running instance
// instead of silently exiting. The existing mutex still prevents two live
// processes; this layer adds the "show the running window" side.
//
// Protocol (trivial, single line):
//   Client writes "SHOW\n" (ASCII). Server receives it, fires a callback.
//   Anything else is ignored.
public static class SingleInstance
{
    private const string PipeName = "QuickFind_SingleInstance_Pipe";
    private const string ShowCommand = "SHOW";

    // Attempt to signal an already-running instance. Returns true on success.
    // Short timeout — if no-one is listening we fall back to the first-run path.
    public static bool TrySignalShow(int timeoutMs = 500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(ShowCommand);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Info($"SingleInstance.TrySignalShow: no running instance responded ({ex.GetType().Name})");
            return false;
        }
    }

    // Start a background server that fires `onShow` whenever another launch
    // of QuickFind asks us to pop the window. Call from the main instance.
    public static void StartServer(Action onShow, CancellationToken ct)
    {
        var thread = new Thread(() => ServerLoop(onShow, ct))
        {
            IsBackground = true,
            Name = "QuickFind-SingleInstance-Pipe"
        };
        thread.Start();
    }

    private static void ServerLoop(Action onShow, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                var waitTask = server.WaitForConnectionAsync(ct);
                waitTask.Wait(ct);

                using var reader = new StreamReader(server);
                string? line = reader.ReadLine();
                if (line == ShowCommand)
                {
                    try { onShow(); }
                    catch (Exception ex) { Logger.Warn("SingleInstance onShow callback failed", ex); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn("SingleInstance.ServerLoop iteration failed", ex);
                // Brief back-off so we don't spin on a persistent failure.
                try { Task.Delay(500, ct).Wait(ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
