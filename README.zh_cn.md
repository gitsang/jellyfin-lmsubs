<!-- MarkdownTitleNumber auto -->

# jellyfin-lmsubs

[English](README.md) | 简体中文

Jellyfin 语音字幕插件：**ASR 语音转写 + LLM 字幕翻译**。

字幕轨：

- **LM Subtitles**：从**当前播放位置**开始尽力转写＋翻译：优先当前位置，顺序补到片尾，再回头补前面；已生成的时间段直接用磁盘缓存。
- **LM Subtitles (Live)**：**重新调用 API** 从当前位置转写，并**覆盖**该时间段的旧字幕。

## 1. 工作原理

```
选择「LM Subtitles」轨（任意客户端）
        │  PlaybackStart / PlaybackProgress / UserDataSaved 事件
        ▼
LmPlaybackMonitor ──识别选中的 LM 轨 + 播放位置──► SubtitleService
        │
        ▼  按 ChunkSeconds 分段（默认 600s），当前位置的段优先
ffmpeg 提取 16kHz mp3 ──► /audio/transcriptions (ASR)
        │                              │
        ▼                              ▼
   原文 cues.json             /chat/completions (LLM, 滚动上下文)
                                       │
                                       ▼
                     合并进缓存 → 渲染 .srt（字幕轨指向的文件）
                                       │
                                       ▼
                     播放器重新拉取字幕 → 看到增长后的文件
```

## 2. 设计说明与限制

- **字幕刷新粒度**：Jellyfin 客户端不会轮询已加载的外部字幕；生成是异步的，
  增长后的文件要等播放器下次拉取（拖动 / 重选 / HLS 分段）才可见。这是纯原生方案的
  固有权衡——换来零注入与全客户端兼容。
- Live 轨触发依赖客户端上报播放事件（官方客户端都会上报）。不上报进度的第三方播放器
  可用 `POST /LmSubs/Generate {live:true, position:...}` 手动驱动。
- 无时间轴的 ASR 服务（只返回纯文本）也能用：文本按字符比例摊到该段的时间范围上——
  精度下降，但可用。
- 翻译使用编号 JSON 映射（`{"1":"..."}`），失败依次降级为编号行、逐条请求；不会丢行。
- 同一视频同时只跑一条管线；新触发会取消旧任务。「LM Subtitles」已完成的时间段永不重做。
- API Key 明文存在插件配置 XML 中（与其他 Jellyfin 插件一致）——注意文件权限。
- 媒体库刷新后轨道自动补装；若列表里暂时没有，播放一次该视频或调用 Install 接口即可。
- 两条轨是持久化在 Jellyfin 数据库里的**真实外部字幕 MediaStream**
  （`IItemRepository.SaveMediaStreams`），指向插件缓存目录中的 `.srt` 文件（初始为空）。
  Jellyfin 通过原生外部字幕通道直接提供这些文件——无代理、无 hook、无 web 注入。
- 首次播放任何视频时惰性安装轨道。媒体库扫描会重建流列表，因此插件监听
  `RefreshCompleted` 事件自动补装。手动修复：`POST /LmSubs/Install/{itemId}`。
- Live 轨触发时机：选中时触发一次；播放中位置跳变超过 20 秒（拖动）、或距上次触发
  超过 60 秒时再次触发。
- **字幕不会主动推送**：已加载的字幕是快照。想立即看到新生成的部分，
  拖一下进度条（或关掉再开字幕）即可。HLS 转码播放时字幕按分段自动加载。

## 3. 环境要求

- Jellyfin **10.10.x**（.NET 8）
- 服务端可用 `ffmpeg` / `ffprobe`（默认复用 Jellyfin 自带 jellyfin-ffmpeg）

## 4. 安装

### 4.1 安装配置

控制台 → 插件 → 存储库 → 添加：

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

插件 → 目录 → **LMSubs** → 安装 → 重启 Jellyfin。

控制台 → 插件 → **LMSubs**：配置 API Key，Model 等。

### 4.2 使用

播放任意视频 → 字幕选择器里选 **LM Subtitles**（或 **LM Subtitles (Live)**）

首次选择会安装轨道并开始生成

## 5. REST API（可选；供脚本 / 调试 / 外部播放器）

所有接口挂在 `/LmSubs` 下（除字幕下载外需 `X-Emby-Token` 认证）：

| 方法 | 路径                                                                   | 说明                                                                    |
| ---- | ---------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| POST | `/LmSubs/Generate`                                                     | `{itemId, position?, live?, translate?, targetLanguage?}` 启动/续跑管线 |
| GET  | `/LmSubs/Status/{itemId}`                                              | 状态/进度（`queued/running/done/error/cancelled`）                      |
| POST | `/LmSubs/Cancel/{itemId}`                                              | 取消                                                                    |
| POST | `/LmSubs/Install/{itemId}`                                             | 安装/修复两条字幕轨                                                     |
| POST | `/LmSubs/Translate`                                                    | `{itemId, targetLanguage?}` 仅翻译已缓存的原文                          |
| GET  | `/LmSubs/Subtitles/{itemId}?mode=cached\|live\|source&format=vtt\|srt` | 下载字幕（匿名）                                                        |
| POST | `/LmSubs/Test`                                                         | 两个端点的连通性测试                                                    |

缓存位置：`<数据目录>/plugins/lmsubs/cache/<itemId>/`
（`cues.*.json` 为源数据；`translated.<语言>.srt` / `live.<语言>.srt` 为字幕轨文件；
`source.srt` 为原始转写文本）。

## 6. 开发

```bash
dotnet build          # net8.0 + Jellyfin.Controller 10.10.7
dotnet test           # 解析 / 时间轴 / 翻译映射 / 合并语义单元测试
./scripts/package.sh  # 打可安装的 zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # 插件入口 + 配置页注册
├── PluginServiceRegistrator.cs  # DI 注册
├── Configuration/               # 配置模型、Provider 预设、配置页（html + ES module 控制器）
├── Api/LmSubsController.cs      # 可选 REST API
├── Providers/
│   ├── LmTrackInstaller.cs      # 把两条 LM 轨持久化为媒体流
│   └── LmSubtitleProvider.cs    # ISubtitleProvider（lmsubs:// id 解析）
└── Services/
    ├── LmPlaybackMonitor.cs     # 播放事件 → 轨道识别 → 管线触发
    ├── SubtitleService.cs       # 当前位置优先的分段管线 / 覆盖合并 / 任务管理
    ├── SubtitleStore.cs         # cues.json + 渲染 .srt 缓存
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg 切分 / ffprobe 时长
    └── SubtitleText.cs          # SRT/VTT 解析生成、长段拆分
```

## 7. License

MIT
