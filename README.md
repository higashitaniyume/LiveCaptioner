# LiveCaptioner

LiveCaptioner is a Windows WPF desktop caption overlay. It captures the default system output through WASAPI loopback, resamples it to Whisper's 16 kHz mono PCM format, runs local or cloud STT, then optionally asks a configurable LLM provider to polish, punctuate, and produce a real-time bilingual subtitle before showing it in a topmost translucent window.

## Run

```powershell
dotnet restore
dotnet run --project .\LiveCaptioner.csproj
```

On first launch the app downloads `ggml-base.bin` into:

```text
%APPDATA%\LiveCaptioner\models\
```

You can also place the model there manually to keep startup fully offline.

## Notes

- LLM providers: `deepseek`, `openai`, `claude`, and `custom-openai`.
- OpenAI-compatible providers use `{baseUrl}/chat/completions`; Claude uses `{baseUrl}/v1/messages`.
- STT providers: `assemblyai`, `azure`, `google`, `local`, and a reserved `custom` WebSocket option.
- AssemblyAI streams 16 kHz `pcm_s16le` over WebSocket.
- Azure Speech uses subscription key, region, and language code.
- Google Speech uses Application Default Credentials or a service account JSON path.
- API keys and provider settings are saved in `%APPDATA%\LiveCaptioner\settings.json`.
- Whisper ASR uses `tiny` by default for lower latency. `base` and `small` are available in settings when you want higher accuracy.
- ASR backend can be set to `auto`, `cuda`, `vulkan`, or `cpu`. NVIDIA users should try `cuda`; AMD/Intel users can try `vulkan`.
- Whisper native runtime is loaded once per process, so changing the ASR backend usually needs an app restart to take full effect.
- Mouse pass-through can be toggled in the context menu. If the window becomes non-interactive, press `Ctrl+Alt+C` to restore interaction.
- Pass-through has several safeguards: it starts after a short grace period, the tray icon can restore interaction, and `Ctrl+Alt+C` always attempts to disable pass-through.
- ASR is local and free after the Whisper model has been downloaded. DeepSeek requests are debounced to reduce API calls.
- Each app launch starts a new conversation, so the main overlay begins with an empty history list.
- Conversation history is saved as JSONL files in `%APPDATA%\LiveCaptioner\history\conversations\`.
- The main overlay shows recent bilingual caption history for the active conversation, and the context menu can switch conversations or start a new one.
