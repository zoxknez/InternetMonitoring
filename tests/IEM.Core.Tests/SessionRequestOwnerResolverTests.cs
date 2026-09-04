using IEM.Core.Ipc;
using IEM.Service.Runtime;
using IEM.Storage;
using IEM.Storage.Layout;
using Microsoft.Extensions.Options;

namespace IEM.Core.Tests;

public sealed class SessionRequestOwnerResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "iem-owner-resolver-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Owner_is_restored_from_durable_session_request_after_restart()
    {
        Directory.CreateDirectory(_root);
        new SessionRequest(
            TimeSpan.FromHours(48),
            "eth0",
            DateTimeOffset.UtcNow,
            "session-1",
            "unix:1000").Write(_root);
        var resolver = CreateResolver();

        Assert.Equal("unix:1000", resolver.GetSessionOwner("session-1"));
        Assert.Equal("unix:1000", resolver.GetSessionOwner());
        Assert.Null(resolver.GetSessionOwner("different-session"));
    }

    [Fact]
    public void Missing_owner_fails_closed()
    {
        Directory.CreateDirectory(_root);
        new SessionRequest(
            TimeSpan.FromHours(1),
            null,
            DateTimeOffset.UtcNow,
            "session-1").Write(_root);

        Assert.Null(CreateResolver().GetSessionOwner("session-1"));
    }

    private SessionRequestOwnerResolver CreateResolver() => new(
        Options.Create(new MonitorSettings { OutputRoot = _root }),
        new FixedStorageLayout(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedStorageLayout(string root) : IPlatformStorageLayout
    {
        public string DefaultOutputRoot => root;
        public string PortableOutputRoot => root;
        public string ResolveOutputRoot(bool isInstalled) => root;
        public string GetSessionDirectory(string sessionId, bool isInstalled) =>
            Path.Combine(root, sessionId);
    }
}
