# 自动派工（flotsam.autocrew）设计与实现计划

> 2026-09-21。目标：按「选中物品数 ÷ 单人单次载重」自动决定浮标 / 地标收集的派遣小人数，取代手动加减。
> 代码：`ModKit/src/Flotsam.ModKit.Game/GameCrew.cs`（算法+原生写入）、`ModKit/src/mods/FlotsamMod.AutoCrew/AutoCrewMod.cs`（开关/心跳/反馈）。

## 1. 逆向结论（decompile 行号）

- **载重单位是「物品件数」不是重量**：`ReturnCanHaulItem` 132587 → `ReturnItemHaulingCapacity` 132611：
  游泳 = `Agent.Inventory.ReturnStorageCapacity()`；有船 = `Boat.Buildable.Inventory.ReturnStorageCapacity()`。
  `SubInventory` 98411 的 `Capacity/Count` 按件计（`CanAddCountedItemProperties` 98505）。
- **船模式每人一条船**：`ReserveBoat` 任务 133923（`BoatType` 公共字段；船长 skip）→ 派 N 人需要 N 条船，
  故船模式人数必须被 `Community.ReturnBoatCount(type)` 8482 限制；地标船模式上限原生就是系泊点数
  （`LandmarkAction.AssignmentLimitMaximum` 99090：UseBoat 时 = `MooringPointCount`）。
- **浮标三型**：类只有 `SwimmingMarker` 48176 / `BoatMarker` 46478；打捞船 vs 钓鱼船靠项目任务队列里的
  `ReserveBoat.BoatType` 区分（`ProjectProperties.TaskQueue` 133350，`TaskList.List` 153797）。
- **浮标选中物品**：`Marker.SalvageableItemsInRadius` 47324（已过 `ItemFilter` 开关过滤，47528-47534）。
  写入：`Marker.SetAgentAmount(n)` 47481 → `Project.AssignmentLimit` + 光标默认值。
- **地标收集选中物品**：`ActionsBehaviour`（`LandmarkSpawner.LandmarkBehaviour` 139345）→
  `LandmarkActionSalvage.Categories` 100598；类别开关 = `Category.MarkedForSalvage` 100352
  （`IsToggled && CanBeSalvaged`，原生搬运列表 100834 就按它过滤）；类别内物品开关 = `Category.ItemFilter`
  100366（`IsItemFilterToggled` 100513）。计数走 `Category.CountItems(auditor)` 100393 + 自建
  `InventoryAuditor`（不碰 `InventoryAuditor.Global`），取 `CountedItem.ReturnCount(All)`（与原生 UI 显示口径一致）。
  写入：`ActionsBehaviour.SetAssignmentLimit(n)` 101374（逐 action 钳制，与原生面板同入口）。
- **人数上下限**：浮标原生面板 1..5（`MarkerPanel._assignLimitMaximum` 75864）；地标 =
  `ActionsBehaviour.AssignmentLimitMinimum/Maximum` 101176/101189。

## 2. 算法

`need = clamp(ceil(selectedItems / capacity), min, max)`；船模式再 `max = min(max, 船数)`。
`capacity <= 0`（无小人/无该型船）→ 跳过该目标并计数（不瞎写）。`selectedItems = 0` → 落到 min。
仅当 `need != 当前 AssignmentLimit` 才写（稳态零写入、零游戏事件）。

## 3. mod 行为（仿 autopriority 模式）

- HUD 两钮 + 键位：**Ctrl+5** 浮标自动派工、**Ctrl+6** 收集自动派工；标签「浮标派工:自动/手动」「收集派工:自动/手动」；
  图标延迟到进存档首 tick（`Icon_Map_SalvageMarker_1` / `UI_Icon_LandmarkAction_Salvage_64x64`）。
- 触发：自动模式 heartbeat `intervalSec`(默认2s，门禁 IsPlaying && !IsMapOpen && !IsPaused)
  + 事件 `MarkerPlaced/MarkerManuallyRemoved`（浮标侧）、`LandmarkNotificationUpdate`（收集侧），0.5s 防抖合并，
  统一推迟到 OnTick 执行；开开关立即手动跑一次并 Toast 汇总。
- 反馈：手动跑必 Toast；有变化或 verbose 才记一行 `run(auto|manual): markers[…] landmarks[…]`；
  verbose 逐目标 `marker swim: items=12 cap=2 -> 5 (was 3)`。
- 配置：`markerActive`(true)、`landmarkActive`(true)、`intervalSec`(2)、`markerMax`(5)、`verbose`(false)、
  `schema`(1)、`button.*`/`landbutton.*`（按钮位置记忆）。
- 关开关 = 交还手动控制，不回滚已写人数；写回走游戏原生入口，随存档保存。

## 4. 验证清单（进存档）

1. 日志 `discovered 9 mod(s)`、autocrew `loaded` + `ready`；HUD 两钮出图标。
2. 丢游泳浮标（若干件垃圾）→ 人数自动 = ceil(件数/背包格)；改物品开关 → 2s 内跟随。
3. 打捞船/钓鱼船浮标 → 按对应船载重算；只有 1 条船时人数 ≤1。
4. 地标收集面板：只开部分类别开关 → 人数只按开启类别件数算；游泳/船切换 → 载重口径切换。
5. 关自动 → 手动加减不再被覆盖；verbose 开 → 逐目标明细行。
