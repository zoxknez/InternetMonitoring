using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IEM.Storage;

/// <summary>
/// A standing instruction to take one speed measurement at a given moment.
/// <para>
/// The same shape as <see cref="SessionRequest"/>, and for the same reason. A measurement
/// scheduled inside a window or a console dies with the process that scheduled it - so the
/// one case scheduling exists for, "measure at three in the morning when nobody is awake to
/// press the button", was exactly the case it could not serve. Written to disk beside the
/// sessions, the instruction outlives the window that made it, survives a reboot, and is
/// carried out by the service under an account that is always there.
/// </para>
/// <para>
/// The file is removed once the measurement has been taken or refused - not before, so a
/// machine that restarts while the request is pending still honours it.
/// </para>
/// </summary>
/// <param name="DueAtUtc">
/// When to measure. Stored as an absolute moment rather than a delay, so a reboot in the
/// meantime cannot restart the countdown - "za dva sata" written before an hour-long reboot
/// is still due one hour from now, not two.
/// </param>
/// <param name="ContractedDownloadMbps">What the customer pays for, for the verdict. Optional.</param>
/// <param name="ContractedUploadMbps">The same for the sending direction. Optional.</param>
/// <param name="MeasureUpload">False leaves the sending half out, halving the data it costs.</param>
public sealed record SpeedRequest(
    DateTimeOffset DueAtUtc,
    double? ContractedDownloadMbps = null,
    double? ContractedUploadMbps = null,
    string? Interface = null,
    bool MeasureUpload = true)
{
    private const string FileName = "merenje.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// How long past its moment a request is still worth carrying out.
    /// <para>
    /// A machine switched off overnight comes back to a request whose moment passed hours
    /// ago. Measuring then would file a figure timestamped now against an instruction that
    /// meant three in the morning, which is a quietly misleading record - and the whole
    /// point of scheduling was the hour. Past this it is dropped and said so.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public static string PathFor(string outputRoot) => Path.Combine(outputRoot, FileName);

    /// <summary>Whether the moment has come, allowing for the machine having been asleep.</summary>
    public bool IsDue(DateTimeOffset now) => now >= DueAtUtc;

    /// <summary>Whether the moment has passed by more than the measurement would still mean.</summary>
    public bool IsStale(DateTimeOffset now) => now > DueAtUtc + Grace;

    /// <summary>Reads the pending request, or null if there is none.</summary>
    public static SpeedRequest? Read(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var path = PathFor(outputRoot);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredRequest>(File.ReadAllText(path), JsonOptions);

            if (stored is null ||
                !DateTimeOffset.TryParse(
                    stored.DueAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var due))
            {
                return null;
            }

            return new SpeedRequest(
                due,
                stored.ContractedDownloadMbps,
                stored.ContractedUploadMbps,
                stored.Interface,
                stored.MeasureUpload);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt request is treated as no request, exactly as for sessions. Guessing
            // at what someone meant and then measuring on that guess would be worse.
            return null;
        }
    }

    public void Write(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        Directory.CreateDirectory(outputRoot);

        var stored = new StoredRequest(
            DueAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ContractedDownloadMbps,
            ContractedUploadMbps,
            Interface,
            MeasureUpload);

        File.WriteAllText(
            PathFor(outputRoot),
            JsonSerializer.Serialize(stored, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Removes the request once the measurement has been taken, or refused.</summary>
    public static void Clear(string outputRoot)
    {
        var path = PathFor(outputRoot);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leaving a stale request behind is recoverable; throwing here would turn a
            // completed measurement into a failure.
        }
    }

    private sealed record StoredRequest(
        string DueAtUtc,
        double? ContractedDownloadMbps,
        double? ContractedUploadMbps,
        string? Interface,
        bool MeasureUpload);
}
