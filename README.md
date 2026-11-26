<!-- MarkdownTitleNumber auto -->

# jellyfin-lmsubs

English | [简体中文](README.zh_cn.md)

Jellyfin plugin for **ASR speech-to-text + LLM subtitle translation**.

Subtitle tracks:

- **LM Subtitles**: best-effort transcription + translation from the **current playback position**: current position first, then forward to the end, then back to cover the beginning; already-generated ranges are served straight from the on-disk cache.
- **LM Subtitles (Live)**: **re-calls the API** from the current position and **overwrites** the old subtitles in that time range.

## 1. How it works

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

## 2. Design notes & limitations

- **Subtitle refresh granularity**: Jellyfin clients do not poll a loaded external
  subtitle; generation is async, so the grown file is only picked up on the player's
  next fetch (seek / re-select / HLS segment). This is the inherent trade-off of the
  pure native approach — in exchange for zero injection and all-client compatibility.
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
- The two tracks are **real external subtitle MediaStreams** persisted in Jellyfin's
  database (`IItemRepository.SaveMediaStreams`), pointing at `.srt` files in the plugin
  cache (empty at first). Jellyfin serves them through its native external-subtitle
  route — no proxy, no hooks, no web injection.
- Tracks are installed lazily on first playback of any video. Library scans rebuild
  the stream list, so the plugin listens to `RefreshCompleted` and re-installs them.
  Manual repair: `POST /LmSubs/Install/{itemId}`.
- Live-track triggering: once on selection; during playback again when the position
  jumps more than 20 s (seek) or 60 s have passed since the last trigger.
- **Subtitles are not pushed**: a loaded subtitle is a snapshot. To see newly generated
  parts immediately, nudge the seek bar (or toggle the subtitle off/on). HLS transcoded
  playback loads subtitles per segment automatically.

## 3. Requirements

- Jellyfin **10.10.x** (.NET 8)
- `ffmpeg` / `ffprobe` available on the server (Jellyfin's bundled jellyfin-ffmpeg is reused by default)

## 4. Installation

### 4.1 Install & configure

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

Plugins → Catalog → **LMSubs** → Install → restart Jellyfin.

Dashboard → Plugins → **LMSubs**: configure API Key, Model, etc.

### 4.2 Use

Play any video → pick **LM Subtitles** (or **LM Subtitles (Live)**) in the subtitle selector

First selection installs the tracks and starts generation

## 5. REST API (optional; scripts / debugging / external players)

All endpoints live under `/LmSubs` (auth via `X-Emby-Token`, except subtitle download):

| Method | Path                                                                   | Description                                                             |
| ------ | ---------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| POST   | `/LmSubs/Generate`                                                     | `{itemId, position?, live?, translate?, targetLanguage?}` start/resume the pipeline |
| GET    | `/LmSubs/Status/{itemId}`                                              | status/progress (`queued/running/done/error/cancelled`)                 |
| POST   | `/LmSubs/Cancel/{itemId}`                                              | cancel                                                                  |
| POST   | `/LmSubs/Install/{itemId}`                                             | install/repair the two subtitle tracks                                  |
| POST   | `/LmSubs/Translate`                                                    | `{itemId, targetLanguage?}` translate the cached source only            |
| GET    | `/LmSubs/Subtitles/{itemId}?mode=cached\|live\|source&format=vtt\|srt` | download subtitles (anonymous)                                          |
| POST   | `/LmSubs/Test`                                                         | connectivity test for both endpoints                                    |

Cache location: `<data dir>/plugins/lmsubs/cache/<itemId>/`
(`cues.*.json` is the source of truth; `translated.<lang>.srt` / `live.<lang>.srt` are
the track files; `source.srt` is the raw transcript).

## 6. Development

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

## 7. License

MIT
