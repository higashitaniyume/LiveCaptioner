namespace LiveCaptioner.Services;

#pragma warning disable CS0067
public sealed class UnsupportedAsrService : IAsrService
{
    private readonly string _message;

    public UnsupportedAsrService(string message)
    {
        _message = message;
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public Task StartAsync()
    {
        StatusChanged?.Invoke(this, _message);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
#pragma warning restore CS0067
