namespace LiveCaptioner.Models;

public sealed class BilingualSubtitle
{
    public string RawText { get; init; } = string.Empty;
    public string CorrectedText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string PrimaryText => string.IsNullOrWhiteSpace(CorrectedText) ? RawText : CorrectedText;
    public string SecondaryText => TranslatedText;
    public bool HasTranslation =>
        !string.IsNullOrWhiteSpace(TranslatedText) &&
        !string.Equals(PrimaryText.Trim(), TranslatedText.Trim(), StringComparison.OrdinalIgnoreCase);

    public static BilingualSubtitle Raw(string rawText)
    {
        return new BilingualSubtitle
        {
            RawText = rawText.Trim(),
            CorrectedText = rawText.Trim(),
            TranslatedText = string.Empty
        };
    }
}
