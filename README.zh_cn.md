<!-- MarkdownTitleNumber auto -->

# jellyfin-lmsubs

[English](README.md) | 简体中文

Jellyfin 语音字幕插件：**ASR 语音转写 + LLM 字幕翻译**（OpenAI Compatible 接口）。

工作方式与 OpenSubtitles 完全一致：**内容先就绪 → 轨道后出现 → 用户最后选**。
在媒体的「编辑字幕」对话框里搜索，会出现原文/译文/双语三个条目，按需下载；
服务端转写一次共享，只翻译你下载的那种，
完成后字幕自动落盘并出现在所有客户端的字幕列表里。已完成的结果永久缓存，重复下载不再调 API。

## 1. 工作原理

```
媒体详情页 → ⋮ 菜单 → 编辑字幕（Edit Subtitles）
        │  搜索: POST Items/{id}/RemoteSearch/Subtitles/{lang}
        ▼
LmSubtitleProvider.Search() ──返回原文/译文/双语三条候选──► 对话框列表
        │
        │  点下载: POST Items/{id}/RemoteSearch/Subtitles/{subtitleId}
        ▼
LmSubtitleProvider.GetSubtitles(id)
        │  全量顺序管线（0 → 片尾，按 ChunkSeconds 分段，默认 600s）
        ▼
ffmpeg 提取 16kHz mp3 ──► /audio/transcriptions (ASR)
        │                              │
        ▼                              ▼
   原文 cues.json             /chat/completions (LLM, 滚动上下文)
                                       │
                                       ▼
                     完成后返回完整 SRT → Jellyfin SubtitleManager
                     官方落盘（媒体目录 / metadata 目录）→ 注册外部字幕流
                                       │
                                       ▼
                     任意客户端字幕列表出现新轨 → 选中即完整显示
```

## 2. 设计说明与限制

- **全量生成，无实时**：字幕在整片处理完成后才出现（和 OpenSubtitles 下载字幕的体验一致）。
  一部电影通常需要几分钟，取决于 ASR/LLM 速度与分段设置。
- **下载请求会等待生成完成**：若反向代理超时（如 nginx 默认 60s）断开连接，
  后台任务不受影响继续跑；完成后字幕照样落盘注册，稍后刷新字幕列表即可看到。
- **重复点击会防抖**：已有一个下载请求在等待时，再点下载会立即返回提示而不是
  再挂一个保存请求，不会产生重复字幕轨。
- **日志**：全部写入 Jellyfin 服务器日志。高级设置里有插件独立的 Log level，
  设为 Debug 可打印每个 ASR 分段和每批翻译的详细过程。
- **ASR 跨目标语言共享**：每个媒体只转写一次；再请求第二种语言时复用同一份原文
  （及其进度），只花翻译的钱。不同语言的翻译并行进行。
- 已完成的结果永久缓存：再次搜索下载直接返回缓存，不再调用 API。
  想强制重做用 REST `POST /LmSubs/Generate {restart:true}`。
- 无时间轴的 ASR 服务（只返回纯文本）也能用：文本按字符比例摊到该段的时间范围上——
  精度下降，但可用。
- 翻译使用编号 JSON 映射（`{"1":"..."}`），失败依次降级为编号行、逐条请求；不会丢行。
- 同一视频同时只跑一条管线；重复触发返回进行中的任务。
- API Key 明文存在插件配置 XML 中（与其他 Jellyfin 插件一致）——注意文件权限。
- 字幕落盘位置由 Jellyfin 库设置「随媒体保存字幕」决定（媒体目录或 internal metadata 目录），
  与 OpenSubtitles 下载的字幕完全相同。

## 3. 环境要求

- Jellyfin **10.11.x**（.NET 9）
- 服务端可用 `ffmpeg` / `ffprobe`（默认复用 Jellyfin 自带 jellyfin-ffmpeg）

## 4. 安装

### 4.1 安装配置

控制台 → 插件 → 存储库 → 添加：

```
https://raw.githubusercontent.com/gitsang/jellyfin-lmsubs/main/manifest.json
```

插件 → 目录 → **LMSubs** → 安装 → 重启 Jellyfin。

控制台 → 插件 → **LMSubs**：配置 API Key，Model 等。设置页还有 **Tasks** 任务列表
（运行中 + 已完成）：可停止运行中的任务，或删除已完成任务（仅删插件缓存，
已下载到媒体目录的字幕不受影响）。

### 4.2 使用

媒体详情页 → ⋮ 菜单 → **编辑字幕（Edit Subtitles）** → 选语言 → 搜索 →
出现三个条目，按需下载：

- **LM Subtitles - Source**：仅转写原文，不花翻译的钱
- **LM Subtitles - Translate**：仅译文
- **LM Subtitles - Bilingual**：原文 + 译文双语两行

三者共用同一次 ASR；译文和双语还共用**同一个翻译任务**——每种语言只翻译一次，
两个条目只是同一份数据的两种渲染。搜索时选的语言
**就是翻译目标语言**。条目描述显示模型/目标语言；**再次搜索可看进度**——运行中 `(40%)`，完成后 `(Completed)`。

生成完成后（几分钟），字幕出现在该媒体的字幕列表里，任意客户端选中即可。

## 5. REST API（可选；供脚本 / 调试 / 外部播放器）

所有接口挂在 `/LmSubs` 下（除字幕下载外需 `X-Emby-Token` 认证）：

| 方法 | 路径                                                          | 说明                                                          |
| ---- | ------------------------------------------------------------- | ------------------------------------------------------------- |
| POST | `/LmSubs/Generate`                                            | `{itemId, mode? (source\|translated\|bilingual), targetLanguage?, restart?}` |
| GET  | `/LmSubs/Status/{itemId}`                                     | 状态/进度（`queued/running/done/error/cancelled`）            |
| POST | `/LmSubs/Cancel/{itemId}`                                     | 取消                                                          |
| GET | `/LmSubs/Tasks`                                               | 全部任务：运行中 + 已完成（含重启后仅剩缓存的条目）        |
| DELETE | `/LmSubs/Tasks/{itemId}?lang=`                                | 停止任务并删缓存（省略 lang 删全部语言）；已下载字幕保留 |
| POST | `/LmSubs/Translate`                                           | `{itemId, targetLanguage?}` 仅翻译已缓存的原文                |
| GET  | `/LmSubs/Subtitles/{itemId}?mode=translated\|source&format=vtt\|srt` | 下载字幕（匿名）                                       |
| POST | `/LmSubs/Test`                                                | 两个端点的连通性测试                                          |

缓存位置：`<数据目录>/plugins/lmsubs/cache/<itemId>/`
（`cues.*.json` 为源数据；`translated.<语言>.srt` 为成品字幕；`source.srt` 为原始转写文本）。

## 6. 开发

```bash
dotnet build          # net9.0 + Jellyfin.Controller 10.11.5
dotnet test           # 解析 / 时间轴 / 翻译映射单元测试
./scripts/package.sh  # 打可安装的 zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # 插件入口 + 配置页注册
├── PluginServiceRegistrator.cs  # DI 注册
├── Configuration/               # 配置模型、Provider 预设、配置页（html + ES module 控制器）
├── Api/LmSubsController.cs      # 可选 REST API
├── Providers/
│   └── LmSubtitleProvider.cs    # ISubtitleProvider：Search 候选 + GetSubtitles 全量生成
└── Services/
    ├── SubtitleService.cs       # 全量顺序管线 / 任务管理 / 完成信号
    ├── SubtitleStore.cs         # cues.json + 渲染 .srt 缓存
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg 切分 / ffprobe 时长
    └── SubtitleText.cs          # SRT/VTT 解析生成、长段拆分
```

## 7. License

MIT
