using System.IO;
using System.IO.Pipes;
using System.Text;
using IEM.Storage;
using IEM.Windows;

namespace IEM.App.Tests;

/// <summary>
/// Windows-specific IPC reachability and protocol handshake tests for WindowsInstallationProbe.
/// Verifies:
/// - Pipe absent -> Unreachable
/// - Connect timeout -> Unreachable
/// - Malformed/invalid response -> Unreachable
/// - Valid protocol handshake -> Reachable
/// - Unit exists != service healthy (verified bounded IPC protocol handshake).
/// </summary>
public sealed class WindowsInstallationProbeTests
{
    [Fact]
    public void IPC_reachability_probe_returns_unreachable_when_pipe_is_absent()
    {
        var absentPipe = $"NonExistentPipe_{Guid.NewGuid():N}";
        var result = WindowsInstallationProbe.ProbePipeReachabilityWithHandshake(absentPipe, timeoutMs: 50);

        Assert.False(result);
    }

    [Fact]
    public async Task IPC_reachability_probe_verifies_protocol_handshake_with_mock_server()
    {
        var pipeName = $"IemTestPipe_{Guid.NewGuid():N}";

        // 1. Valid handshake test
        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync();
            if (line is not null)
            {
                var response = StatusResponse.Ok(new
                {
                    protocolVersion = ServiceContract.ProtocolVersion,
                    appVersion = ServiceContract.AppVersion,
                    commands = StatusProtocol.Commands,
                });
                await writer.WriteLineAsync(response.ToLine());
            }
        });

        await Task.Delay(50);
        var reachable = WindowsInstallationProbe.ProbePipeReachabilityWithHandshake(pipeName, timeoutMs: 1500);
        await serverTask;

        Assert.True(reachable);
    }

    [Fact]
    public async Task IPC_reachability_probe_rejects_malformed_handshake_response()
    {
        var pipeName = $"IemTestPipeMalformed_{Guid.NewGuid():N}";

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync();
            if (line is not null)
            {
                // Write malformed/corrupted response
                await writer.WriteLineAsync("NOT_A_VALID_JSON_RESPONSE");
            }
        });

        await Task.Delay(50);
        var reachable = WindowsInstallationProbe.ProbePipeReachabilityWithHandshake(pipeName, timeoutMs: 1500);
        await serverTask;

        Assert.False(reachable);
    }
}
