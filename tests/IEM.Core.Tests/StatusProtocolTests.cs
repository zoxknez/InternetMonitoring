using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// The status channel between the service and the window, tested as ordinary text.
/// <para>
/// The two ends update separately - the service through an installer that needs
/// administrator rights, the window through whatever the user runs - so a window of one
/// version talking to a service of another is the ordinary state of affairs for as long as
/// it takes somebody to get round to the second half of an update. What that must never do
/// is look like the service being down: told "not running", a user reinstalls something that
/// works perfectly well.
/// </para>
/// </summary>
public sealed class StatusProtocolTests
{
    // ---- Reading a request --------------------------------------------------------

    [Fact]
    public void A_request_carries_its_command_and_the_version_it_speaks()
    {
        var request = StatusProtocol.ParseRequest(StatusRequest.For("STATUS").ToLine());

        Assert.Equal("STATUS", request.Command);
        Assert.Equal(ServiceContract.ProtocolVersion, request.ProtocolVersion);
        Assert.True(request.SpeaksOurProtocol);
    }

    /// <summary>
    /// A bare word is accepted so the channel can be driven from a console while diagnosing,
    /// which is half the reason it is one JSON object per line rather than something binary.
    /// </summary>
    [Theory]
    [InlineData("STATUS")]
    [InlineData("status")]
    [InlineData("  STATUS  ")]
    public void A_bare_command_typed_by_hand_is_understood(string line)
    {
        var request = StatusProtocol.ParseRequest(line);

        Assert.Equal("STATUS", request.Normalised);
        Assert.True(request.SpeaksOurProtocol);
    }

    /// <summary>
    /// Absent means a client older than versioning itself. Served rather than refused: it
    /// can only be speaking version one, since there was nothing else to speak.
    /// </summary>
    [Fact]
    public void A_request_without_a_version_is_served_rather_than_refused()
    {
        var request = StatusProtocol.ParseRequest("""{"command":"LIVE"}""");

        Assert.Equal("LIVE", request.Command);
        Assert.Null(request.ProtocolVersion);
        Assert.True(request.SpeaksOurProtocol);
    }

    [Fact]
    public void A_request_from_another_protocol_version_is_recognised_as_such()
    {
        var request = StatusProtocol.ParseRequest($$"""{"command":"STATUS","protocolVersion":{{ServiceContract.ProtocolVersion + 1}}}""");

        Assert.False(request.SpeaksOurProtocol);
        Assert.Contains("Ažurirajte", request.IncompatibilityMessage, StringComparison.Ordinal);
        Assert.Contains(ServiceContract.AppVersion, request.IncompatibilityMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Answered rather than dropped. A client that gets no reply cannot tell that apart from
    /// the service being down.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{ nije json")]
    [InlineData("""{"command":42}""")]
    public void Anything_unreadable_becomes_a_request_with_no_command(string? line)
    {
        var request = StatusProtocol.ParseRequest(line);

        Assert.Null(request.Command);
        Assert.True(request.SpeaksOurProtocol);
    }

    /// <summary>A field a newer client sends must not stop an older service answering.</summary>
    [Fact]
    public void Fields_the_reader_has_never_heard_of_are_ignored()
    {
        var request = StatusProtocol.ParseRequest(
            $$"""{"command":"PING","protocolVersion":{{ServiceContract.ProtocolVersion}},"nesto":"novo"}""");

        Assert.Equal("PING", request.Command);
        Assert.True(request.SpeaksOurProtocol);
    }

    // ---- Answering ----------------------------------------------------------------

    [Fact]
    public void A_successful_reply_reaches_the_reading_end_with_its_payload()
    {
        var line = StatusResponse.Ok(new { pong = true }).ToLine();

        var envelope = StatusEnvelope<PongPayload>.Parse(line);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.True(envelope.Data?.Pong);
        Assert.False(envelope.IsIncompatible);
        Assert.Equal(ServiceContract.ProtocolVersion, envelope.ProtocolVersion);
        Assert.Equal(ServiceContract.AppVersion, envelope.AppVersion);
    }

    /// <summary>
    /// "That failed" and "we cannot usefully talk at all" call for entirely different actions
    /// from whoever is reading, so they are two different answers rather than one.
    /// </summary>
    [Fact]
    public void A_failure_and_an_incompatibility_are_told_apart()
    {
        var failed = StatusEnvelope<PongPayload>.Parse(StatusResponse.Error("Prazna komanda.").ToLine());
        var refused = StatusEnvelope<PongPayload>.Parse(StatusResponse.Refused("Druga verzija.").ToLine());

        Assert.NotNull(failed);
        Assert.False(failed.Success);
        Assert.False(failed.IsIncompatible);

        Assert.NotNull(refused);
        Assert.False(refused.Success);
        Assert.True(refused.IsIncompatible);
        Assert.Equal("Druga verzija.", refused.Message);
    }

    [Fact]
    public void An_unknown_command_is_answered_with_the_ones_that_exist()
    {
        var message = StatusResponse.Unknown("MERI").Message;

        Assert.NotNull(message);
        Assert.Contains("MERI", message, StringComparison.Ordinal);

        foreach (var command in StatusProtocol.Commands)
        {
            Assert.Contains(command, message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A reply from a service that predates versioning carries no status field at all, and a
    /// window that treated that as a mismatch would report a working service as unreachable.
    /// </summary>
    [Fact]
    public void A_reply_from_before_versioning_is_treated_as_compatible()
    {
        var envelope = StatusEnvelope<PongPayload>.Parse("""{"success":true,"data":{"pong":true}}""");

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.False(envelope.IsIncompatible);
    }

    /// <summary>And a field a newer service adds must not stop an older window reading it.</summary>
    [Fact]
    public void A_reply_with_unfamiliar_fields_still_reads()
    {
        var envelope = StatusEnvelope<PongPayload>.Parse(
            """{"success":true,"data":{"pong":true},"status":"SUPPORTED","novoPolje":123}""");

        Assert.NotNull(envelope);
        Assert.True(envelope.Data?.Pong);
    }

    /// <summary>
    /// Every command the service answers is listed where a client can ask for the list, so
    /// the two cannot quietly disagree about what exists.
    /// </summary>
    [Fact]
    public void The_advertised_commands_include_the_scheduled_measurement()
    {
        Assert.Contains("SPEED", StatusProtocol.Commands);
        Assert.Contains("STATUS", StatusProtocol.Commands);
        Assert.Contains("HELLO", StatusProtocol.Commands);
    }

    private sealed record PongPayload(bool Pong);
}
