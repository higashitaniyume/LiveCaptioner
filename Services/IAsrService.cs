namespace LiveCaptioner.Services;

public interface IAsrService : IAsyncDisposable
{
    event EventHandler<string>? TranscriptReady;
    event EventHandler<string>? StatusChanged;

    Task StartAsync();
    Task StopAsync();
}
