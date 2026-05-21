using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using LiveCaptioner.Localization;
using LiveCaptioner.Models;

namespace LiveCaptioner.Services;

public sealed class CaptionHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CaptionHistoryService(string appDataDirectory)
    {
        HistoryDirectory = Path.Combine(appDataDirectory, "history");
        ConversationDirectory = Path.Combine(HistoryDirectory, "conversations");
    }

    public string HistoryDirectory { get; }
    public string ConversationDirectory { get; }
    public CaptionConversationInfo CurrentConversation { get; private set; } = null!;

    public string CurrentConversationPath => CurrentConversation.FilePath;

    public CaptionConversationInfo StartNewConversation()
    {
        Directory.CreateDirectory(ConversationDirectory);
        var now = DateTimeOffset.Now;
        var id = now.ToString("yyyyMMdd-HHmmss");
        CurrentConversation = new CaptionConversationInfo
        {
            Id = id,
            CreatedAt = now,
            FilePath = GetConversationPath(id)
        };

        return CurrentConversation;
    }

    public CaptionConversationInfo SwitchConversation(string conversationId)
    {
        var conversation = GetConversations()
            .FirstOrDefault(item => string.Equals(item.Id, conversationId, StringComparison.OrdinalIgnoreCase));

        if (conversation is null)
        {
            throw new InvalidOperationException(LocalizationManager.T("MissingConversationHistory"));
        }

        CurrentConversation = conversation;
        return CurrentConversation;
    }

    public IReadOnlyList<CaptionConversationInfo> GetConversations()
    {
        Directory.CreateDirectory(ConversationDirectory);

        var conversations = Directory
            .EnumerateFiles(ConversationDirectory, "conversation-*.jsonl")
            .Select(ParseConversationInfo)
            .Where(item => item is not null)
            .Cast<CaptionConversationInfo>()
            .OrderByDescending(item => item.CreatedAt)
            .ToList();

        if (CurrentConversation is not null &&
            conversations.All(item => !string.Equals(item.Id, CurrentConversation.Id, StringComparison.OrdinalIgnoreCase)))
        {
            conversations.Insert(0, CurrentConversation);
        }

        return conversations;
    }

    public async Task AppendAsync(CaptionHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.RawText) &&
            string.IsNullOrWhiteSpace(entry.CorrectedText) &&
            string.IsNullOrWhiteSpace(entry.TranslatedText))
        {
            return;
        }

        if (CurrentConversation is null)
        {
            StartNewConversation();
        }

        Directory.CreateDirectory(ConversationDirectory);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(CurrentConversationPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<CaptionHistoryEntry>> LoadCurrentRecentAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (CurrentConversation is null)
        {
            return Task.FromResult<IReadOnlyList<CaptionHistoryEntry>>([]);
        }

        return LoadRecentAsync(CurrentConversation.Id, maxCount, cancellationToken);
    }

    public async Task<IReadOnlyList<CaptionHistoryEntry>> LoadRecentAsync(
        string conversationId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetConversationPath(conversationId);
        if (!File.Exists(filePath))
        {
            return [];
        }

        var entries = new List<CaptionHistoryEntry>();
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines.Reverse())
        {
            if (entries.Count >= maxCount)
            {
                return entries;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<CaptionHistoryEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch
            {
                // Skip malformed lines so one bad write never hides the rest of the history.
            }
        }

        return entries;
    }

    private string GetConversationPath(string conversationId)
    {
        return Path.Combine(ConversationDirectory, $"conversation-{conversationId}.jsonl");
    }

    private static CaptionConversationInfo? ParseConversationInfo(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        const string prefix = "conversation-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = fileName[prefix.Length..];
        var createdAt = DateTimeOffset.Now;
        if (DateTime.TryParseExact(
                id,
                "yyyyMMdd-HHmmss",
                null,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            createdAt = new DateTimeOffset(parsed);
        }

        return new CaptionConversationInfo
        {
            Id = id,
            CreatedAt = createdAt,
            FilePath = filePath
        };
    }
}
