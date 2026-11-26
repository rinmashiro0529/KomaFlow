# MangaGui 发布前验证清单（v1.7.0）

本清单用于规范每次构建与发布前的可复现门禁验证，确保零计费漏停风险、多账号严格隔离与成品完整性（对应审计项 A-15）。

## 1. 静态与脚本门禁

- [ ] `global.json` 锁定 `.NET 8` SDK 版本，`dotnet build MangaGui.sln -c Release` 达到 **0 警告、0 错误**。
- [ ] Shell 资产语法检查通过（无 CRLF 破坏、无语法错误）：
  ```powershell
  bash -n assets/vm_setup.sh
  bash -n assets/vm_job.sh
  bash -n assets/vm_status.sh
  bash -n assets/vm_pack.sh
  ```
- [ ] 确认未修改父目录 `../` 中的原始脚本（`vm_setup.sh` / `run_translate.sh` / `wait_translate.sh` / `deploy.py` / `submit.py`）。

## 2. 自动化回归测试门禁（零真实 Colab 计费）

执行：
```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test MangaGui.sln -c Release --verbosity normal
```

必须全部通过的关键回归覆盖：
- [ ] **A-01 会话三态真值**：`colab sessions` 空输出 / 乱码 / 非零退出码时，停机核实与启动探测均拒绝假设会话已停止。
- [ ] **A-02 悲观计费保护与内存优先**：`colab new` 超时/失败后复查 `colab sessions`；磁盘写/删失败不破坏内存 `HasActiveVm` 保护，且损坏的 `session.json` 会被识别并告警。
- [ ] **A-03 生命周期原子协调**：停机立即抢占并取消进行中的部署，阻止过期部署回写状态；并发停机共享单次执行（single-flight）。
- [ ] **A-04 成品完整性与 PROTO 2 契约**：等待 `JOB_ALIVE=0` 且 `JOB_EXIT_CODE=0` 后才打包；拒绝 22 字节空 ZIP、截断 ZIP、SHA256 不匹配、缺页及伪造魔数图片。
- [ ] **A-05 / A-08 输入安全与自然页序**：验证体积/页数/解压上限、路径穿越拦截、图片魔数校验、`1.jpg / 2.jpg / 10.jpg` 自然页序。
- [ ] **A-06 / A-07 隔离清理与健康失联**：失败/取消 `finally` 清理远端 `/content/jobs/<job_id>`；`stop_job` 未确认成功时自动暂停队列；连续轮询失联自动标记失联。
- [ ] **A-09 / A-10 / A-11 / A-12 / A-13**：到期弹窗默认 `None` 不静默续费；脚本预检阻断 `colab new` 且用户配置优先；`llama :6006` 独立就绪；直连 `colab.exe` 参数往返与日志脱敏；单任务移除/重试与 `.cbz`/自定义输出目录。
- [ ] **UI 视觉渲染**：亮色 / 深色主题与全部弹窗（含诊断、账号管理、启动选号弹窗）无裁剪、无对比度异常、设计令牌（间距/图标/资源键）全量通过。
- [ ] **多账号隔离与选号安全（v1.5.0）**：`GOOGLE_APPLICATION_CREDENTIALS` 与 `--config` 严格按进程注入；自动选号优先空闲高余额；安全 Failover 仅在 `ExplicitlyAbsent && newUnnamed == 0` 触发；跨账号孤儿会话探测与一键停机；运行中锁定账号并禁用删除/切换；`OfflineGuardColabCli` 拦截离线命令的多账号云端调用。

## 3. 发布产物与校验

1. 确认无残留 `MangaGui.exe` 进程占用目标目录：
   ```powershell
   Get-Process MangaGui -ErrorAction SilentlyContinue
   ```
2. 执行单文件发布：
   ```powershell
   & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish src\MangaGui\MangaGui.csproj -c Release -r win-x64 --self-contained false `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
   ```
2b. MangaCli 使用相同参数发布到同一目录：
   ```powershell
   & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish src\MangaCli\MangaCli.csproj -c Release -r win-x64 --self-contained false `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
   ```
3. 验证发布目录包含：
   - `publish\MangaGui.exe`
   - `publish\MangaCli.exe`
   - `publish\assets\vm_setup.sh`
   - `publish\assets\vm_job.sh`
   - `publish\assets\vm_status.sh`
   - `publish\assets\vm_pack.sh`
   - `publish\assets\mtui_patch.py`
   - `publish\assets\mtui_config.json`
   - `publish\assets\sakura_dict.txt`
   - `publish\assets\prompt_hq.yaml`
   - `publish\assets\manga.example.json`
   - `publish\MangaGui.runtimeconfig.json`（随单文件发布产生；若 MangaCli 也产出对应 `.runtimeconfig.json` 一并保留）
   - `publish\build_manifest.json`（含版本号、构建时间与各产物 SHA256；**必须同时包含 MangaGui.exe 与 MangaCli.exe**，所有内置资产的哈希必须与 `gui\assets\` 完全一致）
4. 资产完整性：对 4 个 `.sh` 执行 `bash -n` 语法检查；`sakura_dict.txt` 必须是 0 字节空文件；
   `mtui_config.json` 必须 `cli.verbose=false`、`cli.batch_size=1`。

## 4. 真机冒烟清单（仅在用户自行开启 L4 时执行）

- [ ] 启动 GUI，打开右上角「诊断」，确认 `CLI 启动模式` 为「直接启动 colab.exe」且内置脚本校验和非空。
- [ ] 拖入一份含中括号/中文/子目录/未补零页码的 `.zip` 或 `.cbz`，确认离线预检秒出页数与大小。
- [ ] 启动 1 小时定时会话，验证部署完成后状态为「服务就绪」，并完成 1 本短漫画端到端翻译与校验下载。
- [ ] 部署阶段确认 `assets\vm_setup.sh` 五个阶段依次通过（首次真实运行需重点观察 STAGE1/STAGE2 的下载与 `STAGE5` 的可选跳过）。
- [ ] 会话运行中关闭并重新打开 GUI，确认直接恢复为「服务就绪」并沿用原计时（验证 `colab sessions` 真实输出的解析）。
- [ ] 翻译一本大于 48 MiB 的压缩包，确认日志出现「分 N 片上传」「分 N 片下载」且成品校验通过；记录实际单片耗时，用于校准分片大小。
- [ ] 点击「停止并结束计费」，确认返回「已核实停止计费」且终端执行 `colab sessions` 显示无活动会话。
