# jellyfin-lmsubs

Jellyfin plugin for **ASR speech-to-text + LLM subtitle translation**, using
**OpenAI-compatible** APIs throughout. Provider / Base URL / API Key / Model are
configurable for both stages in the plugin settings page.

**Zero web modification**: the plugin adds two native subtitle tracks to every video,
selectable from any client's subtitle picker (Web / Android / iOS / TV / Infuse…):

| Track | Behavior |
|---|---|
| **LM Subtitles** | Selecting it starts best-effort transcription + translation from the **current playback position**: the chunk at that position first, then forward to the end, then back to cover the beginning. Already-generated ranges are served from the on-disk cache; **once the whole video is covered, no API calls are ever made again** |
| **LM Subtitles (Live)** | Every selection (or a large seek during playback) **re-calls the API** from the current position and **overwrites** the old subtitles in that time range |

The subtitle files grow chunk by chunk — players see new content whenever they
re-fetch the subtitle (seek, HLS segment loads, toggling subtitles).

Same rolling-context idea as
[potplayer-lmtranslate](https://github.com/gitsang/potplayer-lmtranslate)
(recent source→translation pairs keep names and terminology consistent), now
running entirely inside the Jellyfin server.

## How it works

```
Select the "LM Subtitles" track (any client)
        │  PlaybackStart / PlaybackProgress / UserDataSaved events
        ▼
LmPlaybackMonitor ──detects selected LM track + position──► SubtitleService
        │
        ▼  chunks of ChunkSeconds (default 600s), current position first
ffmpeg extracts 16 kHz mp3 ──► /audio/transcriptions (ASR)
        │                              │
        ▼                              ▼
   source cues.json           /chat/completions (LLM, rolling context)
                                       │
                                       ▼
                     merge into cache → render .srt (the file the track points at)
                                       │
                                       ▼
                     player re-fetches subtitle → sees the grown file
```

- The two tracks are **real external subtitle MediaStreams** persisted in Jellyfin's
  database (`IItemRepository.SaveMediaStreams`), pointing at `.srt` files in the plugin
  cache (empty at first). Jellyfin serves them through its native external-subtitle
  route — no proxy, no hooks, no web injection.
- Tracks are installed lazily on first playback of any video. Library scans rebuild
  the stream list, so the plugin listens to `RefreshCompleted` and re-installs them.
  Manual repair: `POST /LmSubs/Install/{itemId}`.
- Live-track triggering: once on selection; during playback again when the position
  jumps more than 20 s (seek) or 60 s have passed since the last trigger.

## Requirements

- Jellyfin **10.10.x** (.NET 8)
- `ffmpeg` / `ffprobe` available on the server (Jellyfin's bundled jellyfin-ffmpeg works;
  its encoder path is reused by default)
- An ASR service (`/v1/audio/transcriptions`) and a chat service (`/v1/chat/completions`),
  which may be the same vendor: OpenAI, SiliconFlow (SenseVoice), DeepSeek, Moonshot,
  Zhipu, DashScope, Groq, OpenRouter, Together, Ollama, LM Studio, local
  faster-whisper-server / speaches, or any OpenAI-compatible endpoint.

## Installation

### 1. Install the plugin (either way)

**A. Plugin repository (recommended, supports updates)**

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

Then Plugins → Catalog → **LMSubs** → Install → restart Jellyfin.

**B. Manual DLL copy**

Download the zip from [Releases](https://github.com/gitsang/jellyfin-lmsubs/releases),
extract `Jellyfin.Plugin.LmSubs.dll` into `<data dir>/plugins/LMSubs_<version>/`
(usually `/config/plugins/` in Docker), restart.

### 2. Configure

Dashboard → Plugins → **LMSubs**:

- **ASR**: pick a Provider (its default URL applies when Base URL is empty) or
  *OpenAI Compatible (custom)*; API Key; Model (e.g. `whisper-1`, `SenseVoiceSmall`);
  optional source-language hint; chunk length (default 600 s — lower to 120–300 for
  faster first subtitles)
- **Translation**: Provider / API Key / Model (e.g. `gpt-4o-mini`, `deepseek-chat`);
  target language (default Chinese (Simplified)); context pairs (default 8); batch size;
  bilingual or translation-only; extra instructions
- Click **Test Connection** to verify both endpoints, then Save.

### 3. Use

Play any video → pick **LM Subtitles** (or **LM Subtitles (Live)**) in the subtitle selector.

- First selection installs the tracks and starts generation; subtitles appear chunk by
  chunk from the current position.
- **Subtitles are not pushed**: a loaded subtitle is a snapshot. To see newly generated
  parts immediately, nudge the seek bar (or toggle the subtitle off/on). HLS transcoded
  playback loads subtitles per segment automatically.
- Once the whole video is generated, selecting "LM Subtitles" on any device is an
  instant cache load — no further cost.

## REST API (optional; scripts / debugging / external players)

All endpoints live under `/LmSubs` (auth via `X-Emby-Token`, except subtitle download):

| Method | Path | Description |
|---|---|---|
| POST | `/LmSubs/Generate` | `{itemId, position?, live?, translate?, targetLanguage?}` start/resume the pipeline |
| GET | `/LmSubs/Status/{itemId}` | status/progress (`queued/running/done/error/cancelled`) |
| POST | `/LmSubs/Cancel/{itemId}` | cancel |
| POST | `/LmSubs/Install/{itemId}` | install/repair the two subtitle tracks |
| POST | `/LmSubs/Translate` | `{itemId, targetLanguage?}` translate the cached source only |
| GET | `/LmSubs/Subtitles/{itemId}?mode=cached\|live\|source&format=vtt\|srt` | download subtitles (anonymous) |
| POST | `/LmSubs/Test` | connectivity test for both endpoints |

Cache location: `<data dir>/plugins/lmsubs/cache/<itemId>/`
(`cues.*.json` is the source of truth; `translated.<lang>.srt` / `live.<lang>.srt` are
the track files; `source.srt` is the raw transcript).

## Design notes & limitations

- **Subtitle refresh granularity**: Jellyfin clients do not poll a loaded external
  subtitle; generation is async, so the grown file is picked up on the player's next
  fetch (seek / re-select / HLS segment). This is the inherent trade-off of the pure
  native approach — in exchange for zero injection and all-client compatibility.
- Live-track triggering relies on clients reporting playback events (all official
  clients do). For third-party players that don't report progress, drive it manually
  via `POST /LmSubs/Generate {live:true, position:...}`.
- ASR services without timestamps (plain text only) still work: the text is spread
  proportionally across the chunk's time range — less precise, but usable.
- Translation uses a numbered JSON map (`{"1":"..."}`), falling back to numbered
  lines and then line-by-line requests; lines are never dropped.
- One pipeline per video at a time; a new trigger cancels the previous run. Completed
  ranges of "LM Subtitles" are never redone.
- API keys are stored in plain text in the plugin configuration XML (same as other
  Jellyfin plugins) — mind the file permissions.
- Tracks are re-installed automatically after library refreshes; if they are missing
  from the list, play the video once or call the Install endpoint.

## Development

```bash
dotnet build          # net8.0 + Jellyfin.Controller 10.10.7
dotnet test           # parsing / timing / translation-map / merge unit tests
./scripts/package.sh  # build the installable zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # entry point + config page registration
├── PluginServiceRegistrator.cs  # DI registration
├── Configuration/               # config model, provider presets, config page (html + ES-module controller)
├── Api/LmSubsController.cs      # optional REST API
├── Providers/
│   ├── LmTrackInstaller.cs      # persists the two LM tracks as media streams
│   └── LmSubtitleProvider.cs    # ISubtitleProvider (lmsubs:// id resolution)
└── Services/
    ├── LmPlaybackMonitor.cs     # playback events → track detection → pipeline trigger
    ├── SubtitleService.cs       # position-first chunk pipeline / overwrite merge / job control
    ├── SubtitleStore.cs         # cues.json + rendered .srt cache
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg chunking / ffprobe duration
    └── SubtitleText.cs          # SRT/VTT parsing & rendering, long-segment splitting
```

## License

MIT
