# jellyfin-lmsubs

Jellyfin 语音字幕插件：**ASR 语音转写 + LLM 字幕翻译**，全部走 **OpenAI Compatible** 接口，
Base URL / API Key / Model 均可在插件配置页自定义。

与 [potplayer-lmtranslate](../potplayer-lmtranslate) 同一思路（滚动上下文保持人名/术语一致），
这次把整条链路搬进 Jellyfin 服务端。

## 功能

| 模式 | 说明 |
|---|---|
| **全量生成** | ffmpeg 按段切分音频 → 并发请求 `/audio/transcriptions`（verbose_json 带时间轴）→ 生成 SRT → 可选自动 LLM 翻译。后台任务 + 进度轮询，结果缓存到服务端，所有客户端可用 |
| **实时模式** | 播放时按需转写当前位置往前 30s 的窗口（ASR + 翻译），随播随取、窗口级缓存，拖动进度条自动跟随。不需要先跑全量 |
| **双语字幕** | 默认「原文 + 译文」两行，可切换仅译文 |
| **滚动上下文** | 翻译时携带最近 N 条「原文 → 译文」作为对话历史（默认 8，与 LMTranslate 一致） |
| **连通性测试** | 配置页一键测试 ASR / 翻译两个 endpoint |

兼容任何 OpenAI Compatible 服务：OpenAI、SiliconFlow（SenseVoice）、DeepSeek、Moonshot、
智谱、DashScope、Groq、OpenRouter、Ollama、LM Studio、本地 faster-whisper-server / speaches 等。

## 环境要求

- Jellyfin **10.10.x**（.NET 8）
- 服务端可用 `ffmpeg` / `ffprobe`（Jellyfin 自带 jellyfin-ffmpeg 即可，插件默认复用其编码器路径）
- 一个 ASR 服务（`/v1/audio/transcriptions`）和一个 Chat 服务（`/v1/chat/completions`），可以是同一家

## 安装

### 1. 安装插件（二选一）

**A. 手动复制 DLL**

```bash
./scripts/package.sh            # 需要 .NET 8 SDK；产物 dist/lmsubs_1.0.0.0.zip
# 解压后把 Jellyfin.Plugin.LmSubs.dll 放到:
#   <数据目录>/plugins/LMSubs_1.0.0.0/Jellyfin.Plugin.LmSubs.dll
# Docker 通常是 /config/plugins/LMSubs_1.0.0.0/
# 重启 Jellyfin
```

**B. 插件存储库**

把 zip 发布到任意可下载的 URL（如 GitHub Release），修改 `manifest.json` 的
`sourceUrl` 与 `checksum`（MD5），在 控制台 → 插件 → 存储库 添加 manifest 的 URL 后在目录中安装。

### 2. 注入 Web 播放页脚本（实时模式 / 播放页按钮需要）

Jellyfin 插件机制不允许服务端直接改 Web UI，播放页的「LM」按钮由 `web/lmsubs.js` 提供：

```bash
sudo ./scripts/install-web.sh                 # 自动找 web 目录并注入 index.html
# 或显式指定: sudo ./scripts/install-web.sh /usr/share/jellyfin/web
```

Docker / 不想改 web 文件：用 nginx `sub_filter` 注入，见
[`scripts/nginx-inject.conf.example`](scripts/nginx-inject.conf.example)（Jellyfin 升级后不失效）。

> 不注入脚本也能用：插件 REST API 完整可用（见下），任何客户端都可以自己调。

### 3. 配置

控制台 → 插件 → **LMSubs**：

- **ASR**：Base URL（填到 `/v1` 即可）、API Key、Model（如 `whisper-1`、`SenseVoiceSmall`）、
  源语言提示（可空自动检测）、分段时长（默认 600s）、并发数
- **翻译**：Base URL、API Key、Model（如 `gpt-4o-mini`、`deepseek-chat`）、目标语言（默认简体中文）、
  上下文条数、每批条数、双语/仅译文、附加指令
- 点「测试连接」验证两个 endpoint，保存。

### 4. 使用

浏览器播放视频 → 右下角 OSD 出现 **LM** 按钮：

- `全量生成（转写＋翻译）`：后台跑全片，面板显示进度，完成后自动加载双语字幕
- `实时模式（转写＋翻译）`：立即开始，按需转写当前位置附近音频，边播边出字幕
- `加载已生成译文/原文`：秒载缓存结果
- `停止 / 关闭字幕`

## REST API

所有接口挂在 `/LmSubs` 下（除字幕下载外需要 `X-Emby-Token`）：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/LmSubs/Generate` | `{itemId, audioStreamIndex?, translate?, targetLanguage?}` 提交全量任务 |
| GET | `/LmSubs/Status/{itemId}` | 任务状态/进度（`queued/extracting/transcribing/translating/done/error`） |
| POST | `/LmSubs/Cancel/{itemId}` | 取消任务 |
| POST | `/LmSubs/Translate` | `{itemId, targetLanguage?}` 只翻译已有原文缓存 |
| GET | `/LmSubs/Subtitles/{itemId}?translated=true&lang=...&format=vtt\|srt` | 下载字幕（匿名，供 `<track>` 使用） |
| POST | `/LmSubs/Realtime` | `{itemId, start, window?, translate?, targetLanguage?}` 窗口转写（实时模式） |
| POST | `/LmSubs/Test` | 测试 ASR / 翻译连通性 |

字幕缓存位置：`<数据目录>/plugins/lmsubs/cache/<itemId>/`
（`source.srt`、`translated.<语言>.srt`、`windows/*.json`）。

## 设计说明与限制

- **实时模式的本质**是「按需窗口转写」：客户端报告播放位置，服务端切 30s 音频 → ASR → 翻译 →
  返回带时间轴的 cues。字幕会比画面晚几秒出现（取决于 ASR/LLM 延迟），窗口结果有缓存，
  回看不再花钱。真正的逐秒流式 ASR 需要 WebSocket 流式接口，OpenAI HTTP 转写协议做不到，
  这是接口形态决定的取舍。
- 无时间轴的 ASR 服务（只返回纯文本）也能用：整段文本按字符比例摊到时间段上，精度下降但可用。
- 翻译走 JSON 编号映射（`{"1":"..."}`），失败自动降级为编号行、再降级逐条翻译，不会丢行。
- 全量任务串行执行（一次一部片），段内并发可配；实时窗口请求不受全量任务阻塞。
- API Key 明文存在插件配置 XML 中（与 Jellyfin 其他插件一致），注意配置文件权限。
- 匿名字幕下载端点仅暴露不可猜测的 GUID 对应的已生成字幕，不含媒体本身。

## 开发

```bash
dotnet build          # net8.0 + Jellyfin.Controller 10.10.7
dotnet test           # 解析/时间轴/翻译映射单元测试
./scripts/package.sh  # 打 zip
```

```
Jellyfin.Plugin.LmSubs/
├── Plugin.cs                    # 插件入口 + 配置页注册
├── PluginServiceRegistrator.cs  # DI 注册
├── Configuration/               # 配置模型 + configPage.html
├── Api/LmSubsController.cs      # REST API
└── Services/
    ├── OpenAiClient.cs          # /audio/transcriptions + /chat/completions
    ├── AudioExtractor.cs        # ffmpeg 切分 / ffprobe 时长
    ├── SubtitleText.cs          # SRT/VTT 解析生成、长段拆分
    └── SubtitleService.cs       # 全量管线 / 翻译上下文 / 窗口缓存 / 任务管理
web/lmsubs.js                    # 播放页注入脚本（无需构建）
scripts/                         # 安装 / 打包 / nginx 注入示例
```

## License

MIT
