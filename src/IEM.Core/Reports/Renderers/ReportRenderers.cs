using System.Globalization;
using System.Text;

namespace IEM.Core.Reports.Renderers;

/// <summary>
/// Renderer capabilities descriptor.
/// Invariant 143: RENDERER_LIMITATION_NEVER_CHANGES_OR_INVENTS_EVIDENCE_MEANING.
/// </summary>
public sealed record RendererCapabilities(
    bool SupportsNarrative,
    bool SupportsTables,
    bool SupportsTimelines,
    bool SupportsEvidenceReferences,
    bool SupportsPagination,
    bool SupportsRichIntegritySection);

/// <summary>
/// Common contract for report document renderers.
/// Invariants:
/// 132. REPORT_RENDERERS_NEVER_CONTAIN_EVIDENCE_BUSINESS_LOGIC
/// 147. RENDERING_IS_STRICTLY_READ_ONLY_WITH_RESPECT_TO_REPORT_AND_EVIDENCE_MODELS
/// </summary>
public interface IReportRenderer
{
    string RendererId { get; }
    string OutputMimeType { get; }
    RendererCapabilities Capabilities { get; }

    string RenderToString(ReportDocumentModel model, CultureInfo? culture = null);
}

/// <summary>
/// HTML5 report renderer.
/// </summary>
public sealed class HtmlReportRenderer : IReportRenderer
{
    public string RendererId => "HtmlReportRenderer-v1";
    public string OutputMimeType => "text/html; charset=utf-8";

    public RendererCapabilities Capabilities { get; } = new(
        SupportsNarrative: true,
        SupportsTables: true,
        SupportsTimelines: true,
        SupportsEvidenceReferences: true,
        SupportsPagination: false,
        SupportsRichIntegritySection: true);

    public string RenderToString(ReportDocumentModel model, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        culture ??= CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"sr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine($"  <title>{model.Title}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: system-ui, -apple-system, sans-serif; line-height: 1.5; padding: 2rem; color: #1e293b; }");
        sb.AppendLine("    .metric { display: flex; justify-content: space-between; padding: 0.5rem 0; border-bottom: 1px solid #e2e8f0; }");
        sb.AppendLine("    .claim { margin: 0.5rem 0; padding: 0.75rem; background: #f8fafc; border-left: 4px solid #3b82f6; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 0.2rem 0.5rem; font-size: 0.8rem; font-weight: bold; border-radius: 4px; background: #e0e7ff; color: #3730a3; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"  <h1>{model.Title}</h1>");
        if (!string.IsNullOrEmpty(model.Subtitle))
        {
            sb.AppendLine($"  <h3>{model.Subtitle}</h3>");
        }

        foreach (var section in model.Sections)
        {
            sb.AppendLine($"  <section id=\"{section.SectionId}\">");
            foreach (var block in section.Blocks)
            {
                RenderBlockToHtml(sb, block, culture);
            }
            sb.AppendLine("  </section>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void RenderBlockToHtml(StringBuilder sb, ReportBlock block, CultureInfo culture)
    {
        switch (block)
        {
            case HeadingBlock h:
                sb.AppendLine($"    <h{h.Level}>{h.Text}</h{h.Level}>");
                break;
            case ParagraphBlock p:
                sb.AppendLine($"    <p>{p.Text}</p>");
                break;
            case MetricBlock m:
                sb.AppendLine($"    <div class=\"metric\"><span>{m.Label}:</span> <strong>{m.Value.Format(culture)}</strong></div>");
                break;
            case ClaimBlock c:
                sb.AppendLine($"    <div class=\"claim\">");
                sb.AppendLine($"      <span class=\"badge\">[{c.Claim.EpistemicClass.ToString().ToUpperInvariant()}]</span>");
                sb.AppendLine($"      <span>{c.Claim.StatementKey}</span>");
                if (c.Claim.StructuredValue != null)
                {
                    sb.AppendLine($"      <strong>{c.Claim.StructuredValue.Format(culture)}</strong>");
                }
                sb.AppendLine($"    </div>");
                break;
            case QualityBadgeBlock q:
                sb.AppendLine($"    <div class=\"badge\">Kvalitet: {q.Band} ({q.SummaryReason})</div>");
                break;
            case IntegrityNoticeBlock i:
                sb.AppendLine($"    <div class=\"claim\">Integritet: <strong>{i.IntegrityState}</strong> | Poverenje: <strong>{i.TrustState}</strong></div>");
                break;
            case TimelineBlock t:
                sb.AppendLine("    <ul>");
                foreach (var entry in t.Entries)
                {
                    sb.AppendLine($"      <li>[{entry.Category}] {entry.StartUtc:HH:mm:ss} - {entry.EndUtc:HH:mm:ss}: {entry.Description}</li>");
                }
                sb.AppendLine("    </ul>");
                break;
        }
    }
}

/// <summary>
/// CSV projection renderer for structured datasets.
/// Invariant 143: RENDERER_LIMITATION_NEVER_CHANGES_OR_INVENTS_EVIDENCE_MEANING.
/// </summary>
public sealed class CsvReportProjection : IReportRenderer
{
    public string RendererId => "CsvReportProjection-v1";
    public string OutputMimeType => "text/csv; charset=utf-8";

    public RendererCapabilities Capabilities { get; } = new(
        SupportsNarrative: false,
        SupportsTables: true,
        SupportsTimelines: false,
        SupportsEvidenceReferences: true,
        SupportsPagination: false,
        SupportsRichIntegritySection: false);

    public string RenderToString(ReportDocumentModel model, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        culture ??= CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine("ClaimId,ClaimKind,EpistemicClass,StatementKey,Value,Unit,SupportState");

        foreach (var section in model.Sections)
        {
            foreach (var block in section.Blocks)
            {
                if (block is ClaimBlock c)
                {
                    var val = c.Claim.StructuredValue?.Format(culture) ?? "Unknown";
                    sb.AppendLine($"\"{c.Claim.ClaimId}\",\"{c.Claim.ClaimKind}\",\"{c.Claim.EpistemicClass}\",\"{c.Claim.StatementKey}\",\"{val}\",\"{c.Claim.StructuredValue?.Unit ?? ""}\",\"{c.Claim.SupportState}\"");
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Formal complaint composer.
/// Invariant 144: NARRATIVE_TEMPLATE_NEVER_STRENGTHENS_THE_UNDERLYING_CLAIM.
/// </summary>
public sealed class ComplaintNarrativeComposer : IReportRenderer
{
    public string RendererId => "ComplaintNarrativeComposer-v1";
    public string OutputMimeType => "text/plain; charset=utf-8";

    public RendererCapabilities Capabilities { get; } = new(
        SupportsNarrative: true,
        SupportsTables: false,
        SupportsTimelines: true,
        SupportsEvidenceReferences: true,
        SupportsPagination: false,
        SupportsRichIntegritySection: true);

    public string RenderToString(ReportDocumentModel model, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        culture ??= CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine("PRIGOVOR NA KVALITET I KONTINUITET USLUGE");
        sb.AppendLine("===========================================");
        sb.AppendLine($"Sesija merenja: {model.SessionRef}");
        sb.AppendLine($"Datum i vreme generisanja: {model.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("1. PREGLED I ČINJENIČNO STANJE:");
        sb.AppendLine(model.Summary);
        sb.AppendLine();

        sb.AppendLine("2. ZABELEŽENI NALAZI I DOKAZI:");
        foreach (var section in model.Sections)
        {
            foreach (var block in section.Blocks)
            {
                if (block is ClaimBlock c)
                {
                    var val = c.Claim.StructuredValue == null || c.Claim.StructuredValue.Kind == ReportValueKind.Unknown
                        ? "Nije utvrđeno (Unknown)"
                        : c.Claim.StructuredValue.Format(culture);
                    sb.AppendLine($"- [{c.Claim.EpistemicClass}]: {c.Claim.StatementKey} -> {val} (Podrška: {c.Claim.SupportState})");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("3. INTEGRITET DOKAZNOG MATERIJALA:");
        sb.AppendLine($"Kriptografski integritet paketa: {model.IntegrityState}");
        sb.AppendLine($"Poverenje vremenskog žiga (TSA): {model.TrustState}");
        sb.AppendLine($"Ukupni kvalitet dokaza: {model.OverallQualityBand}");
        sb.AppendLine();
        sb.AppendLine("Napomena: Ovaj prigovor sadrži isključivo objektivno izmerene parametre bez pretpostavljenih ili nedokazanih tvrdnji o uzroku.");

        return sb.ToString();
    }
}

/// <summary>
/// RATEL regulatory submission composer.
/// Invariant 134: DOCUMENT_PURPOSE_MAY_CHANGE_COMPOSITION_BUT_NEVER_EVIDENCE_SEMANTICS.
/// </summary>
public sealed class RatelRegulatoryComposer : IReportRenderer
{
    public string RendererId => "RatelRegulatoryComposer-SR-v1";
    public string OutputMimeType => "text/plain; charset=utf-8";

    public RendererCapabilities Capabilities { get; } = new(
        SupportsNarrative: true,
        SupportsTables: true,
        SupportsTimelines: true,
        SupportsEvidenceReferences: true,
        SupportsPagination: false,
        SupportsRichIntegritySection: true);

    public string RenderToString(ReportDocumentModel model, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        culture ??= CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine("REGULATORNO PODNOŠENJE - RATEL PROTOKOL MERENJA");
        sb.AppendLine("================================================");
        sb.AppendLine($"Identifikator dokazne sesije: {model.SessionRef}");
        sb.AppendLine($"Profil: {model.Provenance.CompositionProfileRef}");
        sb.AppendLine($"Schema verzija: {model.DocumentSchemaVersion}");
        sb.AppendLine();
        sb.AppendLine("ZABELEŽENE ČINJENICE I MERENJA:");

        foreach (var section in model.Sections)
        {
            foreach (var block in section.Blocks)
            {
                if (block is ClaimBlock c)
                {
                    var val = c.Claim.StructuredValue?.Format(culture) ?? "Unknown";
                    sb.AppendLine($"[RATEL-EVIDENCE-{c.Claim.EpistemicClass}] {c.Claim.StatementKey}: {val}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"INTEGRITET: {model.IntegrityState} | TRUST: {model.TrustState}");
        return sb.ToString();
    }
}
