using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IEM.Linux.Wifi;

/// <summary>
/// Production AF_NETLINK socket client for Generic Netlink (NETLINK_GENERIC = 16) and nl80211.
/// Invariants 249-254.
/// </summary>
public sealed class LinuxNl80211Socket : ILinuxNl80211Socket
{
    private static int _globalSequence;
    private Socket? _socket;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public static LinuxNl80211Socket Create() => new();

    private void EnsureSocket()
    {
        if (_socket is not null && _socket.Connected)
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
            // AF_NETLINK = 16, SOCK_RAW = 3, NETLINK_GENERIC = 16
            _socket = new Socket((AddressFamily)16, SocketType.Raw, (ProtocolType)LinuxGenlProtocol.NETLINK_GENERIC)
            {
                ReceiveTimeout = 3000,
                SendTimeout = 3000
            };
        }
        catch (Exception)
        {
            _socket?.Dispose();
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

            await _socket.SendAsync(req, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            var recvBuffer = new byte[8192];
            var bytesRead = await _socket.ReceiveAsync(recvBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            if (bytesRead <= 0)
            {
                return null;
            }

            var ret = LinuxGenlProtocol.ParseGetFamilyResponse(recvBuffer.AsSpan(0, bytesRead), seq, out var familyInfo);
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

    public async Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(
        ushort nl80211FamilyId,
        int? ifindex = null,
        CancellationToken cancellationToken = default)
    {
        var interfaces = new List<LinuxNl80211InterfaceInfo>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || nl80211FamilyId == 0)
        {
            return interfaces;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSocket();
            if (_socket is null)
            {
                return interfaces;
            }

            var seq = (uint)Interlocked.Increment(ref _globalSequence);
            var req = LinuxNl80211Protocol.BuildGetInterfaceRequest(nl80211FamilyId, ifindex, seq);

            await _socket.SendAsync(req, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            var combinedStream = new MemoryStream();
            var recvBuffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await _socket.ReceiveAsync(recvBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                combinedStream.Write(recvBuffer, 0, bytesRead);

                // Check if last message was NLMSG_DONE or NLMSG_ERROR
                var span = recvBuffer.AsSpan(0, bytesRead);
                if (IsEndOfMultiPart(span))
                {
                    break;
                }

                if (!ifindex.HasValue)
                {
                    // For dump requests, continue receiving until NLMSG_DONE
                    if (_socket.Available == 0 && combinedStream.Length > 0 && IsEndOfMultiPart(combinedStream.ToArray()))
                    {
                        break;
                    }
                }
                else
                {
                    // Single query response is complete after 1 receive
                    break;
                }
            }

            var totalBytes = combinedStream.ToArray();
            if (totalBytes.Length > 0)
            {
                LinuxNl80211Protocol.ParseInterfaceResponse(totalBytes, seq, out interfaces);
            }

            return interfaces;
        }
        catch (Exception)
        {
            return interfaces;
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
        var wiphys = new List<LinuxNl80211WiphyInfo>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || nl80211FamilyId == 0)
        {
            return wiphys;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSocket();
            if (_socket is null)
            {
                return wiphys;
            }

            var seq = (uint)Interlocked.Increment(ref _globalSequence);
            var req = LinuxNl80211Protocol.BuildGetWiphyRequest(nl80211FamilyId, wiphyIndex, seq);

            await _socket.SendAsync(req, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            var combinedStream = new MemoryStream();
            var recvBuffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await _socket.ReceiveAsync(recvBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                combinedStream.Write(recvBuffer, 0, bytesRead);

                var span = recvBuffer.AsSpan(0, bytesRead);
                if (IsEndOfMultiPart(span))
                {
                    break;
                }

                if (wiphyIndex.HasValue)
                {
                    break;
                }
            }

            var totalBytes = combinedStream.ToArray();
            if (totalBytes.Length > 0)
            {
                LinuxNl80211Protocol.ParseWiphyResponse(totalBytes, seq, out wiphys);
            }

            return wiphys;
        }
        catch (Exception)
        {
            return wiphys;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static bool IsEndOfMultiPart(ReadOnlySpan<byte> buffer)
    {
        int offset = 0;
        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                break;
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE || nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                return true;
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        return false;
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
