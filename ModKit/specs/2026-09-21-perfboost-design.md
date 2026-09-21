# flotsam.perfboost 性能优化与探针 — 设计文档

> 日期：2026-09-21。状态：已获用户确认（画质取舍=轻微损失可接受；卡顿=整体帧率低；分辨率 2K）。
> 配套：`ModKit/开发交接报告.md`（架构与生命周期约定）、`ModKit/使用说明.md`。

## 1. 背景与诊断结论

用户报告"游戏完全没用到显卡、帧率低、建筑多了卡"。证据链：

| 检查 | 结果 |
|---|---|
| `Player.log` 启动段 | `Direct3D 11.0 / Renderer: NVIDIA GeForce RTX 4070 SUPER` —— **渲染就在独显上** |
| 运行中进程 GPU 计数器（`\GPU Engine(*)\Utilization`） | 游戏占用 GPU **3D 引擎** 2%~14% —— 在用，但吃不饱 |
| 结论 | **CPU 主线程瓶颈**（Unity Mono：逻辑 + 渲染提交都在主线程；建筑/小人越多越明显）。任务管理器显示 0% 是图表引擎选错/占用确实低，不是"没用显卡" |

反编译确认的浪费点（行号对应 `_mod_recon/src/Assembly-CSharp.decompiled.cs`）：

1. **CTAA 空转**：`CTAA_PC`（1309）挂在 **3 台相机**上（日志 `CTAA Standard Mode Enabled` ×3）。游戏实为 **URP**（`UniversalAdditionalCameraData` 35032、`ShadowManager._renderPipeline` 109451、RenderGraph using），CTAA 的 `OnRenderImage`（1744）在 URP 下**不会执行**，但它的 `OnEnable`（1465）仍给每台相机强加 `depthTextureMode |= Depth | MotionVectors`（运动向量=运动物体每帧多画一遍），`OnPreCull`（1686）每帧做投影抖动。纯浪费。
2. **日志刷屏**：`ParticleController.OnDestroy`（45200，方法体仅一条 `LogFormat`）与 `PrefabPool.PrefabReference.OnDestroy`（201566，仅一条 `LogWarningFormat`），游玩中持续触发；每条 Log 默认还带栈回溯采集。
3. **游戏设置粒度过粗**：`VideoSettingsPanel`（83386）只暴露 分辨率/画质档/垂直同步/UI 缩放，阴影距离、粒子密度、渲染比例都调不了。

## 2. 目标与非目标

**目标**
- 立即可感知的帧率提升（关空转、削隐藏开销），每项可独立开关、禁用即回滚；
- 一个 **F9 性能面板**（ProfilerRecorder 分项计时 + 对象计数 + 定期写日志），让后续优化由数据驱动；
- 不修改游戏本体文件、不影响存档、不改动其他 8 个 mod。

**非目标（v1 不做）**
- 深度游戏逻辑补丁（寻路/任务派发节流）——等探针数据定位热点后另立项；
- 强制 D3D12 / boot.config 改动（风险项，留作备选实验）；
- 动态分辨率。

## 3. 结构与分层

沿用既有分层：**游戏类型只出现在 Game 层**，mod 层只做配置/按键/补丁注册/UI。

```
Flotsam.ModKit.Game/
  GamePerf.cs          新增：全部优化项的应用与回滚（CTAA/阴影/动画剔除/粒子/物理步长）
mods/FlotsamMod.PerfBoost/
  PerfBoostMod.cs      入口：配置、按键(F9)、Harmony 注册、HUD 按钮、生命周期
  PerfProbe.cs         ProfilerRecorder 包装 + FPS 统计（ring buffer，1% low）
  PerfOverlay.cs       F9 面板（UiWindow，ContentMode.None，4Hz 刷新，10s 日志快照）
Mods/flotsam.perfboost/mod.json
build.ps1              $mods 增加 'FlotsamMod.PerfBoost' = 'flotsam.perfboost'
Flotsam.ModKit.Game.csproj  增加 Unity.RenderPipelines.Universal.Runtime.dll 引用（Private=false，游戏自带）
```

## 4. 优化项明细（配置键 / 默认 / 机制 / 回滚）

| 项 | 配置键(默认) | 机制 | 回滚 |
|---|---|---|---|
| CTAA 空转 | `disableCtaa`(true) | `FindObjectsByType<CTAA_PC>(Include)`：记录原值 → `CTAA_Enabled=false`、`enabled=false`、相机 `depthTextureMode &= ~(Depth\|MotionVectors)`。GameStart 时执行 | 逐组件恢复原值（`enabled=true` 时游戏自己重建材质） |
| 日志降噪 | `suppressLogSpam`(true) | Harmony prefix 跳过 `ParticleController.OnDestroy` 与 `PrefabPool+PrefabReference.OnDestroy`（两方法体均只有日志）；`Application.SetStackTraceLogType(Log/Warning, None)` | host 撤补丁；OnDisable 恢复 StackTraceLogType 默认值 |
| 阴影距离 | `shadowDistance`(100；0=不动) | 双写：`QualitySettings.renderPipeline as UniversalRenderPipelineAsset`（当前画质档的活跃 asset）+ `ShadowManager.SetShadowDistance`（游戏自带 API，109498）。GameStart 执行 | OnDisable/OnGameEnd 恢复记录的原值 |
| 离屏动画剔除 | `cullOffscreenAnimators`(true) | GameStart 全扫 + `BuildableBuilt/Placed`、`AgentAddedToPlayerCommunity` 事件置脏后补扫：`Animator.cullingMode = CullUpdateTransforms`，**仅处理子树含 Renderer 的**（避开 UGUI 动画）。逐实例记录原值 | 逐实例恢复 |
| 粒子密度 | `particleRateScale`(0.5；1=不动) | postfix `ParticleController.Initialize(Transform,Vector3)`（45208，Spawn 两条路径都经它）：首次见到的实例按倍率缩 `emission.rateOverTimeMultiplier/rateOverDistanceMultiplier/main.maxParticles`，逐实例记录原值 | 逐实例恢复 |
| 渲染比例 | `renderScale`(1.0=不动) | 活跃 URP asset `renderScale`（0.85 等）。默认关 | 恢复原值 |
| 物理步长 | `fixedDeltaMs`(0=不动) | `Time.fixedDeltaTime = ms/1000`（实验项，等探针证明 FixedUpdate 是热点再建议开） | 恢复 0.02/原值 |

通用约束：GameStart 才应用场景类优化（相机/Animator 随场景销毁，每次进存档重做）；OnGameEnd 清空实例账本；OnDisable 全量恢复；每步 try/catch 防呆——**任何一项失败只记 Error，不影响其他项与游戏本身**。

## 5. F9 性能面板

- **开关**：`F9`（可改键）或 HUD 按钮「性能面板」；位置/尺寸记忆（`window.*`）；进存档懒构建，退存档销毁重建（NativeSkin 随场景销毁的既有约定）。
- **内容**（4Hz 刷新）：FPS（0.5s 均值 + 1% low）/ 帧 ms；分项毫秒：Update 脚本(`BehaviourUpdate`)、LateUpdate、FixedUpdate、物理(`Physics.Processing`)、动画(`Animator.Update`)、粒子(`ParticleSystem.Update`)、渲染、UI、GC 每帧分配；DrawCall/SetPass；计数：建筑(`Community.Buildables.Count`)/小人(`Agents.Count`)/活跃粒子/已剔除动画数。
- **探针鲁棒性**：候选 marker 逐个 `ProfilerRecorder.Valid` 探测，无效项显示 `-` 并在首次构建时把"哪些可用"写一行日志（不同 Unity 版本 marker 名有差异，靠探测不靠猜）。
- **日志快照**：`logIntervalSec`(10) 一行写 `LogOutput.log`，供事后分析。

## 6. 验证

1. `build.ps1` 编译全绿（0 error/0 warning），安装到 `Mods/flotsam.perfboost/`。
2. 用户重启游戏 → 进存档：日志应见 `perfboost loaded` + 各优化项应用行；F9 面板出数。
3. 对比：同视角同存档，开关 mod 前后 FPS/帧 ms（面板自证）。
4. 回滚验证：F10 禁用本 mod → CTAA/阴影/动画/粒子/日志全部回原版。

## 7. 风险与边界

- **CTAA 关闭后画面可能略锐/少一层亚像素抖动**——URP 下它本就不做抗锯齿，观感差异应接近零；若用户不喜欢，关 `disableCtaa` 即恢复。
- `CullUpdateTransforms` 仍推进动画状态机（事件照发），只停屏外骨骼写入；不用于无 Renderer 的 UGUI 动画。
- 粒子倍率只乘 `Multiplier` 属性，不破坏发射曲线模式；池化实例只缩一次（实例 ID 记账）。
- ProfilerRecorder 在 release Mono 播放器可读内建 marker；个别 marker 缺失时面板显示 `-`，不影响其他项。
