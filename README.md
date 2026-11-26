<div align="center">

<img src="src/MangaGui/Assets/komaflow.svg" width="84" height="84" alt="KomaFlow" />

# KomaFlow

**支持多账号智能调度与多档 GPU 自适应的 Windows 原生漫画翻译工作台**

把 Gemini 赠送 / 闲置的 Google Colab 算力额度花在刀刃上，拖入 `.zip / .cbz` 生肉漫画，全自动部署、看图翻译、校验下载并自动停机止损。

[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4)](#下载与安装)
[![Release](https://img.shields.io/badge/Version-v1.7.0-F9AB00)](https://github.com/rinmashiro0529/KomaFlow/releases)
[![Runtime](https://img.shields.io/badge/.NET%208-Self--Contained-512BD4)](#下载与安装)

</div>

---

## 核心特点

- **多账号智能调度，余额优先**
  每个 Google 账号使用独立的 ADC 凭据与会话配置，互不污染系统默认 `gcloud`。支持自动并发刷新余额、优先选用空闲且余额最高的账号、安全 Failover 顺延，以及跨账号残留会话告警与一键停机。
- **开机自测显存，按卡自动匹配最佳方案**
  无需手写 Colab 脚本，开机实测真实显存后自动拉起对应档位的 OCR、大模型翻译与重绘流水线：

  | GPU 档位 | 参考 CU/h | 自动分配方案 | 适用场景 |
  |---|---:|---|---|
  | **L4**（24G） | `2.33` | **Sakura 14B** + `manga-ocr` + `LaMa Large` | 经济走量、省额度、速度快 |
  | **A100 40G** | `8.16` | **Qwen3.5-27B (Q6_K)** 看图多模态 + `hayai_ocr_v2` | 结合分镜画面/表情翻，告别盲翻惨案 |
  | **A100 80G** | `10.99` | **Qwen3-VL-32B (Q8)** 主译 + 本地 **Sakura 14B** 热备 | 旗舰画质，单页翻车自动零拒答热切兜底 |
  | **G4 / Pro 6000**（96G） | `10.00` | **Qwen3.5-122B-A10B MoE** 顶配（或切 `32B + 14B` 均衡档） | 最高理解力，适合复杂剧情与长台词 |

  > **高阶画质增强**：40G 及以上档位支持勾选 **Flux2-Klein 4B** 高精重绘（彩漫 / 复杂网点背景更干净）；采样短边中位数 `< 900px` 的低清图源自动开启 `mangajanai x2` 超分。
  > 选「A100 标准」或「A100 High-RAM」时，均以开机实测显存在 40G / 80G 方案间自动适配。

- **多重防忘关扣费保护**
  - `colab sessions` 三态解析，绝不把空输出或网络报错误判为「已停机」。
  - 定时自动停机 + 停机后二次查云端核实；关闭窗口防呆保护与系统托盘常驻提醒。
  - 断线 / 崩溃重开后自动接管原云端会话并沿用原计时；无人值守到期按安全策略自动收尾停机。
- **严格成品校验与原生 Fluent 体验**
  - 支持 `.zip` / `.cbz` 拖入，入队即离线预检损坏包、页数与自然页序（`1, 2, 10` 不错乱）。
  - 超过 48 MiB 自动分片上传/下载；成品强制校验字节数、SHA256、ZIP 结构、页数与图片魔数，未通过绝不落盘。
  - Windows 11 原生 Mica 界面，支持浅色 / 深色 / 跟随系统主题、任务栏进度条与同内核无窗口命令行 [`MangaCli`](docs/CLI.md)。

---

## 下载与安装

前往 [**Releases**](https://github.com/rinmashiro0529/KomaFlow/releases) 下载（均内置 `.NET 8 Desktop Runtime`，**Win10 / Win11 64 位零依赖开箱即用**）：

| 发行版 | 文件名 | 说明 |
|---|---|---|
| **安装程序（推荐）** | `KomaFlow-v1.7.0-win-x64-setup.exe` | 免管理员权限，支持自定义目录、创建桌面快捷方式，并自动将 `MangaCli` 加入用户 `PATH` |
| **便携包** | `KomaFlow-v1.7.0-win-x64-portable.zip` | 解压后直接运行 `KomaFlow\MangaGui.exe`（保持与 `assets\` 同级目录） |

> 安装版与便携包共用 `%APPDATA%\manga_gui\` 配置目录，切换安装方式时账号与设置无缝保留；卸载程序默认不删除你的账号和配置。

---

## 快速上手

1. **一次性环境检查**（不申请 GPU、不计费）
   打开 KomaFlow → 右上角「**… → 环境检查与修复**」，按引导确认本机具备：
   - [`google-colab-cli`](https://github.com/googlecolab/colab-cli)（软件自动定位 `colab.exe`）
   - Windows OpenSSH（`ssh.exe`）及 `~/.ssh/id_ed25519.pub` 或 `id_ecdsa.pub`（Colab 不接受 RSA 密钥）
2. **添加 Colab 账号**
   打开「**Colab 账号**」→ 浏览器一键授权登录（或导入已有 ADC JSON），核对剩余 CU 额度并勾选启用需要参与调度的账号。
3. **开始翻译**
   将漫画 `.zip / .cbz` 拖入窗口 → 点击「**启动服务**」选择 GPU 档位与定时时长 → 自动部署、逐本翻译下载，跑完或到时自动停机核实。

---

## 引擎、术语表与快捷键

- **翻译引擎**：默认使用 `mtui`（`manga-translator-ui`，针对日文漫画优化，气泡嵌字质量更好）；如需翻译英文/泰文等非日文漫画，可在「**… → 翻译引擎**」切换为 `mit` 回退引擎（需将 `assets/manga.example.json` 复制为 `manga.json`）。
- **自定义术语表**：可在设置中直接指定自定义术语表文件，格式为每行 `原文->译文 #备注`（留空则不启用术语表）。
- **快捷键**：`Ctrl+O` 添加压缩包 · `F5` 复查云端会话 · `Delete` 移除待处理任务 · `Enter / 双击` 打开成品或失败日志 · `Ctrl+C` 复制日志 · `Esc` 关闭弹窗。

---

## 命令行（MangaCli）

`MangaCli.exe` 与 GUI 共享同一套会话、多账号调度、计费保护与校验内核，面向批处理脚本与 Agent 自动化，stdout 严格输出 NDJSON 事件流：

```powershell
# 环境自检与离线预检（不触网、不计费）
MangaCli doctor
MangaCli precheck .\book.cbz

# 查看 / 刷新多账号额度
MangaCli accounts list --refresh

# 自动选号 + A100 多模态 + Flux2-Klein 重绘，定时 90 分钟跑完自动停机
MangaCli run .\vol01.cbz .\vol02.cbz --minutes 90 --gpu a100 --inpaint flux2-klein --account auto

# 兜底停机核实
MangaCli stop
```

完整命令表、参数、NDJSON 事件契约、退出码及 Agent 后台调用规范见 [**`docs/CLI.md`**](docs/CLI.md)。

---

## 常见排查

| 现象 | 排查方式 |
|---|---|
| 提示缺少 `assets` | 重新解压完整便携包或重装，不要只单独复制 `.exe` |
| 授权失效 / 网络异常 | 授权失效在「Colab 账号」重新登录；网络异常先检查代理后点刷新 |
| 申请 GPU 报 `503`（且已核实未分配） | 当前档位临时缺货，换选 `A100 标准`、`L4` 或切换其他账号 |
| `STAGE2 / STAGE5` 耗时较长 | 首次冷启动正在云端拉取环境与模型（约 4–8 分钟，下载期间正常计费），可在日志栏查看进度 |
| 停机后仍提示存在 `[?]` 未命名实例 | 按提示中标注的账号名打开 Colab 网页 →「管理会话」核对并手动终止 |

---

## 从源码构建与打包

需要 `.NET 8 SDK`（Windows x64）；打包安装程序需本机安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)。

```powershell
# 1. 编译与回归测试（Mock IColabCli，零 Colab 计费）
dotnet build MangaGui.sln -c Release
dotnet test MangaGui.sln -c Release

# 2. 一键打包（门禁测试 + 便携 ZIP + Inno Setup 安装包 + SHA256SUMS.txt）
.\scripts\build-release.ps1

# 3. 零污染实机端到端验收（隔离 APPDATA + 便携包/安装卸载闭环）
.\scripts\verify-release.ps1
```

发版门禁细则见 [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md)。

---

## 说明

- 云端默认引擎会按需拉取 [`manga-translator-ui`](https://github.com/zyddnys/manga-image-translator)（GPL-3.0）及相关开源 OCR / 翻译 / 重绘模型。
- 本项目仅做本地客户端编排与计费安全保护，不内置或分发任何模型权重，也不收集任何 Google 凭据。
