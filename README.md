<!-- MarkdownTitleNumber auto -->

# jellyfin-lmsubs

English | [简体中文](README.zh_cn.md)

Jellyfin plugin for **ASR speech-to-text + LLM subtitle translation** (OpenAI-compatible APIs).

Works exactly like OpenSubtitles: **content first → track appears → user selects last**.
Search in the item's **Edit Subtitles** dialog and download one of three entries —
Source / Translated / Bilingual; the server transcribes (once, shared) and translates
only what you asked for, then the finished subtitle is saved and
shows up in every client's subtitle list. Completed results are cached forever — downloading
again never re-calls the API.

## 1. How it works

```
Item details → ⋮ menu → Edit Subtitles
        │  search: POST Items/{id}/RemoteSearch/Subtitles/{lang}
        ▼
LmSubtitleProvider.Search() ──returns source/translated/bilingual candidates──► dialog list
        │
        │  download: POST Items/{id}/RemoteSearch/Subtitles/{subtitleId}
        ▼
LmSubtitleProvider.GetSubtitles(id)
        │  full sequential pipeline (0 → end, chunks of ChunkSeconds, default 600s)
        ▼
ffmpeg extracts 16 kHz mp3 ──► /audio/transcriptions (ASR)
        │                              │
        ▼                              ▼
   source cues.json           /chat/completions (LLM, rolling context)
                                       │
                                       ▼
                     complete SRT returned → Jellyfin SubtitleManager
                     saves it (media dir / metadata dir) → registers external stream
                                       │
                                       ▼
                     new track in any client's subtitle list → select → full display
```

## 2. Design notes & limitations

- **Full generation, no realtime**: the subtitle only appears after the whole video is
  processed (same experience as downloading an OpenSubtitles entry). A movie typically
  takes a few minutes, depending on ASR/LLM speed and chunk settings.
- **The download request waits for generation**: if a reverse proxy times out (e.g. nginx's
  default 60 s) and drops the connection, the background job is unaffected and keeps running;
  the subtitle is still saved and registered when done — refresh the subtitle list later.
- **Duplicate clicks are debounced**: while a download is already waiting on a running job,
  clicking download again fails fast instead of queueing a second save (no duplicate tracks).
- **Logging**: everything goes to the Jellyfin server log. The Advanced settings have a
  plugin-only Log level; set it to Debug to log every ASR chunk and translation batch.
- **ASR is shared across target languages**: transcription runs once per item; requesting a
  second language reuses the same transcript (and its live progress) and only pays for
  translation. Translations for different languages run in parallel.
- Completed results are cached forever: searching/downloading again returns the cache without
  any API calls. To force a redo use REST `POST /LmSubs/Generate {restart:true}`.
- ASR services without timestamps (plain text only) still work: the text is spread
  proportionally across the chunk's time range — less precise, but usable.
- Translation uses a numbered JSON map (`{"1":"..."}`), falling back to numbered lines and
  then line-by-line requests; lines are never dropped.
- One pipeline per video at a time; repeated triggers return the running job.
- API keys are stored in plain text in the plugin configuration XML (same as other Jellyfin
  plugins) — mind the file permissions.
- Where the subtitle file lands is decided by Jellyfin's library option "Save subtitles with
  media" (media dir or internal metadata dir) — identical to OpenSubtitles downloads.

## 3. Requirements

- Jellyfin **10.11.x** (.NET 9)
- `ffmpeg` / `ffprobe` available on the server (Jellyfin's bundled jellyfin-ffmpeg is reused by default)

## 4. Installation

### 4.1 Install & configure

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

Plugins → Catalog → **LMSubs** → Install → restart Jellyfin.

Dashboard → Plugins → **LMSubs**: configure API Key, Model, etc. The settings page also
has a **Tasks** list (running + completed): stop a running task, or delete a completed one
(removes the plugin cache only; subtitles already downloaded to your media stay).

### 4.2 Use

Item details → ⋮ menu → **Edit Subtitles** → pick a language → Search →
three entries appear; download the one you want:

- **LM Subtitles — Source (…)**: transcription only, no translation cost
- **LM Subtitles — <language>**: translation only
- **LM Subtitles — Bilingual (… + …)**: source + translation, two lines

All three share one ASR pass, and translated/bilingual share **one translation job** per
language — the translation runs once and the two entries are just different renders of it.
The language you pick in the search dialog **is the translation target**.
Descriptions show models/target and **live progress percentages on re-search**.

When generation finishes (a few minutes), the subtitle appears in the item's subtitle list,
selectable from any client.

## 5. REST API (optional; scripts / debugging / external players)

All endpoints live under `/LmSubs` (auth via `X-Emby-Token`, except subtitle download):

| Method | Path                                                                 | Description                                                        |
| ------ | -------------------------------------------------------------------- | ------------------------------------------------------------------ |
| POST   | `/LmSubs/Generate`                                                   | `{itemId, mode? (source\|translated\|bilingual), targetLanguage?, restart?}` |
| GET    | `/LmSubs/Status/{itemId}`                                            | status/progress (`queued/running/done/error/cancelled`)            |
| POST   | `/LmSubs/Cancel/{itemId}`                                            | cancel                                                             |
| GET    | `/LmSubs/Tasks`                                                      | all tasks: running + completed (incl. cache-only after restart)    |
| DELETE | `/LmSubs/Tasks/{itemId}?lang=`                                       | stop task + delete cache (all langs if omitted); downloaded files kept |
| POST   | `/LmSubs/Translate`                                                  | `{itemId, targetLanguage?}` translate the cached source only       |
| GET    | `/LmSubs/Subtitles/{itemId}?mode=translated\|source&format=vtt\|srt` | download subtitles (anonymous)                                     |
| POST   | `/LmSubs/Test`                                                       | connectivity test for both endpoints                               |

Cache location: `<data dir>/plugins/lmsubs/cache/<itemId>/`
(`cues.*.json` is the source of truth; `translated.<lang>.srt` is the finished subtitle;
`source.srt` is the raw transcript).

## 6. Development

```bash
dotnet build          # net9.0 + Jellyfin.Controller 10.11.5
dotnet test           # parsing / timing / translation-map unit tests
./scripts/package.sh  # build the installable zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # entry point + config page registration
├── PluginServiceRegistrator.cs  # DI registration
├── Configuration/               # config model, provider presets, config page (html + ES-module controller)
├── Api/LmSubsController.cs      # optional REST API
├── Providers/
│   └── LmSubtitleProvider.cs    # ISubtitleProvider: Search candidate + GetSubtitles full generation
└── Services/
    ├── SubtitleService.cs       # full sequential pipeline / job control / completion signal
    ├── SubtitleStore.cs         # cues.json + rendered .srt cache
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg chunking / ffprobe duration
    └── SubtitleText.cs          # SRT/VTT parsing & rendering, long-segment splitting
```

## 7. License

MIT
