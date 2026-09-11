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

        // A missing runtime directory is not a second instance. XDG_RUNTIME_DIR can name a
        // directory that does not exist - a login without a session manager, a service unit
        // with a stale environment - and DirectoryNotFoundException derives from IOException,
        // so catching IOException alone reports "already running" for a directory that was
        // never there. Create it first, and let a genuinely unusable location surface as the
        // startup failure it is rather than as a phantom instance.
        try
        {
            Directory.CreateDirectory(runtimeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runtimeRoot = Path.GetTempPath();
            path = Path.Combine(runtimeRoot, $"internet-evidence-monitor-ui-{Environment.UserName}.lock");
        }

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
            // The lock is held: another instance of the UI owns it for this user.
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
