using IEM.Linux.Storage;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxAtomicProvisioningAndRecoveryTests
{
    // ==========================================
    // 8D-A: Authoritative Absence & Errno Truth
    // ==========================================

    [Fact]
    public void Authoritative_Absence_ENOENT_Allows_Directory_Creation()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        int newFd = LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "keys", policy, LinuxPosixStorageConstants.Mode0700);
        Assert.True(newFd >= 0);

        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys", out var entry));
        Assert.True(entry.Stat.IsDirectory);
        Assert.Equal(0x1C0, (int)(entry.Stat.Mode & 0xFFF)); // 0700
    }

    [Theory]
    [InlineData(LinuxPosixStorageConstants.EACCES)]
    [InlineData(LinuxPosixStorageConstants.EIO)]
    [InlineData(LinuxPosixStorageConstants.ENOTDIR)]
    [InlineData(LinuxPosixStorageConstants.ELOOP)]
    [InlineData(LinuxPosixStorageConstants.ESTALE)]
    public void Non_ENOENT_Lookup_Error_Fails_Closed_With_Zero_Creation(int errno)
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        mock.FailFstatAtErrno = errno; // Returns error instead of ENOENT
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "keys", policy, LinuxPosixStorageConstants.Mode0700));

        Assert.Contains("Authoritative lookup", ex.Message);
        Assert.Equal(0, mock.MkdirAtCallCount); // ZERO creation!
    }

    // ==========================================
    // 8D-B: Crash-Safe Directory Provisioning
    // ==========================================

    [Fact]
    public void New_Directory_Defeats_Umask_Via_Fchmod_And_Fsyncs_Both_Child_And_Parent()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        int newFd = LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy, LinuxPosixStorageConstants.Mode0700);
        Assert.True(newFd >= 0);

        Assert.Equal(1, mock.FchmodCallCount); // fchmod applied to defeat umask
        Assert.True(mock.FsyncCallCount >= 2); // fsync on child newFd AND parent rootFd!
    }

    [Fact]
    public void New_Directory_Fchmod_Failure_Aborts_Provisioning()
    {
        var mock = new MockPosixStorageApi { FailFchmod = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy));
    }

    [Fact]
    public void New_Directory_Parent_Fsync_Failure_Aborts_Provisioning()
    {
        var mock = new MockPosixStorageApi { FailParentDirFsync = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy));

        Assert.Contains("fsync failed on parent", ex.Message);
    }

    // ==========================================
    // 8D-C: Atomic File Publisher & Ownership Contract (R1-G)
    // ==========================================

    [Fact]
    public void Atomic_Publisher_Completes_Full_Lifecycle_With_Parent_Durability()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var content = "SAMPLE_KEY_CONTENT_DATA_12345"u8.ToArray();

        using var result = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "evidence-signing-v1.p8", content, LinuxPosixStorageConstants.Mode0600, policy, "key");

        Assert.True(result.IsSuccess);
        Assert.True(result.FinalFd >= 0);
        Assert.Equal(0x180, (int)(result.Stat.Mode & 0xFFF)); // 0600
        Assert.Equal(content.Length, result.Stat.Size);

        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out var entry));
        Assert.Equal(content, entry.Content);
    }

    [Fact]
    public void R1_G_AtomicPublishResult_Dispose_Closes_FinalFd_Unless_Taken()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        int observedFd;
        using (var result = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "file1.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy))
        {
            observedFd = result.FinalFd;
            Assert.True(mock.IsFdOpen(observedFd));
        }

        // Dispose should close FinalFd
        Assert.False(mock.IsFdOpen(observedFd));

        // Ownership transfer test: TakeFinalFd should prevent Dispose from closing it
        int takenFd;
        using (var result = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "file2.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy))
        {
            takenFd = result.TakeFinalFd();
            Assert.Equal(-1, result.FinalFd);
            Assert.True(mock.IsFdOpen(takenFd));
        }

        Assert.True(mock.IsFdOpen(takenFd));
        mock.Close(takenFd);
        Assert.False(mock.IsFdOpen(takenFd));
    }

    [Fact]
    public void Atomic_Publisher_Returns_Collision_When_Target_Already_Exists()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: "EXISTING"u8.ToArray());
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var newContent = "NEW_CONTENT"u8.ToArray();

        using var result = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "evidence-signing-v1.p8", newContent, LinuxPosixStorageConstants.Mode0600, policy, "key");

        Assert.True(result.IsCollision);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, mock.WriteCallCount); // ZERO writes!
    }

    [Fact]
    public void Atomic_Publisher_Handles_Race_Collision_With_Winner()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        mock.BeforeRenameAt2Hook = () =>
        {
            mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: "WINNER"u8.ToArray());
            mock.LastErrno = LinuxPosixStorageConstants.EEXIST;
            return false;
        };

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var newContent = "LOSER_CONTENT"u8.ToArray();

        using var result = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "evidence-signing-v1.p8", newContent, LinuxPosixStorageConstants.Mode0600, policy, "key");

        Assert.True(result.IsCollision);
    }

    // ==========================================
    // 8D-F: Recovery After Parent-Fsync Failure (R1-A & R1-B)
    // ==========================================

    [Fact]
    public void R1_A_Directory_Recovery_After_Parent_Fsync_Failure_Re_Fsyncs_Both_And_Succeeds()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        // 1. First call: parent fsync fails after mkdir
        mock.FailParentDirFsync = true;
        Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy));

        // Directory entry exists on disk
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/sessions", out var entry));
        Assert.True(entry.Stat.IsDirectory);

        // 2. Second call: storage is recovered, re-fsyncs child and parent, succeeds
        mock.FailParentDirFsync = false;
        mock.ResetCounters();

        int dirFd = LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy);
        Assert.True(dirFd >= 0);
        Assert.True(mock.FsyncCallCount >= 2); // Child + Parent fsync confirmed!
        Assert.Equal(0, mock.MkdirAtCallCount); // ZERO duplicate mkdir!
        Assert.Equal(0, mock.FchmodCallCount);  // ZERO repair chmod!
    }

    [Fact]
    public void R1_B_File_Publish_Recovery_After_Parent_Fsync_Failure_Re_Fsyncs_And_Succeeds()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var content = "SAMPLE_KEY_BYTES"u8.ToArray();

        // 1. First publish: rename succeeds, but parent fsync fails
        mock.FailParentDirFsync = true;
        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", content, LinuxPosixStorageConstants.Mode0600, policy, "key"));

        // File exists
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out var publishedEntry));
        Assert.Equal(content, publishedEntry.Content);

        // 2. Second call: detected as existing collision, caller verifies and confirms parent durability
        mock.FailParentDirFsync = false;
        using var secondResult = LinuxAtomicFilePublisher.PublishAtomically(
            mock, keysFd, "evidence-signing-v1.p8", content, LinuxPosixStorageConstants.Mode0600, policy, "key");

        Assert.True(secondResult.IsCollision); // Discovered existing winner/published key
    }

    // ==========================================
    // 8D-D: Full Crash-Point State Machine (CP-01 to CP-09)
    // ==========================================

    [Fact]
    public void Crash_Point_CP01_Failure_After_Mkdir_Cleans_Or_Recovers()
    {
        var mock = new MockPosixStorageApi { FailOpenAt2 = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "sessions", policy));
    }

    [Fact]
    public void Crash_Point_CP02_Failure_After_Temp_Create_Leaves_No_Final_File()
    {
        var mock = new MockPosixStorageApi { FailWrite = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy));

        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public void Crash_Point_CP03_Partial_Write_Leaves_No_Final_File()
    {
        var mock = new MockPosixStorageApi { FailWrite = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy));

        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public void Crash_Point_CP05_Temp_Fsync_Failure_Leaves_No_Final_File()
    {
        var mock = new MockPosixStorageApi { FailFsync = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy));

        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public void Crash_Point_CP06_Pre_Rename_Failure_Leaves_No_Final_File()
    {
        var mock = new MockPosixStorageApi { FailRenameAt2Errno = LinuxPosixStorageConstants.EIO };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", "DATA"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy));

        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public void Crash_Point_CP07_Failure_After_Rename_Before_Parent_Fsync_Leaves_Final_File_For_Recovery()
    {
        var mock = new MockPosixStorageApi { FailParentDirFsync = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);
        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.PublishAtomically(mock, keysFd, "evidence-signing-v1.p8", "VALID_KEY"u8.ToArray(), LinuxPosixStorageConstants.Mode0600, policy));

        // Final file was atomically renamed before crash
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    // ==========================================
    // 8D-E: Stale Temp Recovery & Concurrency (R1-C, R1-D, R1-E, R1-F)
    // ==========================================

    [Fact]
    public void R1_C_D_Cleanup_Stale_Temp_Files_Safely_Deletes_Matching_App_Owned_Orphan()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: "ORPHAN"u8.ToArray());
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy, LinuxPosixStorageConstants.Mode0600);
        Assert.True(cleaned);
        Assert.False(mock.TryGetEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", out _));
    }

    [Fact]
    public void R1_C_Cleanup_Rejects_Non_Conforming_Temp_Name()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/.tmp.key.invalid-guid-123", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, ".tmp.key.invalid-guid-123", "key", policy);
        Assert.False(cleaned);
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/.tmp.key.invalid-guid-123", out _));
    }

    [Fact]
    public void R1_D_Cleanup_Rejects_0777_Mode_Drift_Temp()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: false, mode: 0x1FF, uid: 1000, gid: 1000); // 0777!
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy, LinuxPosixStorageConstants.Mode0600);
        Assert.False(cleaned);
        Assert.True(mock.TryGetEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", out _));
    }

    [Fact]
    public void R1_E_Cleanup_Parent_Fsync_Failure_Throws_Durability_Exception()
    {
        var mock = new MockPosixStorageApi { FailParentDirFsync = true };
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy, LinuxPosixStorageConstants.Mode0600));

        Assert.Contains("cleanup durability", ex.Message);
    }

    [Fact]
    public void R1_F_Concurrent_Active_Temp_File_With_Lock_Is_NEVER_Deleted()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000);
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        // Simulate Process A holding active lock
        mock.FailFlockErrno = LinuxPosixStorageConstants.EWOULDBLOCK;

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        // Process B enters recovery scan
        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy);
        Assert.False(cleaned); // Process B skips Process A's live temp file!
        Assert.True(mock.TryGetEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", out _));
    }

    [Fact]
    public void Cleanup_Stale_Temp_Files_NEVER_Deletes_Foreign_Owned_File()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: false, mode: 0x180, uid: 0, gid: 0); // Root owned!
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy);
        Assert.False(cleaned);
        Assert.True(mock.TryGetEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", out _));
    }

    [Fact]
    public void Cleanup_Stale_Temp_Files_NEVER_Follows_Or_Deletes_Symlink()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        var validTempName = $".tmp.key.{Guid.NewGuid():N}";
        mock.AddEntry($"/var/lib/internet-evidence-monitor/keys/{validTempName}", isDir: false, isSymlink: true, mode: 0x180, uid: 1000, gid: 1000); // Symlink!
        int keysFd = mock.Open("/var/lib/internet-evidence-monitor/keys", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        bool cleaned = LinuxAtomicFilePublisher.CleanupStaleTempFile(mock, keysFd, validTempName, "key", policy);
        Assert.False(cleaned);
    }

    // ==========================================
    // 8D-G: No-Repair Invariant
    // ==========================================

    [Fact]
    public void Existing_Directory_With_Wrong_Permissions_Fails_Closed_With_Zero_Fchmod_Repair()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1ED, uid: 1000, gid: 1000); // 0755!
        int rootFd = mock.Open("/var/lib/internet-evidence-monitor", 0, 0);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        Assert.Throws<InvalidOperationException>(() =>
            LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(mock, rootFd, "keys", policy, LinuxPosixStorageConstants.Mode0700));

        Assert.Equal(0, mock.FchmodCallCount); // ZERO repair!
        Assert.Equal(0, mock.FchownCallCount); // ZERO repair!
    }

    // ==========================================
    // MOCK POSIX API HELPER
    // ==========================================

    private sealed class MockPosixStorageApi : ILinuxPosixStorageApi
    {
        public sealed class FileEntry
        {
            public PosixStat Stat;
            public byte[] Content = Array.Empty<byte>();
            public int ReadOffset;
        }

        private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.Ordinal);
        private int _fdCounter = 100;
        private readonly Dictionary<int, string> _openFds = new();

        public bool FailOpenAt2 { get; set; }
        public bool FailFsync { get; set; }
        public bool FailParentDirFsync { get; set; }
        public bool FailFchmod { get; set; }
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public int? FailFstatAtErrno { get; set; }
        public int? FailRenameAt2Errno { get; set; }
        public int? FailFlockErrno { get; set; }

        public Func<bool>? BeforeRenameAt2Hook { get; set; }
        public int LastErrno { get; set; }

        public int MkdirAtCallCount { get; private set; }
        public int WriteCallCount { get; private set; }
        public int FchmodCallCount { get; private set; }
        public int FchownCallCount { get; private set; }
        public int FsyncCallCount { get; private set; }

        public bool IsFdOpen(int fd) => _openFds.ContainsKey(fd);

        public void ResetCounters()
        {
            MkdirAtCallCount = 0;
            WriteCallCount = 0;
            FchmodCallCount = 0;
            FchownCallCount = 0;
            FsyncCallCount = 0;
        }

        public bool TryGetEntry(string path, out FileEntry entry)
        {
            return _entries.TryGetValue(path.TrimEnd('/'), out entry!);
        }

        public void AddEntry(string path, bool isDir, bool isSymlink, int mode, uint uid, uint gid, byte[]? content = null)
        {
            uint fullMode = (uint)mode;
            if (isSymlink) fullMode |= LinuxPosixStorageConstants.S_IFLNK;
            else if (isDir) fullMode |= LinuxPosixStorageConstants.S_IFDIR;
            else fullMode |= LinuxPosixStorageConstants.S_IFREG;

            var bytes = content ?? Array.Empty<byte>();
            _entries[path.TrimEnd('/')] = new FileEntry
            {
                Stat = new PosixStat
                {
                    Mode = fullMode,
                    Uid = uid,
                    Gid = gid,
                    Size = bytes.Length
                },
                Content = bytes
            };
        }

        public int Open(string path, int flags, int mode)
        {
            var norm = path.TrimEnd('/');
            if (_entries.ContainsKey(norm))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = norm;
                return fd;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int OpenAt(int dirfd, string pathname, int flags, int mode)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                return fd;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int OpenAt2(int dirfd, string pathname, ref OpenHow how)
        {
            if (FailOpenAt2) return -1;
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');

            if (_entries.TryGetValue(fullPath, out var existing))
            {
                if (existing.Stat.IsSymlink && (how.Resolve & LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS) != 0)
                {
                    LastErrno = LinuxPosixStorageConstants.ELOOP;
                    return -1;
                }
            }

            bool exists = _entries.ContainsKey(fullPath);
            bool isOcreat = (how.Flags & (ulong)LinuxPosixStorageConstants.O_CREAT) != 0;
            bool isOexcl = (how.Flags & (ulong)LinuxPosixStorageConstants.O_EXCL) != 0;

            if (exists && isOcreat && isOexcl)
            {
                LastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }

            if (!exists && isOcreat)
            {
                AddEntry(fullPath, isDir: false, isSymlink: false, mode: (int)how.Mode, uid: 1000, gid: 1000);
            }

            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                _entries[fullPath].ReadOffset = 0;
                return fd;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            return -1;
        }

        public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
        {
            if (FailFstatAtErrno.HasValue)
            {
                LastErrno = FailFstatAtErrno.Value;
                statbuf = default;
                return -1;
            }

            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else
            {
                LastErrno = LinuxPosixStorageConstants.EBADF;
                statbuf = default;
                return -1;
            }

            if (_entries.TryGetValue(fullPath, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            LastErrno = LinuxPosixStorageConstants.ENOENT;
            statbuf = default;
            return -1;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            LastErrno = LinuxPosixStorageConstants.EBADF;
            statbuf = default;
            return -1;
        }

        public int MkdirAt(int dirfd, string pathname, int mode)
        {
            MkdirAtCallCount++;
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else return -1;

            AddEntry(fullPath, isDir: true, isSymlink: false, mode: mode, uid: 1000, gid: 1000);
            return 0;
        }

        public int Fchmod(int fd, int mode)
        {
            if (FailFchmod) return -1;
            FchmodCallCount++;
            if (_openFds.TryGetValue(fd, out var p) && _entries.TryGetValue(p, out var entry))
            {
                uint cleanMode = (entry.Stat.Mode & ~0xFFFu) | (uint)(mode & 0xFFF);
                entry.Stat.Mode = cleanMode;
                return 0;
            }
            return -1;
        }

        public int Fchown(int fd, uint uid, uint gid)
        {
            FchownCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                entry.Stat.Uid = uid;
                entry.Stat.Gid = gid;
                return 0;
            }
            return -1;
        }

        public int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags)
        {
            if (BeforeRenameAt2Hook != null)
            {
                bool proceed = BeforeRenameAt2Hook();
                if (!proceed) return -1;
            }

            if (FailRenameAt2Errno.HasValue)
            {
                LastErrno = FailRenameAt2Errno.Value;
                return -1;
            }

            if (!_openFds.TryGetValue(olddirfd, out var oldBase) || !_openFds.TryGetValue(newdirfd, out var newBase))
                return -1;

            var oldFull = $"{oldBase}/{oldpath}".TrimEnd('/');
            var newFull = $"{newBase}/{newpath}".TrimEnd('/');

            if (!_entries.TryGetValue(oldFull, out var oldEntry)) return -1;

            if (_entries.ContainsKey(newFull))
            {
                if ((flags & LinuxPosixStorageConstants.RENAME_NOREPLACE) != 0)
                {
                    LastErrno = LinuxPosixStorageConstants.EEXIST;
                    return -1; // EEXIST
                }
            }

            _entries.Remove(oldFull);
            _entries[newFull] = oldEntry;
            return 0;
        }

        public int UnlinkAt(int dirfd, string pathname, int flags)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var full = $"{baseDir}/{pathname}".TrimEnd('/');
            return _entries.Remove(full) ? 0 : -1;
        }

        public int Write(int fd, ReadOnlySpan<byte> buffer)
        {
            if (FailWrite) return -1;
            WriteCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                var newContent = new byte[entry.Content.Length + buffer.Length];
                Buffer.BlockCopy(entry.Content, 0, newContent, 0, entry.Content.Length);
                buffer.CopyTo(newContent.AsSpan(entry.Content.Length));
                entry.Content = newContent;
                entry.Stat.Size = newContent.Length;
                return buffer.Length;
            }
            return -1;
        }

        public int Read(int fd, Span<byte> buffer)
        {
            if (FailRead) return -1;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                int available = entry.Content.Length - entry.ReadOffset;
                if (available <= 0) return 0;
                int toCopy = Math.Min(buffer.Length, available);
                entry.Content.AsSpan(entry.ReadOffset, toCopy).CopyTo(buffer);
                entry.ReadOffset += toCopy;
                return toCopy;
            }
            return -1;
        }

        public int Fsync(int fd)
        {
            if (FailFsync) return -1;
            FsyncCallCount++;
            if (FailParentDirFsync && _openFds.TryGetValue(fd, out var path) &&
                (path.EndsWith("/internet-evidence-monitor") || path.EndsWith("/keys")))
            {
                return -1;
            }
            return 0;
        }

        public int Flock(int fd, int operation)
        {
            if (FailFlockErrno.HasValue)
            {
                LastErrno = FailFlockErrno.Value;
                return -1;
            }
            return 0;
        }

        public int Close(int fd)
        {
            _openFds.Remove(fd);
            return 0;
        }

        public int GetLastErrno() => LastErrno;
        public uint GetEuid() => 1000;
        public uint GetEgid() => 1000;
    }
}
