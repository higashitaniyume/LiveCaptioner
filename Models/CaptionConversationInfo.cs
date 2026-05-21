namespace LiveCaptioner.Models;

public sealed class CaptionConversationInfo
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string FilePath { get; init; } = string.Empty;

    public string DisplayName => Localization.LocalizationManager.Format("ConversationDisplayName", CreatedAt.LocalDateTime);
}
