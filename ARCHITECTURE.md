# Block Preview 实现说明

> 面向想复用、改造或适配这套代码的开发者。本项目为 MIT 许可，可以自由拿去改。
> 文档里的行号与字段名以当前 `main` 为准；游戏更新后若 API 变动，请对照本说明定位修改点。

---

## 1. 它做了什么

拖出卡牌时，在玩家血条右侧实时显示：

- 这张牌能获得多少格挡
- 结算后的总格挡（`+X = Y` 形式）

**纯显示**：只读游戏状态，不写任何游戏数据，`affects_gameplay: false`，联机安全。

---

## 2. 代码结构

| 文件 | 行数量级 | 职责 |
| --- | --- | --- |
| `BlockPreviewCode/BlockPreviewDisplay.cs` | ~7100 | **核心**。捕获拖牌事件、计算各项格挡、缓存、性能埋点 |
| `BlockPreviewCode/BlockPreviewPanel.cs` | ~1400 | UI 绘制。`BlockPreviewValues` 结构体定义在这里 |
| `BlockPreviewCode/BlockPreviewConfig.cs` | ~400 | 配置项（BaseLib `ModConfig` 序列化） |
| `BlockPreviewCode/ConfigLocalization.cs` | ~500 | 设置界面中英文案注入（Harmony patch `LocManager.LoadTable`） |
| `BlockPreviewCode/BlockPreviewLog.cs` | ~450 | 三类功能日志 |
| `BlockPreviewCode/MainFile.cs` | ~120 | 入口：初始化、注册设置、挂载 |
| `BlockPreviewCode/Patches.cs` | ~100 | Harmony 补丁（血条挂载、选牌界面等） |

**分工原则**：`Display` 只算数，`Panel` 只画数。两者通过 `BlockPreviewValues`（`readonly record struct`）单向传递。

---

## 3. 运行流程

```
玩家拖起卡牌
   ↓
HoveredModelTracker.OnLocalCardSelected  (Harmony Postfix)
   ↓
BlockPreviewDisplay.Show(card)  → 立即同步算一次 BuildValues  →  Panel 刷新
   ↓
TickAlways()  每帧回调
   ├─ 拖牌中 → RefreshDue() 时间节流（33ms / ~30Hz）→ RefreshDragPreview()
   └─ 常驻   → RefreshPersistentDue() 同样节流 → ShowPersistent()
   ↓
BlockPreviewPanel 渲染：+手牌 +拳斗 +蜃景 +持久段 =总计
```

关键入口：

| 时机 | 方法 |
| --- | --- |
| 拖起 / 放下卡牌 | `Show(card)` / `Hide()` |
| 每帧 | `TickAlways()` |
| 战斗开始、血条创建 | `AttachPlayerHealthBar(NHealthBar, Creature)` |
| 牌堆变化 | `WatchPile` 订阅的 `ContentsChanged` |
| 选牌界面（净化/弃牌） | `BlockPreviewSelectOverlay` + 反射读 `_selectedCards.Count` |

---

## 4. 数值从哪来（三条原则，改动前务必遵守）

### 4.1 优先读卡面的 `PreviewValue`

游戏在 `NCard.UpdateVisuals` 里已经调用 `UpdateDynamicVarPreview` → `Hook.ModifyBlock`，**敏捷修正等已经被算进去了**。

```csharp
// 正确：和卡面数字完全一致
int block = card.DynamicVars.Block.PreviewValue;
int calc  = card.DynamicVars.CalculatedBlock.PreviewValue;
```

手算会导致「预览 7、卡面 8」这种不一致。

### 4.2 状态类读 Power 实例，不读定义

```csharp
// 正确
creature.GetPower<FeelNoPainPower>()?.Amount
// 错误：这是定义，不是当前层数
ModelDb.Power<FeelNoPainPower>().Amount
```

### 4.3 概率与期望必须标明是估算

凡是「按牌堆比例」「按历史统计」推出来的值，一律放在第二行（估算行），数字带 `?` 或 `%`。
**宁可少显示，也不要冒充确定值。**

---

## 5. 计算模型

### 5.1 三行布局

| 行 | 内容 | 说明 |
| --- | --- | --- |
| 第一行 | 确定值 | 只放**一定**会发生的格挡，`=X` 可当准数用 |
| 第二行 | 估算行 | 概率项（`%`）、估算项（`?`）、额外播放 |
| 第三行 | 药水行 | 药水提供的格挡，基数是真实格挡 |

三行各自有总闸开关：`ShowEstimateLine`、`ShowPotion`。关掉某行的总闸后，该行的所有项既不渲染也不计入总额。

### 5.2 播放次数（几乎所有段都要乘它）

```csharp
// 1 次基础 + 附魔重放
int plays = 1 + card.GetEnchantedReplayCount();
// 再汇总爆发 / 复制药水 / 回声形态 / 遗物 / mod power 的一切来源
int total = Hook.ModifyCardPlayCount(...);
int extra = total - replayCount;   // 只有第二次及以后算「额外播放」
```

**错这里会全面偏小**：所有「每播放一次给 N 格挡」的段都要乘 `total`。

### 5.3 各来源速查

| 来源 | 取法要点 |
| --- | --- |
| 手牌格挡 | `BlockVar.PreviewValue`（已含敏捷） |
| 拳斗（伤害转格挡） | `CalculatedBlockVar.PreviewValue`，**不受**「不动」影响但受敏捷影响；手算要手动乘 `GetUnmovableMultiplier` |
| 蜃景 | 遍历存活敌人累加 `PoisonPower.Amount` |
| 逃脱计划 | 满额 = `BlockVar.PreviewValue`；概率 = 抽牌堆技能牌占比（抽牌堆空时用弃牌堆） |
| 覆甲 | `PlatingPower.Amount`；拖覆甲牌时再加 `DynamicVars["PlatingPower"]` |
| 不动 | 本回合前 N 次**卡牌来源**格挡 ×2。卡面已含，只有拳斗/蜃景这类手算要手动乘；遗物/能力/球/药水不吃 |
| 柔嫩 | 每次播放力量/敏捷 -1，用 `SumBlockWithTenderDecay(perPlay, plays, step)` 逐次递减 |
| 无惧疼痛 | `FeelNoPainPower.Amount × 手牌中虚无牌数` |
| 额外播放 | 只补第二次及以后；图标按 `modifyingModels` 里的来源（爆发/复制/回声）选 |
| 死亡之舞 | `EnergyCost.GetResolved() >= power.DynamicVars.Energy` 时加 `power.Amount` |
| 狂怒 | `card.Type == Attack` 时加 `power.Amount` |
| 余像 | 任意牌都加 `power.Amount` |
| 不可格挡 | `NoBlockPower` 存在时不预览任何卡牌格挡 |
| 遗物 / 药水 | 见第 8 节的扩展方式 |

---

## 6. 充能球推演

实现：`BlockPreviewDisplay.SimulateOrbs`。

### 6.1 为什么用队列模拟，而不是数学公式

游戏生成球走 `OrbCmd.Channel`，本身就是「进一个、满了先挤出队首」的循环：

```csharp
if (orbQueue.Orbs.Count >= orbQueue.Capacity)
    await EvokeNext(choiceContext, player);   // 先激发队首
if (await orbQueue.TryEnqueue(orb)) { ... }   // 新球加到末尾
```

事实（来自游戏源码）：

| 事实 | 依据 |
| --- | --- |
| `Orbs[0]` 是队首（最右侧出口） | `EvokeNext` 取 `Orbs.First()` |
| 新球 `Add` 到末尾 | `_orbs.Add(orb)` |
| 满了先激发队首 | `if (Count >= Capacity) EvokeNext()` |
| `Capacity` 动态，上限 10 | `AddCapacity` / `RemoveCapacity` |
| 减少容量时从**末尾**移除 | `RemoveCapacity` 里取 `_orbs.Last()` |

既然游戏就是逐颗处理，**照着模拟天然正确**，不需要推导「新球自我挤压」「混合球偏移」这类特例。

### 6.2 推演流程

每次播放（`totalPlays`）执行一轮：

```
1. 扩容（如有）：Capacity = min(10, Capacity + slotAdd)

2. 逐颗生成：
     若 orbs.Count >= Capacity：
         pushed = orbs[0]; orbs.RemoveAt(0)
         若 pushed 是冰球 → overflowBlock += pushed.EvokeVal
     orbs.Add(新球)

3. 主动激发（这张牌会激发时）：
     Front：激发并移除队首
     All  ：每颗都激发后清空

4. 吞噬暗影（玩家有 ConsumingShadowPower）：
     回合结束会激发队尾 N 颗球（Orbs.Last()，方向与挤出**相反**）
     被激发的冰球计入 evokeBlock，且不再提供被动
```

四个输出：

| 输出 | 含义 | 面板 |
| --- | --- | --- |
| `evokeBlock` | 玩家主动激发（Dualcast / MultiCast 等） | `+N [引雷针]` |
| `overflowBlock` | 生成新球时被挤出的队首冰球（**溢出激发**） | `+N [冰球图标]` |
| `frostBlock` | 推演后仍存活的冰球，回合末被动 | `+N [冰球]` |
| `goldCablesBlock` | 镀金缆线让队首球被动多触发一次 | `+N [缆线图标]` |

> **方向别搞混**：挤出 / 主动激发看 `orbs[0]`（队首），吞噬暗影看 `orbs.Last()`（队尾）。

### 6.3 数值口径

| 项 | 值 | 注意 |
| --- | --- | --- |
| 冰球激发 | `EvokeVal` = **5 + 集中** | 不是 2，STS1 才是 2 |
| 冰球被动 | `PassiveVal` = **2 + 集中** | |
| 集中修正 | `FocusPower.ModifyOrbValue` → `Math.Max(value + Amount, 0)` | 允许负集中，结果 clamp 到 ≥ 0 |

现有球直接读实例的 `frost.EvokeVal` / `frost.PassiveVal`（已含集中）。新生成的球没有实例，按「模板值 + 当前集中」估算，同样 clamp。

### 6.4 生成规格表（`GetOrbChannelSpecs`）

**固定序列**

| 卡牌 | 生成 |
| --- | --- |
| `Zap` / `BallLightning` | 闪电 ×1 |
| `ColdSnap` / `Coolheaded` | 冰霜 ×1 |
| `Darkness` / `Null` / `ShadowShield` | 黑暗 ×1 |
| `Fusion` | 等离子 ×1 |
| `Glasswork` | 玻璃 ×1 |
| `Rainbow` | 闪电 → 冰霜 → 黑暗（**顺序固定**） |
| `Glacier` | 冰霜 ×2（先给 6 格挡） |
| `IceLance` | 冰霜 ×3（`Repeat` 恒为 3，升级不加数量） |
| `MeteorStrike` | 等离子 ×3 |
| `Refract` | 玻璃 ×2 |

**读卡面变量**

| 卡牌 | 生成 | 变量 |
| --- | --- | --- |
| `ConsumingShadow` | 黑暗 × `Repeat` | 基础 2，升级 3 |
| `Hibernate` | 冰霜 × `Repeat` | 基础 2，升级 3 |

**依赖局面**

| 卡牌 | 生成 | 依据 |
| --- | --- | --- |
| `Chill` | 冰霜 × 可命中敌人数 | `CombatState.HittableEnemies` |
| `Tempest` | 闪电 × 当前能量 | X 费，升级额外 +1 |
| `Spinner` | 玻璃 ×1 | **仅升级版** |
| `Voltaic` | 闪电 × 本场已生成闪电球数 | 读战斗历史，见 6.5 |
| `TheMagician` | 随机 × `Repeat` | 类型不可知，见 6.6 |

**只扩容、不生成**

| 卡牌 | 扩容 |
| --- | --- |
| `Capacitor` | `Repeat`（基础 2，升级 3） |
| `Modded` | 1 |
| `TheMagician` | `Repeat`（基础 3，升级 4）**并**生成同数量随机球 |

`OrbCmd.AddSlots` 内部有 `Math.Min(10 - Capacity, amount)` 裁剪，模拟里同样按 10 格上限处理。

> **扩容先于生成**：`TheMagician` 先 `AddSlots` 再 `Channel`，到 `Channel` 时已有槽位，
> 不会触发「没有球槽时隐式 `AddSlots(1)`」的兜底。模拟里用 `slotAdd <= 0` 作为补 1 的前提。

### 6.5 Voltaic：读历史是准数

```csharp
CombatManager.Instance.History.Entries
    .OfType<OrbChanneledEntry>()
    .Count(e => e.Actor.Player == card.Owner && e.Orb is LightningOrb)
```

战斗历史实时可读，所以能算准。**但重放要逐轮递增**：每生成一颗闪电球都会立刻写入 `OrbChanneledEntry`，
第二轮就是 `base × 2`。预览不能写游戏数据，`SimulateOrbs` 里用 `voltaicChannelled` 自己累计。

### 6.6 TheMagician：随机类型怎么办

候选池固定 5 种（闪电/冰霜/黑暗/等离子/玻璃），等概率。

| 影响 | 是否计入 | 原因 |
| --- | --- | --- |
| 扩容 | ✅ | 确定效果 |
| 新球把队首冰球挤出去 | ✅ | 挤掉的是队列里真实存在的球，收益确定 |
| 随机球自己是冰球时的被动/激发 | ❌ 记 0 | 5 选 1 的运气，不预测 |

所以这张牌显示的是**下限**，不是期望值。

### 6.7 为什么溢出单独成段

主动激发是玩家明确选择的收益；溢出是副作用——玩家可能只想铺球，没意识到槽位满了会烧掉一颗冰球。

分开才能分辨：`+10 [引雷针]` 是我主动打的，`+5 [❄]` 是满槽顶出去的。两者在总额里同等计入。

---

## 7. 性能：遍历与缓存

`BuildValues` 由 50 多个 `GetXxx` 组成，早期版本有两个性能问题：

| 问题 | 后果 |
| --- | --- |
| 战斗历史被反复全扫 | 每次刷新 6 次 × O(历史条目)，**战斗越久越卡** |
| 牌堆被反复全扫 | 所向无敌扫 2 次、逃脱计划扫 1 次、重振精神扫 2 次 |
| 刷新按帧节流 | 游戏帧率上限 500FPS，**帧率越高反而越卡** |

### 7.1 历史统计：6 次全扫合并成 1 次

把 `BuildValues` 需要的 7 项历史统计，合并成**一次遍历**的结果并按玩家缓存：

```csharp
private sealed class HistorySnapshot
{
    public int AttackPlaysThisTurn;      // 精致折扇
    public bool AttackPlayedThisTurn;    // 波纹水盆
    public bool PowerPlayedEver;         // 永冻冰晶
    public int CardBlockGainedThisTurn;  // 不动
    public bool DoomAppliedThisTurn;     // 灾厄之衣
    public bool CardGeneratedThisTurn;   // 君王矿石
    public int LightningChannelsEver;    // Voltaic
}
```

一次 `foreach (entry in Entries)` 用 `switch (entry)` 同时处理
`CardPlayFinishedEntry` / `BlockGainedEntry` / `PowerReceivedEntry` / `CardGeneratedEntry` / `OrbChanneledEntry`。

**关键设计：保留 fallback。** 拿不到历史时 `GetHistorySnapshot` 返回 `null`，7 处调用点立刻退回原来的直接 LINQ 查询，不会比优化前更差：

```csharp
var snap = GetHistorySnapshot(combatState, player);
if (snap != null) return snap.AttackPlaysThisTurn;
return CombatManager.Instance.History.CardPlaysFinished.Count(...);
```

**缓存失效判据（踩过坑，别简化）**

只看「历史条目数 + 轮次 + 阵营」是不够的。`CombatHistoryEntry.HappenedThisTurn` 还依赖**每个玩家各自的** `PlayerCombatState.TurnNumber`——多人模式下别人过回合时，轮次和阵营都可能不变，缓存会命中却返回旧值。

```csharp
private static int TurnSignature(ICombatState state)
{
    int sig = 17;
    try
    {
        foreach (var p in state.Players)
            sig = sig * 31 + (p.PlayerCombatState?.TurnNumber ?? -1);
        return sig;
    }
    catch
    {
        // 读不到就给新值，让缓存永远失效 —— 宁可慢，不能显示错数字
        return ++_turnSigFallback;
    }
}
```

完整失效条件：

```csharp
if (version != _histSnapVersion
    || state.RoundNumber != _histSnapRound
    || !Equals(side, _histSnapSide)
    || turnSig != _histSnapTurnSig)
{
    _histSnaps.Clear();   // 重建
}
```

> **设计原则**：所有「不确定」都往「宁可多算一次」的方向倒。缓存只是加速，正确性优先。

### 7.2 牌堆统计：一次遍历算四项

```csharp
private sealed class PileStats
{
    public int Total;            // 总张数
    public int Skills;           // 技能牌数（逃脱计划）
    public int NonAttacks;       // 非攻击牌数（重振精神重放）
    public int Invincibles;      // 所向无敌数量
    public int InvincibleBlock;  // 它的格挡值
}
```

**失效判据：双保险，缺一不可**

```csharp
if (_pileStats.TryGetValue(pile, out var cached) && cached.Total == pile.Cards.Count)
    return cached;   // 张数没变 → 用缓存
// 否则重算
```

| 判据 | 覆盖什么 | 只用另一条的后果 |
| --- | --- | --- |
| 张数比对（O(1)） | 抽牌 / 加牌 / 洗牌 | 事件不触发时缓存永远停在旧值 |
| `ContentsChanged` 事件 | 张数不变但内容变了 | 显示可能滞后一帧 |

> ⚠️ **不能只靠事件**：游戏里有些改动走 `silent` 路径（`CardPile.AddInternal(..., silent: true)`），**不触发** `ContentsChanged`。只订阅事件会让缓存永久失效不了。

**生命周期**：首次访问某个堆时订阅（`WatchPile`）；换战斗时（`AttachPlayerHealthBar` → `ResetPileStats`）退订所有旧堆并清缓存——不退订会持有已废弃的 `CardPile` 并造成泄漏。

### 7.3 手牌遍历：现状与可选改进

`BuildValues` 里有约 10 处 `PileType.Hand.GetPile(player).Cards` 遍历（手牌格挡、无惧疼痛、奇巧、覆甲等）。

**现状：没有合并，也不需要合并。** 手牌通常 ≤10 张，`O(10) × 10` 的开销远小于历史扫描，不是瓶颈。

另外 `CardPile.Cards => _cards` 直接返回内部列表，不产生分配，本身不是热点。

如果确实还要优化，做法是合并成一次遍历：

```csharp
// 可选：把下面这些一次性统计出来，传给各 GetXxx
int handCount, etherealCount, nonAttackCount, skillCount, slyCount;
foreach (var c in hand.Cards) { /* switch on keywords */ }
```

### 7.4 刷新节流：按时间，不要按帧数

```csharp
private const double RefreshIntervalMs = 33.0;   // ≈30Hz
private static readonly long MinRefreshTicks =
    (long)(RefreshIntervalMs * (Stopwatch.Frequency / 1000.0));

private static bool RefreshDue()
{
    long now = Stopwatch.GetTimestamp();
    if (_lastRefreshTicks == 0) { _lastRefreshTicks = now; return true; }
    if (now - _lastRefreshTicks < MinRefreshTicks) return false;
    _lastRefreshTicks = now;
    return true;
}
```

| 帧率 | 每 2 帧的间隔（旧方案） | 实际刷新率 |
| --- | --- | --- |
| 30 FPS | 66ms | 15Hz |
| 60 FPS | 33ms | 30Hz（当初的假设） |
| 144 FPS | 14ms | 72Hz |
| 500 FPS | 4ms | **250Hz，比不节流还密 8 倍** |

**不要再改回按帧节流。** 调参直接改 `RefreshIntervalMs`：`16` ≈ 60Hz（更跟手）、`100` ≈ 10Hz（更省）。

`Show()` 里仍会立刻算一次，算完把 `_lastRefreshTicks` 推到当前时刻，避免选中瞬间算两次。

### 7.5 性能日志怎么读

开关：`BlockPreviewDisplay.PerfLogEnabled`（用 `static readonly` 而非 `const`，否则 `if (!x) return;` 会被折叠成死代码并报 CS0162）。

每 2 秒汇总一行：

```
[BlockPreview] 性能(2001ms): refresh 61次(30.5Hz) 均0.85ms 峰4.21ms | show 1次 均3.90ms 峰3.90ms | 快照 命中58/重建4(94%) 重建均0.30ms 峰1.10ms | 节流跳过59次 | 牌堆重算3次 | 历史412条
```

| 字段 | 看什么 |
| --- | --- |
| `refresh …(30.5Hz)` | 应稳定在 ~30Hz，**与 FPS 无关**；跑成 60/120 说明节流失效 |
| `show` | 选中卡牌那一帧的耗时，含一次完整 refresh；它大说明首帧卡 |
| `快照 命中/重建` | 命中率应很高；重建频繁说明失效判据太敏感 |
| `牌堆重算` | 应远小于 `refresh` 次数，只在抽牌/弃牌/洗牌时涨 |
| `历史 N 条` | 成本随它线性增长，长战斗看它是否失控 |

---

## 8. 扩展指南：新增一个格挡来源

以「某遗物给格挡」为例，四处改动：

**① 配置开关** — `BlockPreviewConfig.cs`

```csharp
public bool ShowMyRelic { get; set; } = true;
```

**② 计算** — `BlockPreviewDisplay.BuildValues` 里加一段

```csharp
if (Config.ShowMyRelic)
{
    int block = GetMyRelicBlock(creature);      // 读遗物实例，不读 ModelDb 定义
    if (block > 0) values.MyRelicBlock = block; // 或累加进 HandBlock/持久段
}
```

**③ 显示段** — `BlockPreviewPanel.cs`

1. `BlockPreviewValues` 加字段（`int` + 可选 `Texture2D? Icon`）
2. 渲染顺序里插入 `+N [图标]`
3. 若属于「回合结束才结算」的类型，记得在 `ShowPersistent` 里用 `IsPlayerTurn()` 判断——非玩家回合这些已经结算过，再叠一次会重复计算

**④ 中英文案** — `ConfigLocalization.cs`

往 `Entries` 加一行，格式 `BLOCKPREVIEW-{Slugify(属性名)}.title`，中英各一份。

**排查**：新增段不显示时，按顺序查——配置默认值 → `BuildValues` 是否被调用 → 值是否 > 0 → `Panel` 是否渲染 → 图标是否为 null。

---

## 9. 已知限制

- 只统计 `Block` / `CalculatedBlock` 两个主变量，**不含**下回合格挡 `BlockNextTurn`
- 概率项按当前牌堆比例静态估算，不模拟「抽走一张后比例变化」
- 重放与额外播放同时存在时，按「先重放、再补额外次数」近似，不模拟交错
- **创世之柱**不按手牌上限封顶（手牌满时游戏会把新牌放进弃牌堆，生成效果照常触发），但同回合多次生成只按当前手牌数静态估算
- **灾厄之衣**忽略敌人身上的「人工制品」（会挡掉灾厄，实际触发可能更少）
- **重振精神**重放轮按「非攻击牌均匀分布」估算，不按抽牌堆真实顺序
- **TheMagician** 的随机球只计溢出收益，不计自身格挡（见 6.6）
- 位置以血条容器 `%HpBarContainer` 为锚点，血条战斗中位移时可能差几像素，用 `OffsetX/OffsetY` 微调

---

## 10. 构建

需要：

- **.NET 9 SDK**（目标框架 `net9.0`）
- **MegaDot 4.5.1**（Godot 分支，版本必须与游戏一致，否则 `.pck` 不被加载）
- **BaseLib**（创意工坊订阅即可，引用由 `Sts2PathDiscovery.props` 自动发现）

```powershell
Copy-Item Directory.Build.props.example Directory.Build.props   # 改 GodotPath
dotnet build -c Release
```

产物在 `bin\Release\net9.0\`，包含 `.dll` / `.pck` / `.json`。

> **不要把游戏自带 dll 复制进输出目录**（`sts2.dll`、`0Harmony.dll`），会与游戏冲突。

---

## 11. 许可

MIT，见 [LICENSE](LICENSE)。保留版权声明即可。

依赖的 [BaseLib](https://github.com/Alchyr/BaseLib-STS2) 为 MIT；RitsuLib 遵循其原许可。
