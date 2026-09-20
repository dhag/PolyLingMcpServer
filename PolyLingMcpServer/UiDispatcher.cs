namespace PolyLingMcpServer;

/// <summary>
/// Routes tool-thread messages to wherever the host can show them.
/// The host is a console process launched by the MCP client, and its stdout
/// carries the JSON-RPC stream, so every message goes to stderr.
/// </summary>
public sealed class UiDispatcher
{
    private readonly Action<string> _appendLogLine;

    private UiDispatcher(Action<string> appendLogLine)
    {
        _appendLogLine = appendLogLine;
    }

    /// <summary>
    /// Headless stdio mode. Never writes to stdout: that is the MCP transport.
    /// </summary>
    public static UiDispatcher ForStandardError()
        => new(line => Console.Error.WriteLine(line));

    /// <summary>Fire-and-forget. There is no UI thread to marshal onto.</summary>
    public void Post(Action action) => action();

    /// <summary>
    /// Runs the function and propagates its exception through the returned task,
    /// matching the behaviour tools relied on when a UI thread existed.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            return Task.FromResult(func());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        => InvokeAsync(() => { action(); return true; }, cancellationToken);

    /// <summary>Appends one timestamped line to the activity log. Safe to call from any thread.</summary>
    public void Log(string message)
        => Post(() => _appendLogLine($"{DateTime.Now:HH:mm:ss}  {message}"));
}
