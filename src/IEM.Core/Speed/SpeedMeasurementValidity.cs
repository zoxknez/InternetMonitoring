using IEM.Core.Model;

namespace IEM.Core.Speed;

/// <summary>Why a speed measurement cannot be used to prove a contracted rate.</summary>
public enum SpeedMeasurementDefect
{
    /// <summary>
    /// Measured over Wi-Fi.
    /// <para>
    /// The single most common reason a speed complaint is rejected, and rightly so: the
    /// radio link between the computer and the router is the customer's own, and it is
    /// routinely the slowest part of the path. An operator asked to answer for a figure
    /// measured over Wi-Fi will point at the Wi-Fi, and they will be right to.
    /// </para>
    /// </summary>
    NotWired,

    /// <summary>
    /// The negotiated link rate is at or below the contracted speed.
    /// <para>
    /// A 100 Mbit/s port cannot demonstrate a 200 Mbit/s service however slow the line is.
    /// The measurement would show the cable, not the connection.
    /// </para>
    /// </summary>
    LinkSlowerThanContract,

    /// <summary>The machine was using the connection for something else at the time.</summary>
    LinkBusy,

    /// <summary>The connection was not working properly while the measurement ran.</summary>
    ConnectionDegraded,

    /// <summary>Some of the measurement's traffic was leaving through another adapter or a VPN.</summary>
    PathAmbiguous,

    /// <summary>Every route that could be resolved left through an adapter other than this one.</summary>
    PathElsewhere,

    /// <summary>
    /// Where the traffic went could not be established at all.
    /// <para>
    /// Separate from <see cref="PathAmbiguous"/> on purpose. An ambiguous path is a finding
    /// about the machine; an unverified one is the absence of a finding, and the two need
    /// different advice. Both stop the measurement from supporting a complaint, because a
    /// figure whose path nobody could check is a figure about an unknown link.
    /// </para>
    /// </summary>
    PathUnverified,

    /// <summary>The contracted speed was never entered, so there is nothing to compare against.</summary>
    ContractUnknown,
}

/// <param name="ContractedDownloadMbps">What the customer is paying for.</param>
/// <param name="MeasuredDownloadMbps">What was measured.</param>
public sealed record SpeedMeasurementConditions(
    LinkMedium Medium,
    long? LinkSpeedBitsPerSecond,
    double? ContractedDownloadMbps,
    double MeasuredDownloadMbps)
{
    /// <summary>The link was quiet enough that the measurement had the connection to itself.</summary>
    public bool LinkWasIdle { get; init; } = true;

    /// <summary>Nothing was failing while the measurement ran.</summary>
    public bool ConnectionHealthy { get; init; } = true;

    /// <summary>
    /// What the route table said about the measurement's own traffic.
    /// <para>
    /// Defaults to <see cref="MeasurementRouteState.Unknown"/>, which costs the measurement
    /// its standing until somebody checks. That is the point: it used to default to "one
    /// path, no VPN", so every caller that never looked recorded a verified path it had
    /// never verified.
    /// </para>
    /// </summary>
    public MeasurementRouteState RouteState { get; init; } = MeasurementRouteState.Unknown;

    /// <summary>
    /// What the customer is paying for in the sending direction, when the contract states it
    /// separately - which for cable and DSL it nearly always does.
    /// </summary>
    public double? ContractedUploadMbps { get; init; }

    /// <summary>What was measured in the sending direction, when that half ran.</summary>
    public double? MeasuredUploadMbps { get; init; }
}

/// <summary>
/// Whether a speed measurement can stand behind a complaint about contracted speed.
/// <para>
/// Deliberately separate from the outage evidence, and the distinction matters. An outage
/// recorded over Wi-Fi is perfectly good evidence - the connection was down, and how the
/// computer was attached to the router does not change that. A <em>speed</em> measured over
/// Wi-Fi proves nothing about the service, because the slowest link in the path is the one
/// the customer owns.
/// </para>
/// <para>
/// Rolling the two together would either throw away valid outage evidence from laptops, or
/// let a Wi-Fi speed figure into a complaint that the operator can dismiss in one sentence.
/// </para>
/// </summary>
public sealed record SpeedMeasurementValidity(
    bool IsValidForComplaint,
    IReadOnlyList<SpeedMeasurementDefect> Defects)
{
    /// <summary>
    /// Speed bands used when judging a measurement against a contract.
    /// <para>
    /// Expressed as shares of the contracted rate. A service is not expected to deliver its
    /// headline figure at every moment; what matters is whether it stays above the floor,
    /// and how far below the usual level it sits.
    /// </para>
    /// <para>
    /// The floor and the usual level are not inventions: the Pravilnik o parametrima kvaliteta
    /// sets the minimum speed at 70 % of the contracted maximum and the normally available
    /// speed at 80 % of it <em>over 90 % of measurement time</em>. That last condition is why
    /// a single measurement can only place a figure in a band and never conclude that the
    /// service is normally available - a conclusion about a share of time needs a series.
    /// </para>
    /// <para>
    /// The shares are the same under both the current pravilnik ("Sl. glasnik RS" 82/2024,
    /// applied from three months after it entered force on 19 October 2024) and the one it
    /// replaced (23/2023), so they are stated once here. Which of the two a given measurement
    /// is judged under is decided by its date, in <c>LegalSources.QualityPravilnikOn</c>,
    /// because a report on an old measurement has to cite the rules that governed it.
    /// </para>
    /// </summary>
    public const double MinimumShare = 0.70;

    public const double NormallyAvailableShare = 0.80;

    public const double AdvertisedShare = 0.90;

    public static SpeedMeasurementValidity Of(SpeedMeasurementConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        var defects = new List<SpeedMeasurementDefect>();

        if (conditions.Medium != LinkMedium.Ethernet)
        {
            defects.Add(SpeedMeasurementDefect.NotWired);
        }

        if (conditions.ContractedDownloadMbps is not { } contracted)
        {
            defects.Add(SpeedMeasurementDefect.ContractUnknown);
        }
        else if (conditions.LinkSpeedBitsPerSecond is { } linkBits &&
                 linkBits / 1_000_000d <= contracted)
        {
            defects.Add(SpeedMeasurementDefect.LinkSlowerThanContract);
        }

        if (!conditions.LinkWasIdle)
        {
            defects.Add(SpeedMeasurementDefect.LinkBusy);
        }

        if (!conditions.ConnectionHealthy)
        {
            defects.Add(SpeedMeasurementDefect.ConnectionDegraded);
        }

        // Only a route table that agrees on every candidate lets the measurement stand. The
        // other three states are each their own defect rather than one shared "path problem",
        // because "we could not check" and "it went out of the VPN" are answered differently.
        switch (conditions.RouteState)
        {
            case MeasurementRouteState.MixedRoutes:
                defects.Add(SpeedMeasurementDefect.PathAmbiguous);
                break;

            case MeasurementRouteState.OtherRouteOnly:
                defects.Add(SpeedMeasurementDefect.PathElsewhere);
                break;

            case MeasurementRouteState.Unknown:
                defects.Add(SpeedMeasurementDefect.PathUnverified);
                break;

            default:
                break;
        }

        return new SpeedMeasurementValidity(defects.Count == 0, defects);
    }

    /// <summary>
    /// Where the measurement falls against the contract, or null when there is no contract
    /// to compare against.
    /// </summary>
    public static SpeedBand? Judge(SpeedMeasurementConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (conditions.ContractedDownloadMbps is not { } contracted || contracted <= 0)
        {
            return null;
        }

        return Band(conditions.MeasuredDownloadMbps / contracted);
    }

    /// <summary>
    /// The same judgement for the sending direction, or null when either the contracted or
    /// the measured figure is missing.
    /// <para>
    /// Judged by the same shares, because the Pravilnik sets them for the contracted speed
    /// without distinguishing direction - and separately, because a connection that meets
    /// its download figure while failing its upload is a real and common complaint that a
    /// download-only measurement cannot state at all.
    /// </para>
    /// </summary>
    public static SpeedBand? JudgeUpload(SpeedMeasurementConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (conditions.ContractedUploadMbps is not { } contracted || contracted <= 0 ||
            conditions.MeasuredUploadMbps is not { } measured)
        {
            return null;
        }

        return Band(measured / contracted);
    }

    private static SpeedBand Band(double share) => share switch
    {
        >= AdvertisedShare => SpeedBand.AtAdvertised,
        >= NormallyAvailableShare => SpeedBand.NormallyAvailable,
        >= MinimumShare => SpeedBand.AboveMinimum,
        _ => SpeedBand.BelowMinimum,
    };
}

/// <summary>Where a measured rate sits relative to what was contracted.</summary>
public enum SpeedBand
{
    /// <summary>Below the floor. This is the case a complaint is built on.</summary>
    BelowMinimum,

    /// <summary>Above the floor but below the level normally expected.</summary>
    AboveMinimum,

    /// <summary>At the level normally expected.</summary>
    NormallyAvailable,

    /// <summary>At or near the advertised rate.</summary>
    AtAdvertised,
}
