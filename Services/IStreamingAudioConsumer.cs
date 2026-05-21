namespace LiveCaptioner.Services;

public interface IStreamingAudioConsumer
{
    void AddAudio(ReadOnlySpan<byte> pcm16Mono16k);
}
