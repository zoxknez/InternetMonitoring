namespace IEM.App.Linux;

internal sealed class SingleInstanceLease : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLease(FileStream stream) => _stream = stream;

    public static SingleInstanceLease? TryAcquire()
    {
        var runtimeRoot = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Path.IsPathFullyQualified(runtimeRoot))
        {
            runtimeRoot = Path.GetTempPath();
        }

        var path = Path.Combine(runtimeRoot, $"internet-evidence-monitor-ui-{Environment.UserName}.lock");

        try
        {
            return new SingleInstanceLease(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
