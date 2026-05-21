namespace LiveCaptioner.Models;

public sealed class CaptionConversationInfo
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string FilePath { get; init; } = string.Empty;

    public string DisplayName => $"对话 {CreatedAt.LocalDateTime:MM-dd HH:mm:ss}";
}
