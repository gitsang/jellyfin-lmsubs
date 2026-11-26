# jellyfin-lmsubs

Jellyfin 语音字幕插件：**ASR 语音转写 + LLM 字幕翻译**，全部走 **OpenAI Compatible** 接口，
Base URL / API Key / Model 均可在插件配置页自定义。

**零 Web 修改**：插件给每个视频加两条原生字幕轨，在任意客户端（Web / Android / iOS / TV / Infuse…）
的字幕选择器里直接选：

| 字幕轨 | 行为 |
|---|---|
| **LM 字幕** | 选中即从**当前播放位置**开始尽力转写＋翻译：先做当前位置那一段，然后顺序往后补，最后回头补前面。已生成的部分直接用磁盘缓存，**全片完成后不再调用任何 API** |
| **LM 字幕（实时）** | 每次选中（或播放中大幅拖动进度）都**重新调用 API** 从当前位置转写＋翻译，并**覆盖**该时间段的旧字幕 |

字幕文件随生成逐段补齐——播放器重新拉取字幕时（拖动进度条、HLS 分段加载、客户端刷新字幕）就能看到新内容。

与 [potplayer-lmtranslate](https://github.com/gitsang/potplayer-lmtranslate) 同一思路
（滚动上下文保持人名/术语一致），这次把整条链路搬进 Jellyfin 服务端。

## 工作原理

```
选择「LM 字幕」轨（任意客户端）
        │  PlaybackStart / PlaybackProgress / UserDataSaved 事件
        ▼
LmPlaybackMonitor ──识别选中的 LM 轨 + 当前播放位置──► SubtitleService
        │
        ▼  按 ChunkSeconds 分段（默认 600s），当前位置的段优先
ffmpeg 提取 16kHz mp3 ──► /audio/transcriptions (ASR)
        │                        │
        ▼                        ▼
   原文 cues.json          /chat/completions (LLM 翻译, 滚动上下文)
                                 │
                                 ▼
                    合并进缓存 → 渲染 .srt 文件（就是字幕轨指向的文件）
                                 │
                                 ▼
              播放器下次拉取字幕 → 看到 grown 的字幕
```

- 两条轨是写入 Jellyfin 数据库的**真实外部字幕流**（`IItemRepository.SaveMediaStreams`），
  指向插件缓存目录里的 `.srt` 文件（初始为空文件）。Jellyfin 用原生外部字幕通道直接读文件，
  没有代理、没有 hook、没有 web 注入。
- 首次播放任何视频时自动安装两条轨；媒体库刷新会重建流列表，插件监听 `RefreshCompleted`
  自动补装。也可手动 `POST /LmSubs/Install/{itemId}`。
- 「实时」轨在播放事件里检测：选中时触发一次；播放中位置跳变 >20s（拖动）或距上次触发 >60s
  时再次触发，从新位置覆盖式重做。

## 环境要求

- Jellyfin **10.10.x**（.NET 8）
- 服务端可用 `ffmpeg` / `ffprobe`（Jellyfin 自带 jellyfin-ffmpeg 即可，默认复用其编码器路径）
- 一个 ASR 服务（`/v1/audio/transcriptions`）和一个 Chat 服务（`/v1/chat/completions`），可以是同一家：
  OpenAI、SiliconFlow（SenseVoice）、DeepSeek、Moonshot、智谱、DashScope、Groq、OpenRouter、
  Ollama、LM Studio、本地 faster-whisper-server / speaches 等

## 安装

### 1. 安装插件（二选一）

**A. 插件存储库（推荐，可在线更新）**

控制台 → 插件 → 存储库 → 添加：

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

然后 插件 → 目录 → **LMSubs** → 安装 → 重启 Jellyfin。

**B. 手动复制 DLL**

从 [Releases](https://github.com/gitsang/jellyfin-lmsubs/releases) 下载 zip，
解压出 `Jellyfin.Plugin.LmSubs.dll` 放到 `<数据目录>/plugins/LMSubs_<版本>/`
（Docker 通常是 `/config/plugins/`），重启。

### 2. 配置

控制台 → 插件 → **LMSubs**：

- **ASR**：Base URL（填到 `/v1` 即可）、API Key、Model（如 `whisper-1`、`SenseVoiceSmall`）、
  源语言提示（可空自动检测）、分段时长（默认 600s；想更快出字幕可调小，如 120–300）
- **翻译**：Base URL、API Key、Model（如 `gpt-4o-mini`、`deepseek-chat`）、目标语言（默认简体中文）、
  上下文条数（默认 8）、每批条数、双语/仅译文、附加指令
- 点「测试连接」验证两个 endpoint，保存。

### 3. 使用

播放任意视频 → 字幕选择器里选 **「LM 字幕」**（或「LM 字幕（实时）」）。

- 首次选择会安装轨道并开始生成；字幕从当前位置逐段出现。
- **字幕不会主动推送**：播放器已加载的字幕是快照，想立即看到新生成的部分，
  拖动一下进度条（或关掉再开字幕）即可刷新。HLS 转码播放时字幕按分段自动加载，无需手动。
- 全片生成完成后，之后任何设备选「LM 字幕」都是秒载缓存，不再花钱。

## REST API（可选，供脚本/调试/外部播放器）

所有接口挂在 `/LmSubs` 下（除字幕下载外需要 `X-Emby-Token`）：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/LmSubs/Generate` | `{itemId, position?, live?, translate?, targetLanguage?}` 启动/续跑管线 |
| GET | `/LmSubs/Status/{itemId}` | 状态/进度（`queued/running/done/error/cancelled`） |
| POST | `/LmSubs/Cancel/{itemId}` | 取消 |
| POST | `/LmSubs/Install/{itemId}` | 手动安装/修复两条字幕轨 |
| POST | `/LmSubs/Translate` | `{itemId, targetLanguage?}` 只翻译已有原文缓存 |
| GET | `/LmSubs/Subtitles/{itemId}?mode=cached\|live\|source&format=vtt\|srt` | 下载字幕（匿名） |
| POST | `/LmSubs/Test` | 测试 ASR / 翻译连通性 |

缓存位置：`<数据目录>/plugins/lmsubs/cache/<itemId>/`
（`cues.*.json` 为源数据，`translated.<语言>.srt` / `live.<语言>.srt` 为字幕轨文件，`source.srt` 原文）。

## 设计说明与限制

- **字幕刷新粒度**：Jellyfin 客户端不会轮询已加载的外部字幕；生成是异步的，所以字幕
  「长出来」后需要播放器重新拉取（拖动/重选/HLS 分段）。这是纯原生方案的固有权衡——
  换来的是零注入、全客户端兼容。
- 「实时」轨的触发依赖客户端上报播放事件（官方客户端都会报）；部分第三方播放器不上报
  进度时，可用 `POST /LmSubs/Generate {live:true, position:...}` 手动驱动。
- 无时间轴的 ASR 服务（只返回纯文本）也能用：整段文本按字符比例摊到时间段上，精度下降但可用。
- 翻译走 JSON 编号映射（`{"1":"..."}`），失败自动降级为编号行、再降级逐条翻译，不会丢行。
- 同一视频同时只跑一条管线；新触发会取消旧任务。「LM 字幕」的已完成段永不重做。
- API Key 明文存在插件配置 XML 中（与 Jellyfin 其他插件一致），注意配置文件权限。
- 媒体库全量刷新后轨道自动补装；若字幕列表里暂时没看到，播放一次或调 Install 接口即可。

## 开发

```bash
dotnet build          # net8.0 + Jellyfin.Controller 10.10.7
dotnet test           # 解析/时间轴/翻译映射/合并语义单元测试
./scripts/package.sh  # 打 zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # 插件入口 + 配置页注册
├── PluginServiceRegistrator.cs  # DI 注册
├── Configuration/               # 配置模型 + configPage.html
├── Api/LmSubsController.cs      # REST API（可选）
├── Providers/
│   ├── LmTrackInstaller.cs      # 把两条 LM 轨写入媒体流数据库
│   └── LmSubtitleProvider.cs    # ISubtitleProvider（lmsubs:// id 兜底解析）
└── Services/
    ├── LmPlaybackMonitor.cs     # 播放事件 → 识别选中轨 → 触发管线
    ├── SubtitleService.cs       # 当前位置优先的分段管线 / 覆盖合并 / 任务管理
    ├── SubtitleStore.cs         # cues.json + .srt 渲染缓存
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg 切分 / ffprobe 时长
    └── SubtitleText.cs          # SRT/VTT 解析生成、长段拆分
```

## License

MIT
