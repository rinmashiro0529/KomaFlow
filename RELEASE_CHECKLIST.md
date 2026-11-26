# KomaFlow 发布前验证清单（v1.7.0）

本清单规范每次构建与发版前的可复现门禁验证，确保零计费漏停风险、多账号严格隔离与双包成品完整性。

## 1. 静态与自动化门禁

直接执行一键打包脚本即可串联完成静态校验、回归测试与双包构建：

```powershell
.\scripts\build-release.ps1
```

脚本内置门禁覆盖：
- [ ] `assets/sakura_dict.txt` 为 0 字节空文件；`assets/mtui_config.json` 满足 `cli.verbose=false`、`cli.batch_size=1`。
- [ ] `bash -n` 校验全部 4 个云端 Shell 脚本（LF 换行、无语法错误）且 `tests/scripts/test_vm_scripts.sh` 全部通过（36/36）。
- [ ] `global.json` 锁定 `.NET 8` SDK，`dotnet build MangaGui.sln -c Release` 达到 **0 警告、0 错误**。
- [ ] `dotnet test MangaGui.sln -c Release` 全部通过（212/212，Mock `IColabCli`，零真实 Colab 计费），核心覆盖：
  - 会话三态真值解析与悲观计费保护
  - 生命周期原子协调（停机抢占部署 + single-flight）
  - 多账号凭据隔离、余额优先选号、安全 Failover 与跨账号孤儿告警
  - 四档 GPU（L4 / A100 40G / A100 80G / G4 96G）自适应映射与动态费率计时
  - PROTO 2 成品校验（大小 / SHA256 / ZIP 结构 / 页数 / 图片魔数 / 自然页序）
  - 亮色 / 深色主题与全部弹窗 UI 视觉渲染测试

## 2. 发布产物清单（`dist/`）

- [ ] `dist\KomaFlow-v<version>-win-x64-setup.exe`（Inno Setup 6 安装程序，LZMA2 压缩，内置 `.NET 8` 自包含共享运行时）
- [ ] `dist\KomaFlow-v<version>-win-x64-portable.zip`（顶层含 `KomaFlow\` 目录，零 `.pdb` 残留）
- [ ] `dist\SHA256SUMS.txt`（双包 SHA256 校验和）
- [ ] 阶段目录内的 `build_manifest.json` 包含 `MangaGui.exe`、`MangaCli.exe` 及全部 9 个内置 `assets/*` 文件，且哈希与仓库 `assets/` 逐字节一致。

## 3. 零污染实机端到端验收

关闭正在运行的 KomaFlow 实例后运行：

```powershell
.\scripts\verify-release.ps1
```

自动校验：
- [ ] `SHA256SUMS.txt` 双包哈希逐字节匹配；`MangaGui.exe` 与 `Setup.exe` PE 版本元信息正确。
- [ ] 便携包顶层目录结构、零 `.pdb`、`build_manifest.json` 11 项哈希全通过；隔离 `APPDATA` 下 `MangaCli version` 正常退出、`MangaGui.exe` 正常弹出主窗口并响应 `WM_CLOSE` 零崩溃退出。
- [ ] 安装程序静默安装到临时自定义目录 → 验证默认创建的桌面快捷方式、开始菜单快捷方式、卸载注册表项与用户 `PATH`（保持 `REG_EXPAND_SZ`）→ 隔离运行安装版 GUI/CLI → 运行 `unins000.exe` 卸载并确认目录、快捷方式、注册表与 `PATH` 条目全部清理干净。

## 4. 真机冒烟清单（按需在真实 Colab GPU 上执行）

- [ ] 打开「更多 → 环境检查与修复」，确认 `colab.exe` 定位、SSH 密钥与内置脚本校验和正常。
- [ ] 拖入含中文/中括号/子目录/未补零页码的 `.zip` 或 `.cbz`，确认离线预检秒出页数与大小。
- [ ] 启动 1 小时定时会话，确认按卡显存自动选中预期方案并完成至少 1 本短漫画端到端翻译与成品校验。
- [ ] 会话运行中关闭并重开 GUI，确认自动接管并沿用原计时。
- [ ] 点击「停止并结束计费」，确认返回「已核实停止计费」且 `colab sessions` 无活动会话。
