# 设计方案：批量管理（flotsam.batchmanager）+ 一键电网（flotsam.powerlink）

> 状态：已与用户对齐（交互模型、批量操作集、确认策略、连网策略、快捷键均已确认）。
> 前置阅读：`ModKit/开发交接报告.md`（架构、UI kit 约定、构建流程）。
> 反编译引用：`_mod_recon/src/Assembly-CSharp.decompiled.cs`，下文行号均指该文件。

---

## 1. 目标与非目标

### 目标
- **Mod A 批量管理**：按「同型号建筑」分组勾选多个实例，批量执行拆除/取消拆除、升级/取消升级、启用/停用；勾选即在世界内高亮，支持逐个跳转核对，防止选错。
- **Mod B 一键电网**：一键把全镇所有可连的未供电建筑按最短电缆接入带电网络（复刻游戏自身的连线合法性规则）；支持带电电网间的「智能互济」合并；可选常驻自动模式。

### 非目标（本期不做）
- 自动**建造**电线杆（涉及资源消耗与摆放合法性，超距建筑只在汇总里提示"需要电线杆"）。
- 批量改名、批量生产设置、跨型号混合批量。
- 电网的手动断线管理界面（原生已有）。
- 对原生 BuildableOverview / 选中面板做任何 Harmony 改动（两个 mod 均零补丁，纯读 + 调游戏公开方法）。

---

## 2. 技术依据（已核实的游戏 API）

### 2.1 批量操作（Mod A）
| 能力 | API | 行号 | 备注 |
|---|---|---|---|
| 拆除 | `Buildable.Salvage()` | 16718 | 与原生拆除按钮同一调用（4197）；Finished→SalvageShutdown→Deconstructing，派小人执行，可撤回 |
| 取消拆除 | `Buildable.CancelDeconstruction()` | 16763 | 原生按钮同调用（4193） |
| 拆除预检 | `Buildable.CanBeDeconstructed(out LocalizedString)` | 16891 | 返回不可拆原因（搬料中/升级中等） |
| 升级 | `Buildable.Upgrade()` | 16589 | 自带资源/解锁/阶段检查，不满足时静默返回 |
| 取消升级 | `Buildable.CancelUpgrade()` | 16560 | |
| 升级预检 | `Buildable.CanUpgrade()` | 16643 | Finished + 科技解锁 + 社区资源足够 |
| 开关 | `Buildable.Activate()/Deactivate()`、`IsActive` | 16436/16446 | 原生开关按钮同调用（4254-4264） |
| 型号判定 | `Buildable.Properties`（共享 ScriptableObject） | 15814 | 同 Properties 引用 = 同型号；升级会换 Properties（StartUpgrade 16658 实例化 `Properties.Upgrade.Prefab`） |
| 世界高亮 | `Buildable.OutlineRenderer` → `OutlineRendererComponent.UpdateSelectedObject()` / `ResetHighlightOutline()` | 165096/165178 | 游戏连电缆时高亮候选建筑用的同一对 API（108516→165220 附近、90367-90373） |
| 相机定位 | 复用 `GameBuildings.Focus()`（`CameraController.Lock` + `OnSelected(true)`） | 17237 | 与建筑总览行为一致 |
| 枚举 | `Community.PlayerCommunity.Buildables`（已封装 `GameBuildings.All()`） | | |

注意：`OnDeselected()`（17244）会对刚取消选中的建筑调 `ResetHighlightOutline()`，可能清掉我们的高亮 → 需要低频重刷（见 4.4）。

### 2.2 电网（Mod B）
| 能力 | API | 行号 | 备注 |
|---|---|---|---|
| 连接（核心） | `EnergyGrid.Connect(from, to)` static | 39166 | 双向 Connect + `Merge` 两电网 + 派发 `EnergyGridConnectionAdded` |
| 电缆可视化 | `EnergyGridConnectionVisualizer` 监听上述事件自动生成 | 39244-39307 | 无需自己画线 |
| 持久化 | 连接存进 `Connections` 数组，随原生存档序列化（`RestoreReferences` 124562/39675） | | mod 连接与手动连接在存档层无差别 |
| 全部连接器 | `EnergyGridManager.Grids`（static）→ `grid.Links` | 39706/38785 | 每个建成用电建筑 `Finish()` 时自建一个 grid（20568-20575），电线杆/镇心也在 Links 里 |
| 合法性规则 | 距离(水平) < `GameManager.Settings.BuildableSettings.CableLinkRange`；双方 `CanConnect()`；未互连 | 39612-39614、39660、90325-90348、90417-90431 | 完整复刻 `EnergyGridConnectCursorProperties.Connect/UpdateComponentsInRange` |
| 空槽判定 | `EnergyGridConnector.CanConnect()` = `_connectionsCount < _connectionsCapacity`；建筑组件要求 `BuildPhase==Finished` | 39660/20717 | 默认容量 2（39380），电线杆容量更大 |
| 带电判定 | `EnergyGrid.IsTownheartGrid / HasEnergy / ReturnEnergyProduction() / ReturnStorageEnergy() / ReturnEnergyRequirement() / GridEfficiency` | 38795-38815、39050-39126 | |
| 未连线警示 | 连接器无连线时游戏自动挂 `ErrorNotLinkedToEnergyGridProperties` 故障（39566-39579），连上自动消除 | | 可作为"未供电"的交叉验证 |

免费性：原生连线不消耗任何物品（只有造电线杆花资源），mod 连线同样零成本。

---

## 3. Mod A：flotsam.batchmanager「批量管理」

### 3.1 入口与快捷键
- 快捷键 **Ctrl+1**（用户指定）：`Keybinds.Register("batch.toggle", KeyCode.Alpha1, …)`，`OnTick` 里判 `_hotkey.IsDown && GameKeys.GetCtrlHeld()`（KeybindService 不支持组合键，沿用 MaterialHelper 的 shift 组合先例，920 行）。keybinds.json 只存基础键，Ctrl 前缀由 mod 代码要求。
- HUD 按钮「批量管理」：`Ui.AddHudButton`，图标 `NativeSkin.Find("build","hammer","construction")`（与建筑总览同族），可拖拽+记忆。

### 3.2 窗口（全部走 UiWindow/NativeSkin/GameUi，禁止自造样式）
```
┌─薄荷标题栏（拖拽把手 | 批量管理 | [提示开关][✕]）──────────┐
│ [搜索框(SearchInput)]  状态行：显示 N / 共 M 座            │
│ ┌左栏:型号列表────┐ ┌右栏:实例列表(ScrollList)──────────┐ │
│ │图标 名称  x12   │ │☑ 图标 名称  状态  ⚠故障  [定位]   │ │
│ │(状态摘要点)     │ │☐ …                               │ │
│ └─────────────────┘ └────────────────────────────────────┘ │
│ 工具条: [全选][反选][清空] 已选 N 座   ◀ 3/8 ▶            │
│ 操作栏: [批量拆除][取消拆除][批量升级][取消升级][启用][停用] │
│ 提示区(可隐藏): 操作说明 + 高亮/跳转用法                   │
└──────────────────────────────────────────────────────────────┘
```
- **左栏型号行**：顶部固定「全部建筑」项，其下按原生分类做小节头（类别图标+类别色，同 BuildingFinder），每个分类下列出该类的**型号**行（按 `Buildable.Properties` 聚合：图标 `Properties.GetIcon()`、名称取 `LocalizedNameTerm` 本地化、数量、状态摘要 `可升级n/拆除中n/建造中n`）。选中行用所属分类色 tint（复用 MakeCategoryButton 配色手法）。
- **右栏实例行**：
  - 勾选框：`GameUi.IconButton` + 原生开关图标 `UI_Icon_InfoPanel_On/Off_64x64`（NativeSkin 已采集）。
  - 名称后缀状态：建造中/升级中/拆除中/停用（沿用建筑总览的 DimText 手法）。
  - ⚠ 故障数：复用 BuildingFinder 的 `PopulateMalfunctions` 行内显示（464-516 行模式）。
  - [定位]：原生定位图标 `UI_Icon_InfoPanel_Locate_64x64`。
- **排序**：右栏用 `GameBuildings.Sort()`（原生序）；提供配置 `sortByDistance`（默认 false）切换为按到镇心距离升序——配合「选前 N 个」语义（先勾最远的/最近的）。
- 搜索框过滤型号名（输入时推 `UIState.Typing`，与 BuildingFinder 一致）。
- 窗口尺寸默认 ~760x680，`ContentMode.Uniform`，位置/尺寸/提示开关按 `window.*` 记忆；`TipsChanged` 时操作栏下方提示区整体收起（遵守交接报告 §2.2 回收约定）。

### 3.3 勾选与世界高亮（防选错核心）
- 勾选集合 `HashSet<Buildable>`，型号切换/刷新时保留（按实例引用）。
- **勾选即高亮**：勾上 → `OutlineRenderer.UpdateSelectedObject()`；取消 → `ResetHighlightOutline()`。
- 窗口可见且 `highlightChecked=true` 时以 **2Hz** 重刷全部勾选高亮（抵消原生 OnDeselected 的清除，17247）。
- 清除时机：取消勾选、关窗、OnDisable、OnGameEnd（场景切换前必须全清，避免残留描边）。
- **逐个跳转**：`◀ ▶` 在勾选列表内循环，显示 `当前序号/总数`，每跳 = `GameBuildings.Focus(b, zoomLevel)`（相机 Lock + 打开原生面板）。跳转不关窗（与建筑总览 `hideAfterFocus` 区分：本窗默认不隐藏，配置 `hideAfterFocus` 默认 false）。

### 3.4 批量执行语义
- 全部走 Game 层新文件 **`GameBatch.cs`**（Flotsam.ModKit.Game 工程），逐个建筑：预检 → 调用 → 记录结果/跳过原因：
  - 拆除：`CanBeDeconstructed(out err)` 通过才 `Salvage()`；已在拆除阶段的算「跳过(已在拆除)」。
  - 取消拆除：仅对 SalvageShutdown/Deconstructing/HaulTo(cancel 标记) 阶段调用。
  - 升级：`CanUpgrade()` 通过才 `Upgrade()`；`Properties.Upgrade==null` 记「无升级」，资源不足记「资源不足」。
  - 取消升级：仅 UpgradeShutdown/UpgradeHaulTo 阶段。
  - 启用/停用：仅对 Finished 建筑调用；「支持开关」的判定实现时核实——优先读 Properties 的动作资产列表是否含 `BuildableActionOnOff`（4246），取不到则退化为 try/catch + `IsActive` 前后比对，不支持的建筑记「不支持开关」跳过。
- 每次批量后 Toast 汇总：`已下令拆除 8 座，跳过 2 座（1 资源不足、1 正在搬料）`；`verbose` 时逐条写日志（`[batchmanager] salvage <name> ok/skip:<reason>`）。
- 执行后保持窗口打开并刷新列表（状态列会变化：拆除中/升级中）。

### 3.5 拆除二次确认（内联，不用原生弹窗——PopUpDialog 无可自定义文案的通用确认 API，57323 起核实）
- 勾选数 ≥ `confirmThreshold`(默认 5) 时点「批量拆除」→ 操作栏原位变红色危险条：`确认拆除 N 座「型号名」？ [确认拆除] [取消]`（危险红 `(0.87,0.30,0.26)`，交接报告 §2.1 色板）。
- 确认条期间禁用其他操作按钮；**执行任何其他交互（勾选/切换型号/搜索/其他批量操作）、「取消」按钮、超时 10s、关窗**均退出确认态。
- 升级/开关不确认。

### 3.6 配置与事件
- 配置键：`window.*`、`button.*`、`highlightChecked`(true)、`confirmThreshold`(5)、`sortByDistance`(false)、`hideAfterFocus`(false)、`zoomLevel`(0.6)、`includeUnfinished`(true)、`verbose`(false)、`schema`(1)。
- 刷新事件：`BuildableBuilt / BuildablePlaced / BuildableSalvaged / BuildableUpgraded` → MarkDirty（可见时重建列表；勾选集合按引用剔除已拆除的）。
- 生命周期：窗口惰性构建（首个进存档的 tick），GameEnd 销毁 + 清高亮，与 BuildingFinder 相同骨架。

---

## 4. Mod B：flotsam.powerlink「一键电网」

### 4.1 入口
- 快捷键 **Ctrl+2**（用户指定）：注册 `KeyCode.Alpha2`，`IsDown && GameKeys.GetCtrlHeld()` → 立即执行一次连接。
- HUD 按钮「连电网」（一键执行）+ HUD 切换按钮「自动连网:关/开」（点击切换 `autoMode` 并写配置；标签随状态变化）。
- 无窗口：结果全部走 Toast + 日志。

### 4.2 连接算法（Game 层新文件 **`GameEnergy.cs`** 承载数据面，mod 侧只做调度）
```
Snapshot():
  遍历 EnergyGridManager.Grids → Links 收集全部连接器 c：
    { c, pos=c.ConnectionTransform.position.Leveled(), free=c.CanConnect(),
      isPole=非建筑连接器(EnergyGridPole/Decoration), grid=c.EnergyGrid }
  带电根集合 R = { c | c.grid.IsTownheartGrid || c.grid 发电>0 || c.grid 储电>0 }

一键执行管线: 快照 → 整理决策(4.2.2) → 连接执行 → 智能互济合并 → 汇总

4.2.1 增量连接（Prim 贪心，最小总缆长；先内存干跑、后统一执行）:
  1) 干跑建树：网络集 N ← R 中所有连接器（整理模式下还包含既有连接
     的两端，见 4.2.2）；每轮在「一端∈N 另一端∉N、双方 free、未互连、
     水平距离<CableLinkRange」的边里取距离最小者（平手优先 free 槽位
     多的一端，再平手优先接建筑而非杆），把边记入生成树 T，把另一端
     并入 N（槽位占用在干跑中记账）。
  2) 剪枝（仅当 connectPolesToGrid=false）：在 T 上迭代删除「叶子是
     电线杆/装饰」的边——杆只作通往建筑的接力（Steiner 点），孤立
     备用杆、纯杆-杆死胡同不白拉线。默认 true 时不剪枝：所有够得着的
     杆也接入（消除杆自身的「未连接电网」故障，并留作将来接力）。
  3) 执行：对每条边复核原生合法性再 EnergyGrid.Connect(a,b)。

4.2.2 既有连线整理（optimizeExisting=true 时，在增量连接前决策）:
  现状边集 E = 各连接器 Connections 数组去重；existingTotal = Σ水平长度。
  两次干跑对比：
    A. 增量方案：保留 E 不动，对未连接目标跑 4.2.1 → 新增缆长 addTotal；
    B. 重构方案：无视 E（槽位全部视为空闲），对全图跑 MST 干跑，其中
       E 内的边权重 ×(1−keepBonus=2%)，平手时倾向保留现线，避免无谓翻动
       → mstTotal。
  gain = (existingTotal + addTotal) − mstTotal。
  gain ≥ max(existingTotal × optimizeGainPct%(默认10), 5u 固定下限)
  → 执行重构：
  （裁决记录：原稿下限为「一根 CableLinkRange」，实现改为固定 5u——
  CableLinkRange 默认仅 20u 且随设置变化，作下限会让整理在中小镇几乎
  永不触发或行为不可预期；5u+pct 双门槛已足够防翻动。）
    移除：E∖MST 逐条 EnergyGrid.Disconnect(a,b)（39182；原生事件删电缆、
    UpdateGrids 重分电网）；
    新增：MST∖E 逐条复核合法性后 EnergyGrid.Connect(a,b)。
    移除与新增在同一帧内完成，不存在跨帧断电窗口。
    【实现后修订 2026-09-21：用户遭遇一次「自动连网+新建电池」硬闪退（根因未定位），
    执行模型改为分帧会话——BeginRun 建队列、每 tick ≤ connectsPerTick(4) 条、每条前重查
    Ready；「同帧」承诺被取代，手动整理时拆/接跨数帧（短暂低电力窗口，可接受的加固代价）。
    另增抗崩溃痕迹文件 Mods/flotsam.powerlink/trace.log（逐条 pre/post 立即落盘）。
    详见开发交接报告 §3.7。】
  gain 不达标 → 现网原样保留，只走增量方案 A。
  整理结果计入汇总与日志（移除X根/新增Y根/总长缩短Z%）。

智能互济合并（mergePoweredGrids=true 时，主体连完后执行）:
  枚举分属不同带电电网、可互连的连接器对 (a,b)：
    若 a.grid.GridEfficiency<1（缺电）且 b.grid 有富余
      （发电≥需求 或 储电>0 且效率==1）→ 连最短的一对，合并后重算，
      继续直到没有「缺电×富余」可连对。
  两个都富余或两个都缺电的电网不合并。

汇总:
  { 新连接建筑数, 新电缆数(含杆-杆接力), 整理移除/新增电缆数与缩短比例,
    合并电网数, 无法连接的建筑数(范围内无可连端点/槽位满/未建成) }
```
- **合法性**：每条边执行前再跑一次原生同款检查（`a.CanConnect() && b.CanConnect() && !a.IsConnected(b) && dist<CableLinkRange`，90325-90348 复刻）；`EnergyGrid.Connect` 自带 Merge 与事件派发，电缆可视化、故障清除、存档持久化全部由原生代码接管。
- **执行时机**：只在 `GameApi.IsPlaying && !IsMapOpen && !IsPaused` 时运行；一次全量在毫秒级（连接器数量 O(n²) 但 n≤数百，可接受；>500 时改用按网格分桶，暂不实现，日志提示）。

### 4.3 自动模式（`autoMode`，默认关）
- 开启时：监听 `BuildableBuilt / BuildablePlaced`（防抖 3s 合并触发）+ `autoIntervalSec`(默认 10s) 心跳兜底，静默跑同一算法。
- 静默 = 无新增连接时不发 Toast；有新增时发简短 Toast（`自动连网：接入 2 座建筑`），`verbose` 关时不刷屏（同一批 5s 内合并播报）。
- 重入保护：执行中标志位；开世界地图/暂停时跳过本轮。
- driver：mod 类 `OnTick` 计时即可，无需自建 GameObject（无每帧逻辑，只有低频扫描）。

### 4.4 配置
- `autoMode`(false)、`autoIntervalSec`(10)、`optimizeExisting`(true：一键时检查既有连线是否最优，收益达标才重构，见 4.2.2)、`optimizeGainPct`(10：重构收益阈值%)、`mergePoweredGrids`(true)、`connectPolesToGrid`(true：够得着的电线杆本身也接入电网；false 时杆只作接力、不为其单独拉线，见 4.2.1 剪枝)、`verbose`(false)、`button.*`（两个 HUD 按钮各自记忆位置）、`schema`(1)。
- keybinds：`powerlink.connect` = Alpha2（Ctrl 组合在代码里）。
- 自动模式只跑「增量连接」，不跑既有连线整理（重构只由手动一键触发，避免后台悄悄翻动玩家布线）。

---

## 5. 共用工程约定

- 新工程：`ModKit/src/mods/FlotsamMod.BatchManager/`、`FlotsamMod.PowerLink/`，csproj 照抄 BuildingFinder（netstandard2.1、无 NuGet、Reference 指 Managed + BepInEx core）。
- Game 层新增：`Flotsam.ModKit.Game/GameBatch.cs`、`GameEnergy.cs`（唯一允许触碰游戏类型的层，防御式 try/catch，场景切换不抛异常）。
- `build.ps1` 的 `$mods` 表加两行映射；安装目录 `Mods/flotsam.batchmanager/`、`Mods/flotsam.powerlink/`（mod.json 人工维护，字段照抄 buildingfinder：apiVersion 1.1、permissions [ui,keybinds,config]）。
- UI 素材：只用 NativeSkin 已采集名单（面板/按钮/开关 On-Off/定位/关闭/加减速图标）+ 游戏自己的建筑图标与分类图标；字体走 `GameUi.Label`（粗体约定遵守交接报告 §2.1）。
- HUD 按钮/Toast/窗口外壳全部走 UiService/GameUi 既有封装；窗口遵守提示区回收、位置记忆、置顶开关约定。
- **日志策略（两 mod 统一，输出到 `BepInEx\LogOutput.log`，走 host 的 `ILog`——ModKitLog 已按 mod 源打标签）**：
  - **恒记（Info，低频、单行、含关键数字）**：mod 生命周期（ready/removed/窗口构建）；每次用户动作一行汇总（`salvage 8 ok, 2 skip` / `connect: +12 buildings, +15 cables, rebuild -4/+6 (-18%), merge 2, unreachable 7`）；autoMode 开关切换。
  - **verbose=true 才记（Info）**：逐项/逐边明细（`salvage <name> skip:<reason>`、每条 MST 边 `edge A↔B d=32.1 keep/new/drop`）、快照统计（连接器数/带电根数/耗时 ms）、高亮重刷异常。
  - **Warn**：可恢复的异常状态（反射成员缺失、跳过某建筑因 API 抛错、连接器数 >500 提示未做分桶）。
  - **Error**：捕获的功能性异常（带堆栈）。
  - **禁止**：OnTick/每帧输出；自动模式空转心跳输出；5s 内重复的同文案行（去重计数合并成 `×N`）；与 Toast 内容完全重复的行（Toast 汇总进日志一条即可）。
- 两 mod 均**零 Harmony 补丁**。

## 6. 风险与缓解
| 风险 | 缓解 |
|---|---|
| Ctrl+1/2 可能与游戏自身绑定冲突 | 基础键可在 keybinds.json 改；冲突时 Toast 不受影响（我们只在 IsDown+Ctrl 时动作）；实测确认 |
| 原生取消选中清掉批量高亮 | 2Hz 重刷（3.3） |
| 型号分组在升级瞬间变化（Properties 更换） | 事件驱动刷新 + 勾选按实例引用保留 |
| 批量拆除误操作 | 阈值内联确认 + 拆除本身可取消（游戏机制兜底） |
| 合并大电网导致效率下降 | 智能互济只在「缺电×富余」时合并（4.2） |
| 整理功能翻动玩家手排的布线 | keepBonus 倾向保留现线 + 收益阈值（默认 ≥10% 才动）+ 只由手动一键触发 + Toast/日志报告移除/新增数 |
| Connect 在场景切换瞬间调用崩溃 | GameApi.IsPlaying 门禁 + Game 层全防御 try/catch |
| 自动模式事件风暴（EnergyGridsUpdated 自触发） | 不订阅 EnergyGridsUpdated，只用建筑事件+心跳+重入保护 |

## 7. 验收标准
1. `build.ps1` 全绿；重启游戏后 F10 管理器可见两个新 mod，日志无红。
2. Ctrl+1 / HUD 开批量管理窗：型号分组数与建筑总览一致；勾选行世界内出现描边高亮，取消勾选消失；◀▶ 逐个跳转正确。
3. 勾 3 座拆除 → 小人去拆、Toast 汇总正确；勾 ≥5 → 出现红色确认条，取消无效化；「取消拆除」能撤回。
4. 批量升级：资源足够的进入升级流程，不足的跳过并计入 Toast；「启用/停用」生效。
5. Ctrl+2 / HUD 一键连网：范围内未供电建筑全部接入（含经已建电线杆接力），电缆可视、`未连接电网`故障消失；超距建筑出现在「无法连接」计数里。
6. 整理：手动把某建筑连到远处端点（明显非最短）→ 一键后该线被移除并按最短重连，Toast/日志报告移除/新增数；仅微小收益（<阈值）时现网不被翻动；自动模式不触发整理。
7. 缺电网 A 与富余网 B 在范围内时自动合并；两富余网不合并（可用 `mergePoweredGrids=false` 全关）。
8. 自动模式开启后，新建筑建成数秒内自动接线；无目标时零 Toast 零日志刷屏。
9. 日志卫生：正常游玩一段（含两次一键、一次批量、自动模式开启）后 `LogOutput.log` 中两 mod 的行数 ≤ 数十行且全是单行汇总；`verbose=true` 时才出现逐边/逐项明细；无每帧刷屏。
10. 存档→读档后，mod 建立的连接与原生手动连接一样保留。
11. 退出存档再进（GameEnd→GameStart），批量窗口重建正常、无残留高亮；两 HUD 按钮位置记忆生效。
