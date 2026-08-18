using System.Net.Sockets;

namespace IEM.Core.Speed;

/// <summary>Whether the sockets went where the measurement says they went.</summary>
public enum PathAgreementState
{
    /// <summary>
    /// Not established - no connection was observed, none resolved to an adapter, or there was
    /// no adapter named to compare against. The default, and never a pass.
    /// </summary>
    Unknown = 0,

    /// <summary>Every observed connection left through the adapter the measurement names.</summary>
    Match,

    /// <summary>At least one observed connection left through a different adapter.</summary>
    Mismatch,
}

/// <summary>
/// What follows from the connections that were observed.
/// <para>
/// An inference, and kept apart from the facts it rests on. <see cref="ConnectionAttempt"/>
/// records that a socket had these endpoints; this says what that means about the adapter the
/// figure is filed against. The distinction is not pedantry: the endpoints stay true forever,
/// while this conclusion depends on rules that can change - and mixing the two in one record is
/// what let a measurement from 2.6 carry its verdict into a version that had corrected it.
/// </para>
/// <para>
/// Deliberately independent of anything that looks like tunnel detection. Comparing interface
/// identifiers is an observation; "this looks like a VPN" is a guess, and a guess must not be
/// able to change the answer to a question the observation already settles.
/// </para>
/// </summary>
public sealed record PathAgreement
{
    public static readonly PathAgreement NotObserved = new();

    public PathAgreementState State { get; init; } = PathAgreementState.Unknown;

    /// <summary>The adapter the measurement claims to describe.</summary>
    public string? RequestedInterfaceId { get; init; }

    /// <summary>Every connection the measurement opened, as observed.</summary>
    public IReadOnlyList<ConnectionAttempt> Attempts { get; init; } = [];

    /// <summary>Connections that left through some other adapter.</summary>
    public IEnumerable<ConnectionAttempt> Elsewhere => Attempts.Where(attempt =>
        attempt.Observed is { } via &&
        RequestedInterfaceId is { } requested &&
        !string.Equals(via.Id, requested, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connections whose local address matched no adapter on this machine.</summary>
    public int UnresolvedCount => Attempts.Count(attempt => attempt.Observed is null);

    /// <summary>The address families that actually carried traffic.</summary>
    public IReadOnlyList<AddressFamily> Families =>
        [.. Attempts.Select(attempt => attempt.Family).Distinct()];

    /// <summary>
    /// Compares what was observed against the adapter the measurement is filed under.
    /// </summary>
    /// <param name="requestedInterfaceId">
    /// The adapter the figure will be attributed to. Null means nothing was named, so there is
    /// nothing to agree or disagree with - the connections are still recorded.
    /// </param>
    public static PathAgreement Of(string? requestedInterfaceId, IReadOnlyList<ConnectionAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var agreement = new PathAgreement
        {
            RequestedInterfaceId = requestedInterfaceId,
            Attempts = attempts,
        };

        if (string.IsNullOrWhiteSpace(requestedInterfaceId))
        {
            return agreement;
        }

        var resolved = attempts.Where(attempt => attempt.Observed is not null).ToArray();

        if (resolved.Length == 0)
        {
            // Sockets may well have connected; none of them could be tied to an adapter. That
            // is the absence of a finding, not a finding.
            return agreement;
        }

        var matching = resolved.Count(attempt =>
            string.Equals(attempt.Observed!.Id, requestedInterfaceId, StringComparison.OrdinalIgnoreCase));

        return agreement with
        {
            State = matching == resolved.Length ? PathAgreementState.Match : PathAgreementState.Mismatch,
        };
    }
}
