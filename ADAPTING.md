# 模组适配指南

> 给**用 AI 辅助开发、想把格挡预测接到自己 mod 上**的开发者。
> 你可以只拿走计算逻辑，不必接管我的 UI。

---

## 0. 先读，再动手

按这个顺序读，能省掉大部分返工：

1. 本文（触发条件怎么找）
2. [ARCHITECTURE.md](ARCHITECTURE.md)（数值怎么算、播放次数怎么乘、性能缓存）
3. 你想接入的那个 mod 的代码

**一条铁律：除非你确实要重写显示层，不要动我的 UI。**

理由在[第 8 节](#8-不要乱动-ui)。UI 的隐藏/显示不是简单开关，它同时挂在三类容器上、走两条不同的代码路径，改错的表现是「拖牌时对、进选牌界面就错」这类很难查的问题。

---

## 1. 适配难在哪：六个维度，逐层踩

绝大多数人卡在「**这个遗物/状态到底什么时候触发、算多少**」。实际有六层，一层错数字就不对：

| 维度 | 典型问题 | 例子 |
| --- | --- | --- |
| **时机** | 回合开始前还是开始后？回合结束前还是"极早"？ | 奥里哈刚必须早于覆甲判断 |
| **次数** | 播一次算一次，还是每次播放都算？重放/爆发怎么乘？ | 狂怒每次播放给一层 |
| **倍率** | 吃不吃「融入暗影」的 2^N？吃不吃「不动」的 ×2？ | 遗物吃暗影倍率，但**不**吃不动 |
| **数值修正** | 敏捷 / 力量 / 虚弱 / 集中，哪些影响它？ | 奥斯提血量是 HP，一个都不吃 |
| **顺序** | 和别的效果谁先结算？ | 覆甲先结算就会"挡住"奥里哈刚 |
| **互斥** | 满足 A 就不触发 B 吗？条件是不是自指？ | 奥里哈刚被自己算出来的格挡挡住 |

只看"什么时候触发"很容易漏掉后四层。**倍率和数值修正尤其容易被忘**——它们不改时机，只改数值，错了不会报错，只会默默显示成错的数。

---

## 2. 四步法

### 第 1 步：定位目标 mod 的代码

| 情况 | 怎么找 |
| --- | --- |
| 有开源仓库 | 直接读；找 `RelicModel` / `PowerModel` / `CardModel` 的派生类 |
| 只有 dll | 用 ILSpy / dnSpy 反编译；先搜字符串常量（遗物名、描述文本）再顺着找到类 |
| 用 BaseLib 做的 | 派生自 `CustomRelicModel` / `CustomPower` / `CustomCardModel`，命名通常带 mod 前缀 |

找到类之后，**先看它的 `DynamicVars` 和字段**，那才是真实的数值来源，不要硬编码。

```csharp
// 正确：跟着遗物/状态自己声明的数值走
int block = relic.DynamicVars.Block.IntValue;

// 错误：把 6 写死，将来数值平衡一改就错
int block = 6;
```

### 第 2 步：找出「格挡条件」

按这个顺序问六个问题：

1. **它给谁格挡？** 玩家自己 / 敌人 / 召唤物——只有给玩家（或计入等效护盾的召唤物）才需要接
2. **什么触发？** 打牌、弃牌、消耗、回合开始、回合结束、受击、生成牌……看它 override 了哪个 Hook
3. **给多少？** 一次性 `Amount`，还是乘层数、乘播放次数、乘牌数
4. **吃哪些倍率与修正？** 融入暗影 ×2^N、不动 ×2、敏捷、力量、虚弱、集中——逐项确认（见[第 5 节](#5-倍率与数值修正最容易算错)）
5. **和谁有顺序关系？** 会不会被别的效果抢在前面或挡住
6. **有没有前置条件？** 当前格挡是否为 0、是否本回合第一次、手里有没有某类牌

### 第 3 步：确认「专属词条」

很多 mod 会注册**游戏里原本不存在的**关键词/状态（例如自定义 `CardKeyword` 或自建 `PowerModel`）。

| 现象 | 处理 |
| --- | --- |
| 卡牌文本里出现陌生关键词 | 去目标 mod 的 `CardKeyword` 定义里搜，看它是否改变格挡行为 |
| 状态图标是 mod 自定义的 | 派生自 `PowerModel` / BaseLib 的 `CustomPower`，按 Power 处理即可 |
| 触发逻辑写在别的 mod 的 Hook patch 里 | 它的效果可能已经通过 `Hook.ModifyBlock` 折进卡面，**这时你什么都不用做** |

**保守原则**：不确定它有格挡收益时，选择**不计入**，并在文档里注明。多算会让玩家不信任数字，少算只是少显示一项。

### 第 4 步：实现接入

见[第 7 节](#7-实现模板四处改动)。

---

## 3. 触发时机：Hook 速查

游戏所有效果都挂在 `MegaCrit.Sts2.Core.Hooks.Hook` 上（完整列表见 `Hook.cs`，约 2600 行）。常与格挡相关的：

| Hook | 时机 | 常见用途 |
| --- | --- | --- |
| `BeforeSideTurnStart` / `AfterSideTurnStart` | 某一阵营回合的开始前后 | 「回合开始时」类效果 |
| `AfterPlayerTurnStart` | 玩家回合开始后（更晚） | 要等其他回合开始效果都结算完的 |
| `BeforeBlockGained` / `AfterBlockGained` | 获得格挡前后 | 修改格挡数值、按格挡触发 |
| `BeforeCardPlayed` / `AfterCardPlayed` | 打牌前后 | 「打出攻击牌时」「每次出牌时」 |
| `AfterCardExhausted` | 卡牌被消耗后 | 无惧疼痛这类 |
| `AfterCardDiscarded` | 卡牌被弃置后 | 奇巧、弃牌触发 |
| `AfterCardDrawn` | 抽牌后 | 抽牌触发 |
| `BeforeSideTurnEnd` | 回合结束结算 | 覆甲、冰球被动 |
| `BeforeSideTurnEndVeryEarly` | **更早**的回合结束 | 奥里哈刚（必须在覆甲之前判断） |
| `AfterSideTurnEnd` | 回合结束之后 | 已经结算完的收尾效果 |
| `AfterDamageReceived` | 受到伤害后 | 受击触发 |
| `AfterCardGeneratedForCombat` | 战斗中生成卡牌 | 创世之柱、君王矿石 |

> **「回合开始」也有前后之分。** `BeforeSideTurnStart` 里做的事，玩家进入出牌阶段时已经结算完了；
> 而 `AfterPlayerTurnStart` 更晚，可能再过一次其他效果。判断「这个遗物开局给我多少格挡」时，
> 要看它挂在哪个点——不是笼统的"回合开始"。

> **同名不同时机是最大的坑**。看到「回合结束时触发」别急着写进 `BeforeSideTurnEnd`——先确认它是不是 `VeryEarly`。

---

## 4. 四个最容易写错的条件

### 4.1 狂怒（Rage）

```csharp
// 只有攻击牌才触发，且每次播放都给一层
if (card.Type == CardType.Attack)
    block += ragePower.Amount * totalPlays;
```

坑：忘了乘 `totalPlays`（重放/爆发时偏小）；或者写成「本回合打过攻击牌就给」——那是遗物的逻辑，不是狂怒。

### 4.2 死亡之舞（Danse Macabre）

```csharp
// 判定的是「这张牌的实际能量花费」≥ 能力声明的门槛
if (card.EnergyCost.GetResolved() >= dansePower.DynamicVars.Energy.IntValue)
    block += dansePower.Amount * totalPlays;
```

坑：用卡面印刷费用而不是 `GetResolved()`（X 费牌、减费效果会算错）。

### 4.3 无惧疼痛（Feel No Pain）——分两段

它由两部分组成，**发生时机完全不同**：

| 部分 | 时机 | 怎么算 |
| --- | --- | --- |
| 打出的牌自己就被消耗 | 立即 | `Amount × 1` |
| 手牌里的虚无牌回合末消耗 | 回合结束 | `Amount × 手牌中虚无牌数` |

坑：把两段合并成一个数放进「立即格挡」。奥里哈刚判定「当前格挡为 0」时，**第二段不能算进去**（它还没发生），否则会错误地挡住奥里哈刚。

### 4.4 奥里哈刚（Orichalcum）

```csharp
BeforeSideTurnEndVeryEarly:          // 注意是 VeryEarly
    if (Owner.Creature.Block > 0) return;   // 已有格挡就不触发
    ShouldTrigger = true;

BeforeSideTurnEnd:
    if (ShouldTrigger) GainBlock(BlockVar);
```

游戏源码注释写得很明白：

> This uses the _very early_ hook because it needs to check the player's block
> before `PlatingPower` triggers (otherwise, `PlatingPower` will prevent this relic's block gain)

所以预览里判断「已有格挡」时，只能用**立即发生**的格挡（当前格挡、手牌格挡、拳斗、蜃景、逃脱计划、额外播放……），
**不能用**回合结束才结算的段（覆甲、冰球被动、所向无敌、斗篷扣、波纹水盆）。

代码里用 `immediateBlock` 表示这个集合：

```csharp
int orichalcumBlock = GetOrichalcumLikeBlock<Orichalcum>(owner, immediateBlock, out icon);
// otherBlock > 0 → 返回 0（被挡住了）
// otherBlock == 0 → 返回 BlockVar
```

这样它**不参与自己的判定**，天然避免循环依赖。

---

## 5. 倍率与数值修正（最容易算错）

这一节的内容不改触发时机，只改数值——**所以错了不报错，只是数字默默不对**，最难发现。

### 5.1 两个倍率是独立的，作用范围不同

代码里是两个不同函数，别混作一个：

| | 融入暗影 `ShadowmeldPower` | 不动 `UnmovablePower` |
| --- | --- | --- |
| 倍率 | ×2^层数（`GetBlockGainMultiplier`） | ×2 |
| 作用范围 | **本回合获得的所有格挡**：卡牌、遗物、药水、能力都算 | **只作用于卡牌来源**的格挡，且限本回合前 N 次 |
| 判断依据 | 当前层数 | 历史里本回合 `CardBlockGainedThisTurn` 已用次数 |
| 遗物是否吃 | ✅ | ❌ |
| 药水是否吃 | ✅ | ❌ |

乘算顺序：

```csharp
int blockMultiplier = GetBlockGainMultiplier(creature);   // 2^N（融入暗影）
int unmovable       = GetUnmovableMultiplier(creature);   // 1 或 2（不动）

value = baseValue * blockMultiplier * unmovable;
```

> **接遗物时最容易错**：遗物格挡要乘融入暗影，但**不能**乘不动。
> 接卡牌时反过来，两个都要乘（卡面 `PreviewValue` 已含敏捷和不动，只有手算的部分要自己补）。

### 5.2 敏捷 / 力量 / 虚弱 / 集中，各管一块

| 修正 | 影响 | 不影响 |
| --- | --- | --- |
| **敏捷** Dexterity | 卡牌的 `BlockVar`（`PreviewValue` 已含） | 奥斯提血量、遗物固定值、充能球 |
| **力量** Strength | 「伤害转格挡」类（拳斗） | 普通格挡牌、蜃景（它是按中毒层数算的） |
| **虚弱** Weak | 降低造成的伤害 → **间接**降低伤害转格挡 | 纯格挡牌（敏捷是它的修正，不是虚弱） |
| **集中** Focus | 冰球被动 / 激发的数值 | 除充能球以外的一切 |
| **脆弱** Vulnerable | 提高**受到**的伤害 | 不影响任何格挡 |

### 5.3 特例：奥斯提血量不是格挡

奥斯提的血替玩家挨伤害，面板上显示成等效护盾，但它**是 HP**：

- 不吃敏捷（敏捷只改 Block）
- 不吃融入暗影倍率
- 不吃不动
- 代码里单独存成 `OstyHpBlock`，并另算 `RealBlock = 总额 - OstyHpBlock`

为什么必须分开：固化药水（Fortifier）把**真实格挡**翻三倍，如果把奥斯提血量一起翻，就凭空变出三倍 HP。

> 接任何「按当前格挡放大」的效果时，先问一句：这个数到底是 Block，还是等效护盾？
> 前者用 `RealBlock` 的语义，后者不能进倍率。

### 5.4 顺序：谁先结算决定谁看得见

典型回合结束链条：

```
BeforeSideTurnEndVeryEarly  → 奥里哈刚判断「当前格挡是否 > 0」
BeforeSideTurnEnd           → 覆甲、冰球被动、斗篷扣、波纹水盆陆续结算
```

所以奥里哈刚看到的是**它们结算之前**的格挡。把它排到覆甲之后，它就永远不触发。

推广：任何「条件依赖当前格挡」的效果，都要找出它确切的 Hook 点，不能笼统按"回合结束"处理。

---

## 6. 专属词条：别人 mod 独有的东西

三种情况，处理方式完全不同：

### 5.1 它通过 Hook 修改了格挡数值

大部分情况属于这类。效果已经在游戏计算卡面时折进去了，你读 `PreviewValue` 就自动包含，
**不要重复加**。

判断方法：看目标 mod 是否 patch 了 `Hook.ModifyBlock` / `ModifyDamage`。是的话，你的工作量为零。

### 5.2 它注册了新的 Power / 关键词，但**不产生格挡**

比如自定义的「打出时给力量」「弃牌时抽牌」。这类通常只需要在文档里注明「未接入」，不必计算。

### 5.3 它确实产生格挡，且触发条件特殊

这时才需要写代码。步骤：

1. 找到它的 Power 类型：`creature.GetPower<TheirPower>()`
2. 找到它注册的 Hook 点，确认时机
3. 按[第 7 节](#7-实现模板四处改动)接入
4. 在配置里加开关，默认开启；在文档「覆盖范围」里写清

> **不要假设别的 mod 一定遵守某个约定**。它的「回合结束」可能是 `BeforeSideTurnEnd`，也可能是自己 patch 的回合流程。宁可多读一遍它的 Hook 实现。

---

## 7. 实现模板：四处改动

以「别的 mod 的遗物在打出攻击牌时给格挡」为例：

**① 配置** — `BlockPreviewConfig.cs`

```csharp
public bool ShowTheirRelic { get; set; } = true;
```

**② 计算** — `BlockPreviewDisplay.BuildValues`

```csharp
if (Config.ShowTheirRelic)
{
    int block = GetTheirRelicBlock(creature, card, totalPlays);  // 条件判断写在里面
    if (block > 0) { values.TheirRelicBlock = block; values.TheirRelicIcon = icon; }
}
```

**③ 面板** — `BlockPreviewPanel.cs`

1. `BlockPreviewValues` 加 `int TheirRelicBlock` 与 `Texture2D? TheirRelicIcon`
2. 在渲染序列里插入 `+N [图标]`

**④ 文案** — `ConfigLocalization.cs`

加一行 `BLOCKPREVIEW-{Slugify(属性名)}.title`，中英各一份。

---

## 8. 不要乱动 UI

我的 UI 不是「一个 Label」，它同时挂在三类容器上，并且走两条不同的代码路径。改动前必须理解：

### 7.1 两条路径必须同步

| 路径 | 触发 | 代码 |
| --- | --- | --- |
| 拖牌 | 拖起卡牌 | `RefreshDragPreview` → `BuildValues` |
| 常驻 / 选牌界面 | 没拖牌、或进入净化/弃牌选牌 | `ShowPersistent` → `ShowPersistentCore` |

**这两条路径是独立实现的。** 只在一条里加了新段，表现就是「拖牌时有、进选牌界面没了」——这是本 mod 历史上真实出现过的 bug。

### 7.2 锚点与容器

- 位置锚在玩家血条容器 `%HpBarContainer` 右侧，血条在战斗中有动画位移
- 每个角色有独立的位置偏移（奥斯提站着会挡住面板）
- 缩放 `Scale` 与偏移 `OffsetX/OffsetY` 联动，改一个要验证另一个

### 7.3 允许的最小改动

| 想做的事 | 安全做法 |
| --- | --- |
| 加一个显示段 | 在 `BlockPreviewValues` 加字段 + `Panel` 渲染序列插入，**两条路径都要改** |
| 改颜色/字体 | 只动 `Panel`，不碰 `Display` |
| 换位置逻辑 | 只动 `SyncFromPanel` / 配置项，别改锚点计算 |
| 隐藏整个面板 | 直接 `Config.Enabled = false`，不要改可见性逻辑 |

> **给 AI 的提示**：让它先读 `ARCHITECTURE.md` 第 3 节（运行流程）和第 7 节（性能路径），
> 再让它改 UI。跳过阅读直接改，几乎一定会漏掉 `ShowPersistent` 那条路径。

---

## 9. 验证清单

接完之后，逐项过一遍：

- [ ] 拖牌时数字正确，且**常驻显示**（不拖牌）也包含新段
- [ ] 进入净化 / 弃牌选牌界面，数字也正确
- [ ] 重放牌（爆发 / 复制药水 / 回声形态）的数值按次数放大
- [ ] 有**融入暗影**时该项也跟着翻倍（遗物、药水都要翻）
- [ ] 有**不动**时只有卡牌来源翻倍，遗物 / 药水 / 球**不**翻
- [ ] **奥斯提血量**不被固化药水之类的倍率放大
- [ ] 敌方回合该类段消失（若属回合结束结算）
- [ ] 关闭对应开关后，该段既不显示也不计入总额
- [ ] 长战斗（十几回合）后拖牌仍然流畅
- [ ] 联机时双方 mod 集合与版本一致

---

## 10. 给 AI 的提示词模板

可以直接复制：

```
我在改一个杀戮尖塔 2 的 mod（Block Preview），仓库里已有 ARCHITECTURE.md。
请先完整阅读 ARCHITECTURE.md 的第 3、5、7 节，再动手。

需求：把 <目标 mod 名> 的 <遗物/Power 名> 的格挡收益接入预览。
它的触发条件是：<从它的代码里读到的具体条件与时机>。

约束：
1. 不要修改 BlockPreviewPanel 的现有布局与锚点计算，只允许在渲染序列里追加一段。
2. 两条路径都要改：BuildValues 和 ShowPersistentCore。
3. 数值要乘 totalPlays（重放/额外播放）。
4. 加配置开关，默认 true，并在 ConfigLocalization 补中英文案。
5. 改完列出你改了哪些文件、每处为什么改。
```

---

## 11. 许可

MIT。拿走、改写、商用都可以，保留版权声明即可。改完之后欢迎回一个 issue 告诉我你接了哪些 mod。
