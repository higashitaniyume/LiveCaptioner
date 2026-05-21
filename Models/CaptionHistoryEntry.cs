namespace LiveCaptioner.Models;

public sealed class CaptionHistoryEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string RawText { get; set; } = string.Empty;
    public string CorrectedText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;

    public static CaptionHistoryEntry FromSubtitle(BilingualSubtitle subtitle)
    {
        return new CaptionHistoryEntry
        {
            Timestamp = subtitle.CreatedAt,
            RawText = subtitle.RawText,
            CorrectedText = subtitle.CorrectedText,
            TranslatedText = subtitle.TranslatedText
        };
    }
}
