# 格挡预览 Block Preview

杀戮尖塔 2 的**纯显示**类 mod（QoL）：拖出卡牌时，在玩家血条右侧实时算出这张牌能给你多少格挡、结算后总格挡是多少。

- 不改动任何游戏数值，`affects_gameplay: false`，联机安全
- 依赖 [BaseLib](https://github.com/Alchyr/BaseLib-STS2)（≥ 3.4.8）
- 支持中文 / 英文界面（跟随游戏语言，其他语言回退英文）

## 效果

```
只有手牌：             +8 [🛡] =23 [🛡]
手牌 + 覆甲：          +8 [🛡] +4 [覆甲] =35 [🛡]
手牌 + 覆甲 + 冰球：   +8 [🛡] +4 [覆甲] +6 [❄] =41 [🛡]
有死亡之舞的 2 费牌：  +12 [🛡] +4 [💃] =16 [🛡]
```

最多三行：**第一行只放确定值**（`=X` 可以当准数用），第二行是不确定项
（概率 `%`、估算 `?`），第三行是药水。带 `?` 的数字表示需要你临场决定，带 `%` 的是按牌堆比例估的概率。

覆盖的机制包括：手牌格挡、伤害转化格挡、中毒总量、覆甲、冰球与激发、溢出激发、
死亡之舞 / 狂怒 / 余像 / 无惧疼痛 / 精神之灰 / 紧缚、重放与额外播放、元素箭与旋律、
创世之柱、群星之子、奥斯提三项、灾厄之衣、所向无敌，以及 **15 个遗物**的格挡收益。

## 安装

### 创意工坊（推荐）

在 Steam 创意工坊订阅本 mod，同时订阅 **BaseLib**。

- 工坊页面：https://steamcommunity.com/sharedfiles/filedetails/?id=3842923799

### 手动安装

1. 从 [Releases](/releases) 下载最新 zip
2. 解压到 `...\steamapps\common\Slay the Spire 2\mods\BlockPreview\`
   （文件夹名**必须**是 `BlockPreview`，与 `BlockPreview.json` 里的 `id` 一致）
3. 确认 `BlockPreview.dll` / `.pck` / `.json` 三个文件在同一层，不要套子文件夹
4. 重启游戏（mod 只在启动时扫描）

## 使用

主菜单 → **Mod 设置** → **格挡预览**。默认就是常驻显示，位置和缩放都调好了；
想挪位置就打开**调整模式**直接拖。

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | 开 | 总开关 |
| `AlwaysVisible` | 开 | 战斗界面常驻显示 |
| `AdjustMode` | 关 | 开启后可拖动定位 |
| `OffsetX` / `OffsetY` | 88 / -146 | 相对血条的偏移 |
| `Scale` | 1.2 | 整体缩放（0.5 ~ 3.0） |
| `ShowEstimateLine` | 开 | 第二行（不确定项）总闸 |
| `ShowPotion` | 开 | 第三行（药水）总闸 |
| `ShowOverflow` | 开 | 溢出激发的格挡 |
| `EnableLogs` | 开 | 功能日志（崩溃日志不受它控制） |

每个显示段、每个遗物都有独立开关；每个角色也能单独设位置（主要给亡灵契约师用，
奥斯提站在同一画面会被面板挡住）。每个显示段和每个遗物都有独立开关。

配置文件：`%APPDATA%\SlayTheSpire2\mod_configs\BlockPreview.cfg`

## 日志

排查问题时看 `%APPDATA%\SlayTheSpire2\BlockPreviewLogs\`（与游戏自己的 `godot.log` 分开）。

## 已知限制

- 只统计 `Block` / `CalculatedBlock` 两个主变量，不含下回合格挡 `BlockNextTurn`
- 概率项（逃脱计划等）按**当前牌堆比例**静态估算，不模拟逐张抽走后的比例变化
- 重放与额外播放同时存在时，按"先重放、再补额外次数"近似，不模拟两者交错
- 合股线、重振精神等按分布估算，不按抽牌堆真实顺序推演
- 概率、估算、重放类项目的具体口径见工坊页面更新日志

## 从源码构建

需要：**.NET 9 SDK**、**MegaDot 4.5.1**（Godot 分支，版本必须与游戏一致，否则 pck 不被加载）、
以及创意工坊订阅的 **BaseLib**（引用由 `Sts2PathDiscovery.props` 自动发现）。

```powershell
# 1. 配置本机路径
Copy-Item Directory.Build.props.example Directory.Build.props
#    编辑其中的 GodotPath 指向你本机的 MegaDot 可执行文件

# 2. 构建（产物自动拷到游戏 mods 目录）
cd ..
.\build.ps1 -Project BlockPreview
```

游戏路径找不到时，在 `Directory.Build.props` 里手动指定 `<Sts2Path>`。

## 二次开发

想复用这套代码、改造或适配新版本，先看 **[ARCHITECTURE.md](ARCHITECTURE.md)**，里面有：

- 代码结构与运行流程
- 数值来源的三条原则（为什么优先读 `PreviewValue`）
- 充能球队列推演的完整流程、数值口径（冰球激发是 **5 + 集中**，不是 2）与 20 多张牌的生成规格
- 性能优化：历史统计快照、牌堆统计双保险缓存、时间节流（为什么不能按帧数）
- 新增一个格挡来源要改哪四处

只想把格挡预测接进**你自己的 mod**？看 **[模组适配指南.md](模组适配指南.md)**——重点讲触发条件怎么找（狂怒、死亡之舞、奥里哈刚这些最容易写错的地方），以及为什么不要动 UI。

## 兼容性

- 游戏版本 `min_game_version: 0.113.0`，Early Access 期游戏更新可能导致失效，会尽量跟进
- 与其他 UI 类 mod 无冲突；联机时所有玩家需使用相同 mod 集合与版本

## 许可

[MIT](LICENSE)，Copyright (c) 2026 Rippleleaf。

依赖的 BaseLib 与 RitsuLib 版权归各自作者，遵循其原许可证。
