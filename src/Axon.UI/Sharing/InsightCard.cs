namespace Axon.UI.Sharing;

/// <summary>
/// Immutable input model for a shareable insight card.
/// </summary>
/// <param name="Title">
/// Short eyebrow / category label displayed above the headline
/// (e.g. "Weekly Summary", "Resting HR").
/// </param>
/// <param name="Headline">
/// The primary statistic or achievement — rendered large
/// (e.g. "12,847 steps", "52 bpm").
/// </param>
/// <param name="Subtitle">
/// Optional supporting line beneath the headline
/// (e.g. "Personal best this month"). Omit with <see langword="null"/>.
/// </param>
/// <param name="Watermark">
/// Footer attribution text. Defaults to <c>"Analyzed locally by Axon"</c>.
/// </param>
public sealed record InsightCard(
    string Title,
    string Headline,
    string? Subtitle,
    string Watermark = "Analyzed locally by Axon");
