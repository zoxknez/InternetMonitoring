using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using IEM.Linux.Network.Netlink;

namespace IEM.Linux.Wifi;

/// <summary>
/// Production AF_NETLINK socket client for Generic Netlink (NETLINK_GENERIC = 16) and nl80211.
/// Invariants 249-254.
/// </summary>
public sealed class LinuxNl80211Socket : ILinuxNl80211Socket
{
    private static int _globalSequence;
    private LinuxNativeNetlinkSocket? _socket;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public static LinuxNl80211Socket Create() => new();

    private void EnsureSocket()
    {
        if (_socket is not null && _socket.IsOpen)
        {
            return;
        }

        _socket?.Dispose();
        _socket = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        try
        {
            _socket = LinuxNativeNetlinkSocket.Open(LinuxGenlProtocol.NETLINK_GENERIC);
        }
        catch (Exception)
        {
            _socket = null;
        }
    }

    public async Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSocket();
            if (_socket is null)
            {
                return null;
            }

            var seq = (uint)Interlocked.Increment(ref _globalSequence);
            var req = LinuxGenlProtocol.BuildGetFamilyRequest(familyName, seq);

            _socket.Send(req);

            using var combinedStream = new MemoryStream();
            var recvBuffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _socket.Receive(recvBuffer, timeoutMs: 3000);
                }
                catch (TimeoutException)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                combinedStream.Write(recvBuffer, 0, bytesRead);

                var span = recvBuffer.AsSpan(0, bytesRead);
                var (isTerminal, hasFatalError) = InspectChunk(span, seq, isDump: false);
                if (isTerminal)
                {
                    break;
                }
            }

            var totalBytes = combinedStream.ToArray();
            if (totalBytes.Length == 0)
            {
                return null;
            }

            var ret = LinuxGenlProtocol.ParseGetFamilyResponse(totalBytes, seq, out var familyInfo);
            return ret == 0 ? familyInfo : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(
        ushort nl80211FamilyId,
        int? ifindex = null,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || nl80211FamilyId == 0)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Unavailable);
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSocket();
            if (_socket is null)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Unavailable);
            }

            var seq = (uint)Interlocked.Increment(ref _globalSequence);
            var req = LinuxNl80211Protocol.BuildGetInterfaceRequest(nl80211FamilyId, ifindex, seq);

            _socket.Send(req);

            using var combinedStream = new MemoryStream();
            var recvBuffer = new byte[8192];
            bool isDump = !ifindex.HasValue;
            bool timedOut = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _socket.Receive(recvBuffer, timeoutMs: 2000);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                combinedStream.Write(recvBuffer, 0, bytesRead);

                var span = recvBuffer.AsSpan(0, bytesRead);
                var (isTerminal, hasFatalError) = InspectChunk(span, seq, isDump);
                if (isTerminal)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Cancelled);
            }

            var totalBytes = combinedStream.ToArray();
            if (totalBytes.Length == 0)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), timedOut ? LinuxNl80211DumpStatus.TimedOut : LinuxNl80211DumpStatus.Incomplete, -11);
            }

            var result = LinuxNl80211Protocol.ParseInterfaceDump(totalBytes, seq, isDump);
            if (timedOut && !result.IsComplete)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.TimedOut, -11);
            }

            return result;
        }
        catch (Exception)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Unavailable);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(
        ushort nl80211FamilyId,
        int? ifindex = null,
        CancellationToken cancellationToken = default)
    {
        var res = await DumpInterfacesAsync(nl80211FamilyId, ifindex, cancellationToken).ConfigureAwait(false);
        return new List<LinuxNl80211InterfaceInfo>(res.Items);
    }

    public async Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(
        ushort nl80211FamilyId,
        uint? wiphyIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || nl80211FamilyId == 0)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Unavailable);
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSocket();
            if (_socket is null)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Unavailable);
            }

            var seq = (uint)Interlocked.Increment(ref _globalSequence);
            var req = LinuxNl80211Protocol.BuildGetWiphyRequest(nl80211FamilyId, wiphyIndex, seq);

            _socket.Send(req);

            using var combinedStream = new MemoryStream();
            var recvBuffer = new byte[8192];
            bool isDump = !wiphyIndex.HasValue;
            bool timedOut = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _socket.Receive(recvBuffer, timeoutMs: 2000);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                combinedStream.Write(recvBuffer, 0, bytesRead);

                var span = recvBuffer.AsSpan(0, bytesRead);
                var (isTerminal, hasFatalError) = InspectChunk(span, seq, isDump);
                if (isTerminal)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Cancelled);
            }

            var totalBytes = combinedStream.ToArray();
            if (totalBytes.Length == 0)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), timedOut ? LinuxNl80211DumpStatus.TimedOut : LinuxNl80211DumpStatus.Incomplete, -11);
            }

            var result = LinuxNl80211Protocol.ParseWiphyDump(totalBytes, seq, isDump);
            if (timedOut && !result.IsComplete)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.TimedOut, -11);
            }

            return result;
        }
        catch (Exception)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Unavailable);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(
        ushort nl80211FamilyId,
        uint? wiphyIndex = null,
        CancellationToken cancellationToken = default)
    {
        var res = await DumpWiphysAsync(nl80211FamilyId, wiphyIndex, cancellationToken).ConfigureAwait(false);
        return new List<LinuxNl80211WiphyInfo>(res.Items);
    }

    private static (bool IsTerminal, bool HasFatalError) InspectChunk(ReadOnlySpan<byte> buffer, uint expectedSeq, bool isDump)
    {
        int offset = 0;
        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return (true, true);
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            if (seq != expectedSeq)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                return (true, false);
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    int error = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                    if (error < 0)
                    {
                        return (true, true);
                    }
                    // error == 0: pure ACK, do NOT terminate single query or dump!
                }
            }
            else if (!isDump)
            {
                // Single/do query: matching DATA response received
                if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
                {
                    return (true, false);
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        return (false, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket?.Dispose();
        _semaphore.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
