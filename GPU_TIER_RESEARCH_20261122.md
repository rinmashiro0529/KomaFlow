# 多 GPU 档位部署方案调研（L4 → G4）

- 调研日期：2026-11-22
- 范围：Colab CLI 0.8.0 可选的 L4、A100 40G、A100 80G（`--high-mem`）、G4（RTX PRO 6000 Blackwell Server Edition 96G），对应当前流水线 manga-image-translator（下称 MIT）+ llama.cpp + Sakura-GalTransl-14B-v3.8。
- 方法：只读调研 GitHub 源码 / Release / Issue、Hugging Face 模型卡与 API、NVIDIA 数据手册、Colab 官方资料；部分硬件与费率数据只有第三方实测，已标注。**没有在任何 VM 上实测**，所有显存、并发、速度数字均为推断，需上机基准测试校准。
- 关键结论已由主会话复核原文：MIT `requirements.txt` 固定 `torch==2.14.1`；Colab `pip-freeze.gpu.txt` 为 `torch==2.14.1+cu130`；llama.cpp b12359 `ggml/src/ggml-cuda/CMakeLists.txt` 默认架构含 `80-virtual 86-real 89-real 90-virtual 120a-real`；v3.8 模型卡写明「不带后缀的版本量化等级为 Q6_K」；MIT `mode/local.py` 在 `batch_size<=1` 时逐文件 `await translate_file` 串行处理。

---

## 1. 最重要的发现

1. **当前部署脚本的 PyTorch 安装与 MIT 冲突，并且在 G4 上必然不可用。**
   [gui/assets/vm_setup.sh](assets/vm_setup.sh) STAGE1 先 `pip install -r requirements.txt`（MIT 现在固定 `torch==2.14.1` / `torchvision==0.29.1`），再 `--force-reinstall --no-deps --index-url .../cu124` 把 torch 换成该索引的最高版本 **2.6.0+cu124**，又额外装 `torchaudio==2.6.0` 和 `nvidia-cusparselt-cu12`。
   - cu124 索引最高只有 2.6.0；sm_120（Blackwell）从 PyTorch 2.7 + CUDA 12.8 才开始支持（https://pytorch.org/blog/pytorch-2-7/ ）。G4 上会出现 `no kernel image` 一类错误。
   - Colab GPU 镜像已预装 `torch 2.14.1+cu130 / torchvision 0.29.1+cu130`（https://github.com/googlecolab/backend-info `pip-freeze.gpu.txt`，2026-11-20），与 MIT 固定版本一致。
   - 结论：去掉 cu124、torchaudio、cusparselt 三步，直接沿用预装 torch。这对 L4 同样有益（消除版本不一致，节省部署时间），是所有档位的前置修复。

2. **MIT `local` 模式逐页串行，单进程无法吃满大卡。**
   - `local.py`：`batch_size<=1` 时 `os.walk` 后逐文件处理；`--batch-size>1` 只把「翻译」这一步合批，检测 / OCR / 修复 / 渲染仍逐张（`manga_translator.py` `translate_batch`）。
   - Sakura 翻译器每页只发 **一个** 请求（全部行拼成一条），行数对不上时退回逐行串行重译（`translators/sakura.py`）。
   - 因此当前 llama-server 的 `-np 8` 实际只用到 1 个 slot，且 `-c 16384 -np 8` 每 slot 只有 2048 token。
   - 大卡的收益必须靠 **多进程分片**：把一本书切成 N 份，同时跑 N 个 MIT 进程，共用一个 llama-server（`-np ≥ N`）。代码中没有锁，但输出路径由输入相对路径决定，分片不重叠即可；首次运行需先单进程预热，避免多进程同时下载模型。PR #881 讨论里维护者也建议「再起一个实例」。

3. **翻译模型在各档位保持一致是合理选择。**
   - SakuraLLM 没有比 14B（Qwen2.5 底模）更新、更大的正式模型；32B 只有 2024 年的 Qwen1.5 底模旧版（Sakura-32B-Qwen2beta-v0.9/v0.10pre1）。Qwen3 底模的 Sakura-14B-v1.5 模型卡自述为「非正式版」，且 system prompt 有改动，MIT 不改代码不能直接用。
   - GalTransl-14B-v3.8 官方只发 GGUF（Q6_K 12.12 GB、Q5_K_S 10.27 GB、Q4_K_M 8.99 GB、IQ4_XS 8.19 GB），没有 Q8_0/BF16，也没有 safetensors，所以 vLLM / SGLang 路线需要第三方转换模型（质量未验证）。
   - 结论：所有档位都用 **llama.cpp + v3.8 Q6_K**，换卡只换速度，不换译文风格；成品可比、可复现。

4. **档位之间真正的差别是三项：显存带宽（LLM 解码）、bf16 算力（检测/修复）、vCPU 数（OCR/渲染/图像 IO）。**
   L4、A100 40G、A100 80G 都只有 12 vCPU；G4 有 48 vCPU。MIT 维护者在 issue #106 指出瓶颈常在 CPU，所以 A100 80G 相比 40G 对本流水线增益很小。

5. **`vm_job.sh` 以 `-v` 运行 MIT**，会把每页中间图写到 `result/` 目录（`manga_translator.py` 中 verbose 分支），增加磁盘 IO。生产运行建议去掉。

---

## 2. 各 GPU 硬件与环境

| 项 | L4 | A100 40G | A100 80G（`--high-mem`） | G4 |
|---|---|---|---|---|
| nvidia-smi 型号 | NVIDIA L4 | A100-SXM4-40GB | A100-SXM4-80GB | RTX PRO 6000 Blackwell Server Edition |
| 计算能力 | 8.9 | 8.0 | 8.0 | 12.0 |
| 可用显存 | 22.5 GiB | 40.0 GiB | 80.0 GiB | 95.6 GiB |
| 官方显存带宽 | 300 GB/s | 1,555 GB/s | 2,039 GB/s | 1,597 GB/s |
| 实测带宽（第三方） | 221 GB/s | 1,028 GB/s | 1,203 GB/s | 1,028 GB/s |
| 实测 bf16 matmul（第三方） | 58 TF | 272 TF | 274 TF | 428 TF |
| FP8 / FP4 | 有 / 无 | 无 / 无 | 无 / 无 | 有 / 有 |
| vCPU（物理核） | 12（6） | 12（6） | 12（6） | 48（24） |
| 系统内存 | 53 GiB | 83 GiB | 167 GiB | 177 GiB |
| 磁盘 `/content` | 235.7 GiB | 235.7 GiB | 235.7 GiB | 235.7 GiB |
| CU/h 涨价前（第三方实测） | 1.54–1.71 | 5.30–5.40 | 6.77–7.52 | 8.71–8.90 |
| CU/h 2026-10-29 后（用户报告） | 约 1.7–1.94 | 约 7 | 约 8.88 | 超过 10 |

来源：
- 规格与带宽实测：https://github.com/jouyang4/AgenticColab/blob/main/docs/01-machine-shapes.md （第三方，2026-11-12 前）。
- NVIDIA 官方：https://www.nvidia.com/en-us/data-center/l4/ 、A100 数据手册 https://www.nvidia.com/content/dam/en-zz/Solutions/Data-Center/a100/pdf/nvidia-a100-datasheet-us-nvidia-1758950-r4-web.pdf 、https://www.nvidia.com/en-us/data-center/rtx-pro-6000-blackwell-server-edition/ 、https://developer.nvidia.com/cuda-gpus 。
- 费率：http://mccormickml.com/2024/04/23/colab-gpus-features-and-pricing/ （2026-03）、https://note.com/high_u/n/n59e05525fef7 （2026-09）、https://mcp-tool-shop-org.github.io/rnd/handbook/colab-golden-path/ （2026-10）、涨价报告 https://github.com/googlecolab/colabtools/issues/6194 。Google 不公布费率。

运行环境（https://github.com/googlecolab/backend-info ，2026-11-20）：Ubuntu 24.04.4、Python 3.13.16、torch 2.14.1+cu130、CUDA toolkit 13.0、cuda-compat 580.126.20。宿主驱动第三方实测各机型均为 580.82.07（CUDA 上限 13.0），G4 与其他机型镜像相同。

---

## 3. 套餐可用性

- Google 员工在 https://github.com/googlecolab/colabtools/issues/6076 （2026-08-10）称 H100 和 G4「for the time being」只对 Pro+ 开放，G4 计划「近期」恢复给 Pro；到 2026-09-08 仍有 Pro 用户反馈 G4 不可选。
- Google AI 套餐（https://developers.googleblog.com/en/colab-is-now-part-of-your-google-ai-plan/ ，2026-09-22）：订阅者有更快加速器的优先使用权，Ultra 有「Premium GPU access」。官方没有列出各档具体可用的 GPU。
- 每月 CU（第三方转引）：AI Plus 50、AI Pro 200、AI Ultra 1,000/2,000。
- `--high-mem` 需要 Colab Pro / Pro+ 权益（CLI 文档）；AI Pro 是否等同未见官方说明。
- 第三方在 AI Ultra 上实测：每种 GPU 规格最多 3 个并发会话，账号总计 20 个（2026-10-23）。

结论：**L4、A100 40G 在 Pro 基线上基本可用；A100 80G 和 G4 在 Pro 上不确定，Ultra 上有实测可用。** 程序无法预先查询权益，只能靠 `colab new` 的结果判断。

---

## 4. LLM（llama.cpp）按卡适配

- 预编译包 `llama-b12359-bin-ubuntu-cuda-12.8-x64` 的默认架构：`75-virtual 80-virtual 86-real 89-real 90-virtual 120a-real`（另含旧架构）。
  - L4（sm_89）、G4（sm_120）有原生 SASS，可直接用。
  - **A100（sm_80）只有 PTX**，首次运行要 JIT 编译。对策：设置 `CUDA_CACHE_MAXSIZE` 并实测 JIT 耗时；若耗时不可接受，在 VM 上用 `-DCMAKE_CUDA_ARCHITECTURES=80` 自行编译（会增加部署时间），或预先编译好上传二进制。
  - 驱动 580 满足 CUDA 12.8 运行时要求。
- 并发：`-np` 按 MIT 进程数 N 设置（建议 `N+1~2`），显式打开 `-kvu`（统一 KV 池；显式指定 `-np` 时默认不开），`-c` 按「每 slot 约 4096 token × slot 数」设置。
- KV 量化：预编译 FA 内核只有 `q4_0/q4_0`、`q8_0/q8_0`、`f16/f16`、`bf16/bf16` 组合，其他组合会退回；显存足够时用 f16。
- KV 显存估算：Qwen2.5-14B 为 48 层 × 8 KV 头 × 128 维，f16 下每 token 约 192 KiB（https://huggingface.co/Qwen/Qwen2.5-14B-Instruct/raw/main/config.json ）。
- 不建议的路线：vLLM 的 GGUF 支持已移到实验性插件（官方原话 "highly experimental and under-optimized"）；且 MIT 每页一个请求，高并发批处理的优势体现不出来。G4 上的 NVFP4 只能用第三方二次量化模型，质量未验证，列为后续可选实验。

---

## 5. MIT 按卡适配

- 精度：四张卡都支持 bf16，保持 `inpainting_precision: bf16`。
- `detection_size`：长边总是缩放到该值，小图也会放大（`detection/default_utils/imgproc.py`），加大不一定更准。
- `inpainting_size`：只在源图长边更大时才缩小（`inpainting/inpainting_lama_mpe.py`），设到不小于源图长边即可，再大无效。维护者在 issue #953 建议 1024–2048。
- 每个 MIT 进程常驻一份检测 / OCR / 修复模型（`local` 模式无 TTL，不在阶段间释放）。单进程显存峰值推断约 3–6 GB，需实测。
- `--batch-size` 保持 1：合批会把多页文本塞进一个请求，更容易行数错位并退回逐行翻译；并发靠多进程。
- 结论：**各档位使用同一份质量配置（`manga.json` 不变）**，只调进程数和 llama-server 参数，保证不同卡的成品一致。

---

## 6. 建议档位方案（初值，需基准测试校准）

| 档位 | colab 参数 | LLM | MIT 进程数 | llama-server 关键参数 | 显存估算 | 定位 |
|---|---|---|---|---|---|---|
| 标准 L4 | `--gpu L4` | v3.8 Q6_K | 1（试 2） | `-np 2 -kvu -c 8192 -fa on -ngl 999` | 12.1 + 1.5 + 3~6 ≈ 17–20 GB | 默认档，单价最低 |
| 加速 A100 | `--gpu A100` | v3.8 Q6_K | 3 | `-np 4 -kvu -c 16384` | 12.1 + 3 + 3×(3~6) ≈ 24–33 GB | 解码速度约为 L4 的 4–5 倍（按带宽推断），受 12 vCPU 限制 |
| （可选）A100 80G | `--gpu A100 --high-mem` | v3.8 Q6_K | 4 | `-np 6 -kvu -c 24576` | ≈ 30–40 GB | 与 40G 同为 12 vCPU，增益有限，**建议先不做** |
| 旗舰 G4 | `--gpu G4` | v3.8 Q6_K | 6–8 | `-np 10 -kvu -c 40960` | 12.1 + 7.5 + 8×(3~6) ≈ 44–68 GB | 48 vCPU + 428 TF，唯一能把多进程跑起来的档位；Pro 上可能不可用 |

建议：**正式提供 L4 / A100 40G / G4 三档**，A100 80G 待基准测试证明有收益后再加。

---

## 7. 实施前必须上机验证的事项

1. 去掉 cu124 步骤后，`pip install -r requirements.txt` 是否沿用预装的 `2.14.1+cu130`；`pydensecrf` 在 Python 3.13 下能否编译。
2. G4 上 `torch.cuda.get_device_capability()` 返回 (12, 0)，MIT 各阶段无 `no kernel image`；llama-server 在 G4 正常加载。
3. A100 上 llama.cpp 的 PTX JIT 首次耗时。
4. 单个 MIT 进程的显存峰值、CPU 占用，以及每页耗时（据此确定进程数）。
5. 多进程分片后的总吞吐是否线性增长，瓶颈是 CPU、GPU 还是 LLM。
6. 去掉 `-v` 后的速度差异。
7. `-kvu` 下长页面的 `max_tokens = max(2×字数, 512)` 是否会被 slot 上下文截断。
8. 各套餐实际能申请到哪些卡（尤其 Pro 上的 G4 和 `--high-mem`）。

建议的基准测试：同一份 30 页样本，在每张卡上依次跑 1、2、4…个进程，记录每页耗时、显存峰值、CPU 占用。估算费用：L4 约 1 CU、A100 约 3.5 CU、G4 约 5 CU，合计约 10 CU（含每次约 10 分钟部署）。

---

## 8. 对 GUI 的影响（供计划书参考）

- 档位选择、各档参考费率（替换写死的 `L4CuPerHour = 1.94`）、`colab new` 参数、恢复会话时从 `Hardware:` 字段识别档位。
- `vm_setup.sh` 按 `nvidia-smi` 识别的 GPU 选择 profile（进程数、`-np`、`-c`），GUI 传入期望档位用于校验。
- `vm_job.sh` 支持分片多进程，并汇总各进程退出码；GUI 端按输出页数统计进度的逻辑需确认兼容。
- 标题、托盘、关闭保护、启动对话框中写死的「L4」文案。
