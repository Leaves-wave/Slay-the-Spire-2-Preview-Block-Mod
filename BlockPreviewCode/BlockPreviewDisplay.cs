using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
#if STS2_FOOL
using MegaCrit.Sts2.Core.Entities.FoolArrows;
#endif
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.ValueProps;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 负责在玩家血条旁边创建/更新/隐藏预览条。
///
/// 显示的语义是：
///   HandBlock + PlatingBlock + FrostBlock + EvokeBlock + DanseBlock = TotalBlock
/// 即 手牌格挡 + 覆甲格挡 + 冰球被动 + 冰球激发 + 死亡之舞 = 总护盾
/// </summary>
public static class BlockPreviewDisplay
{
    /// <summary>战斗中的手牌上限。抽牌超过这个数也只能装满为止。</summary>
    private const int MaxHandSize = 10;

    private static NHealthBar? _playerBar;
    private static Creature? _playerCreature;
    private static BlockPreviewPanel? _panel;

    /// <summary>正在被拖拽的卡牌。非空即表示处于"拖牌中"。</summary>
    private static CardModel? _draggedCard;

    /// <summary>
    /// 净化这类"打出后还要选牌"的牌：牌打出去后预览会收起，
    /// 但选牌界面还开着，这时用它保持显示收益，选完再清掉。
    /// </summary>
    private static CardModel? _pendingSelectCard;

    /// <summary>当前打开的选牌界面，用来读取玩家已选了几张。</summary>
    private static System.Reflection.FieldInfo? _selectedCardsField;

    /// <summary>选牌界面的选择配置（CardSelectorPrefs），用来区分"弃牌"还是"消耗"等。</summary>
    private static System.Reflection.FieldInfo? _selectPrefsField;

    private static int _loggedSelectCount = -1;

    /// <summary>
    /// 玩家在当前选牌界面里已经选了几张。
    /// 从手牌选牌走的是 NPlayerHand 的"选择模式"，选中集合在它的私有字段 _selectedCards 里。
    /// </summary>
    private static int GetSelectedCardCount()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return 0;
        }

        if (_selectedCardsField == null)
        {
            for (var t = typeof(NPlayerHand); t != null && t != typeof(object); t = t.BaseType)
            {
                _selectedCardsField = t.GetField(
                    "_selectedCards",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (_selectedCardsField != null)
                {
                    break;
                }
            }
        }

        return _selectedCardsField?.GetValue(hand) is System.Collections.ICollection collection
            ? collection.Count
            : 0;
    }

    /// <summary>
    /// 手牌是否正处在选牌模式。
    /// 注意：选完牌后 NPlayerHand 会把 CurrentMode 设成 Mode.Play（正常游玩），
    /// 而不是 Mode.None，所以必须精确判断选牌模式的两个值。
    /// </summary>
    private static bool IsSelectionActive()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return false;
        }

        return hand.CurrentMode == NPlayerHand.Mode.SimpleSelect
               || hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect;
    }

    /// <summary>
    /// 读选牌界面的 CardSelectorPrefs（NPlayerHand 的私有字段 _prefs）。
    /// 用来判断当前是"弃牌选择"还是别的选择。
    /// </summary>
    private static CardSelectorPrefs? GetSelectPrefs()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return null;
        }

        if (_selectPrefsField == null)
        {
            for (var t = typeof(NPlayerHand); t != null && t != typeof(object); t = t.BaseType)
            {
                _selectPrefsField = t.GetField(
                    "_prefs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (_selectPrefsField != null)
                {
                    break;
                }
            }
        }

        return _selectPrefsField?.GetValue(hand) is CardSelectorPrefs prefs ? prefs : null;
    }

    /// <summary>
    /// 当前是不是"弃牌选择"（CardSelectCmd.FromHandForDiscard）。
    /// 只有这种选择下，被弃置的奇巧牌才会自动打出来。
    /// </summary>
    private static bool IsDiscardSelection()
    {
        var prefs = GetSelectPrefs();

        if (prefs == null)
        {
            return false;
        }

        var prompt = prefs.Value.Prompt;

        if (prompt != null
            && prompt.LocTable == "card_selection"
            && prompt.LocEntryKey == "TO_DISCARD")
        {
            return true;
        }

        // 赌徒药水这类用自定义 prompt 的弃牌选择：FromHandForDiscard 会给 prefs 设置 ShouldGlowGold，
        // 而普通的 FromHand 选择不会，所以拿它兜底识别"弃牌选择"。
        return prefs.Value.ShouldGlowGold != null;
    }

    /// <summary>
    /// 当前是不是「选择牌来消耗」的界面。有两种来源：
    ///   卡牌（燃烧契约 / 坚毅 / 烙印…）→ `card_selection/TO_EXHAUST`
    ///   药水（灰水 Ashwater）         → `potions/&lt;药水&gt;.selectionScreenPrompt`
    ///     它用 `CardSelectorPrefs(自己的 prompt, 0, 999999999)`，
    ///     所以不能只看 `TO_EXHAUST`，否则灰水的界面识别不到。
    /// </summary>
    private static bool IsExhaustSelection()
    {
        var prefs = GetSelectPrefs();

        if (prefs == null)
        {
            return false;
        }

        var prompt = prefs.Value.Prompt;

        if (prompt == null)
        {
            return false;
        }

        if (prompt.LocTable == "card_selection" && prompt.LocEntryKey == "TO_EXHAUST")
        {
            return true;
        }

        return prompt.LocTable == "potions";
    }

    /// <summary>
    /// 弃牌选择界面里，当前选中的**所有**奇巧技能牌合计的格挡。
    /// 每张被弃置的奇巧牌都会被自动打出，所以多选时要全部累加——不能只取最后一张。
    /// </summary>
    private static int GetSelectedSlyBlock()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return 0;
        }

        EnsureSelectedCardsField(hand);

        if (_selectedCardsField?.GetValue(hand) is not System.Collections.IEnumerable items)
        {
            return 0;
        }

        int total = 0;
        int priorPlays = 0;

        foreach (var item in items)
        {
            if (item is CardModel card && card.Type == CardType.Skill && card.IsSlyThisTurn)
            {
                // 逐张累加时把"前面已经播放过几次"传进去：
                // 有柔嫩时每播一次敏捷 -1，后面的牌格挡会跟着掉。
                total += GetSlyAutoPlayBlock(card, priorPlays, out int plays);
                priorPlays += plays;
            }
        }

        return total;
    }

    private static void EnsureSelectedCardsField(NPlayerHand hand)
    {
        if (_selectedCardsField != null)
        {
            return;
        }

        for (var t = typeof(NPlayerHand); t != null && t != typeof(object); t = t.BaseType)
        {
            _selectedCardsField = t.GetField(
                "_selectedCards",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (_selectedCardsField != null)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 消耗选择界面里，选中的牌被烧掉会触发**几次**无惧疼痛（不管是不是虚无牌）。
    ///
    /// 只用于奥里哈刚的判断：烧牌是**立即**发生的，
    /// 所以哪怕烧的是虚无牌（它对"无惧疼痛总数"是净增 0），
    /// 格挡也已经提前到手了 → 回合结束时 Block > 0 → 奥里哈刚不该触发。
    /// 显示用的数值走 <see cref="GetSelectedExhaustExtraBlock"/>（虚无牌不重复计）。
    /// </summary>
    private static int GetSelectedExhaustTriggerBlock()
    {
        var creature = _playerCreature;
        var power = creature?.GetPower<FeelNoPainPower>();

        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        int selected = GetSelectedCardCount();

        if (selected <= 0)
        {
            return 0;
        }

        return selected * power.Amount * GetBlockGainMultiplier(creature);
    }

    /// <summary>
    /// 奇巧（Sly）牌被弃置后会被自动打出（`CardCmd.DiscardAndDraw` 里 AutoPlay）。
    /// 这里按**逐次播放**累加它给的格挡，并把「柔嫩」造成的敏捷衰减算进去。
    ///
    /// 柔嫩（`TenderPower`）在 `AfterCardPlayed` 里让玩家力量/敏捷各 -1，
    /// 所以同一批奇巧牌里：第 1 次播放用当前敏捷、第 2 次少 1 点、第 3 次再少 1 点……
    /// 重放 / 华彩的**每次播放**同样会触发（它挂的是 `AfterCardPlayed`，不是"每张牌"）。
    ///
    /// 只要每次播放后格挡都不为负，递减总和与打出顺序无关（等差数列求和）；
    /// 真跌到 0 以下被截断时顺序会有影响，这里按 `_selectedCards` 的遍历顺序近似。
    /// </summary>
    /// <param name="priorPlays">本批里在它之前已经播放过的次数（决定它从第几次开始衰减）。</param>
    /// <param name="plays">它自己一共会播放几次（含重放 / 爆发），供调用方累加。</param>
    private static int GetSlyAutoPlayBlock(CardModel card, int priorPlays, out int plays)
    {
        int block = ComputeHandBlock(card, out int damageBlock);
        int single = block + damageBlock;

        plays = Math.Max(1, GetPlayCount(card, _playerCreature));

        if (single <= 0)
        {
            return 0;
        }

        int step = GetTenderDexLossStep(card);

        if (step <= 0)
        {
            return single * plays;
        }

        int total = 0;

        for (int i = 0; i < plays; i++)
        {
            total += Math.Max(0, single - (priorPlays + i) * step);
        }

        return total;
    }

    /// <summary>
    /// 柔嫩（`TenderPower`）：本回合每打出一次牌，力量/敏捷各 -1。
    /// 对"卡牌来源格挡"来说，敏捷 -1 就是格挡 -1，再乘上所有格挡倍率
    /// （融入暗影 / 佩尔的士兵 / 不动）。
    ///
    /// 返回 0 表示当前没有柔嫩，调用方按"每张牌格挡不变"处理。
    /// </summary>
    private static int GetTenderDexLossStep(CardModel card, Creature? creature = null)
    {
        creature ??= _playerCreature;

        if (creature == null || !creature.HasPower<TenderPower>())
        {
            return 0;
        }

        int step = GetBlockGainMultiplier(creature)
                   * GetPaelsLegionMultiplier(card.Owner)
                   * GetUnmovableMultiplier(creature);

        return Math.Max(1, step);
    }

    /// <summary>
    /// 一张牌播放 `plays` 次、每次"从格挡面值开始逐次减 `step`"时的总格挡。
    ///
    /// 没有柔嫩时 `step == 0`，退化为原来的 `perPlay * plays`（行为完全不变）。
    /// 柔嫩时第 i 次播放（0 起）= `perPlay − i × step`，跌到 0 以下按 0 算。
    /// </summary>
    private static int SumBlockWithTenderDecay(int perPlay, int plays, int step)
    {
        if (plays <= 0)
        {
            return 0;
        }

        if (step <= 0)
        {
            return perPlay * plays;
        }

        int total = 0;

        for (int i = 0; i < plays; i++)
        {
            total += Math.Max(0, perPlay - i * step);
        }

        return total;
    }

    /// <summary>
    /// 选牌界面里，当前选中的牌中有几张虚无牌（Ethereal）。
    /// 这些牌一旦被弃置/消耗，就不会再在回合结束被消耗了。
    /// </summary>
    private static int GetSelectedEtherealCount()
    {
        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return 0;
        }

        EnsureSelectedCardsField(hand);

        if (_selectedCardsField?.GetValue(hand) is not System.Collections.IEnumerable items)
        {
            return 0;
        }

        int count = 0;

        foreach (var item in items)
        {
            if (item is CardModel c && c.Keywords.Contains(CardKeyword.Ethereal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 消耗选择界面里，选中的牌被烧掉会**额外**触发几次无惧疼痛。
    ///
    /// 注意不要和"手牌里的虚无牌"重复：
    /// 那些虚无牌本来就会在回合结束被消耗，已经算进 <see cref="GetFeelNoPainBlock"/> 了；
    /// 而在这里被提前烧掉只是把"回合结束那次"挪到现在，总数不变。
    /// 所以只统计选中的**非虚无**牌——它们才会带来净增的一次触发。
    /// </summary>
    private static int GetSelectedExhaustExtraBlock()
    {
        var creature = _playerCreature;
        var power = creature?.GetPower<FeelNoPainPower>();

        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        var hand = NCombatRoom.Instance?.Ui?.Hand;

        if (hand == null)
        {
            return 0;
        }

        EnsureSelectedCardsField(hand);

        if (_selectedCardsField?.GetValue(hand) is not System.Collections.IEnumerable items)
        {
            return 0;
        }

        int extra = 0;

        foreach (var item in items)
        {
            if (item is CardModel c && !c.Keywords.Contains(CardKeyword.Ethereal))
            {
                extra++;
            }
        }

        if (extra <= 0)
        {
            return 0;
        }

        // 返回"未乘融入暗影"的原始值，由调用方统一乘，避免重复
        return extra * power.Amount;
    }

    /// <summary>
    /// 这张牌会弃掉几张手牌：-1 = 弃全部，0 = 不弃牌。
    /// 清单来自游戏里调用 FromHandForDiscard / DiscardAndDraw 的卡牌。
    /// </summary>
    private static int GetDiscardCount(CardModel card)
    {
#if STS2_FOOL
        if (card is CalculatedGamble or WheelOfFortune or ShadowStep or StormOfSteel)
#else
        if (card is CalculatedGamble or ShadowStep or StormOfSteel)
#endif
        {
            return -1;
        }

#if STS2_FOOL
        if (card is Acrobatics or DaggerThrow or Prepared or Survivor or TheHermit)
#else
        if (card is Acrobatics or DaggerThrow or Prepared or Survivor)
#endif
        {
            return 1;
        }

        if (card is HiddenDaggers)
        {
            return Math.Max(0, card.DynamicVars.Cards.IntValue);
        }

        return 0;
    }

    /// <summary>
    /// 这张牌弃牌时能带来的奇巧收益：手牌里的奇巧技能牌被弃置后会自动打出，按它们给的格挡算。
    ///
    /// 只有两种情况在拖牌阶段给数字：
    ///   1. 弃全部（计算下注 / 命运之轮 / 暗影步 / 钢铁风暴）——弃哪些是确定的
    ///   2. 手牌数 <= 要弃的张数——游戏会自动全弃，不弹二级选牌界面
    /// 其余"弃 N 张但手牌更多"的牌会弹出二级选牌界面让玩家挑，
    /// 拖牌阶段猜"最高 N 张"会误导（玩家以为弃哪张都能拿这么多），所以不显示；
    /// 等进入二级界面后，按实际选中的牌实时预览（GetSelectedSlyBlock）。
    /// </summary>
    private static int GetDiscardSlyBlock(CardModel card)
    {
        int discardCount = GetDiscardCount(card);

        if (discardCount == 0)
        {
            return 0;
        }

        var player = card.Owner;

        if (player == null)
        {
            return 0;
        }

        int handCount = 0;
        int priorPlays = 0;
        var blocks = new List<int>();

        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            // 这张牌打出时会离开手牌，不参与计数
            if (handCard == card)
            {
                continue;
            }

            handCount++;

            if (handCard.Type != CardType.Skill || !handCard.IsSlyThisTurn)
            {
                continue;
            }

            int block = GetSlyAutoPlayBlock(handCard, priorPlays, out int plays);
            priorPlays += plays;

            if (block > 0)
            {
                blocks.Add(block);
            }
        }

        // 会弹二级选牌界面：交给界面的实时预览，拖牌阶段不猜
        if (discardCount > 0 && handCount > discardCount)
        {
            return 0;
        }

        // 弃全部，或者手牌数 <= 要弃的张数（游戏自动全弃）
        int total = 0;

        foreach (int block in blocks)
        {
            total += block;
        }

        return total;
    }

    /// <summary>选牌界面关闭时调用，清掉"待选牌"状态。</summary>
    public static void OnSelectScreenClosed()
    {
        _pendingSelectCard = null;
        _selectedCardsField = null;
    }

    /// <summary>由 NHealthBar.SetCreature 的补丁调用，记住玩家自己的那根血条。</summary>
    public static void AttachPlayerHealthBar(NHealthBar bar, Creature creature)
    {
        _playerBar = bar;
        _playerCreature = creature;

        // 诊断：确认绑到的确实是玩家自己的血条（而不是奥斯提那种宠物血条）。
        // 每场战斗一条，成本可忽略，出问题时能直接从 godot.log 对上。
        try
        {
            BlockPreviewLog.Perf($"[诊断] 绑定血条: creature={creature.Name} isPlayer={creature.IsPlayer} barPos={bar.GlobalPosition}");
        }
        catch
        {
        }

        // 新战斗：旧的 CardPile 实例已经废了，退订它们的 ContentsChanged 并清缓存，
        // 否则会一直持有旧堆引用（泄漏），而且事件还在往失效的对象上挂。
        ResetPileStats();

        DestroyUi();

        // 必须立刻把 UI 建出来：常驻显示是靠面板自身的 _Process 驱动的，
        // 如果等第一次拖牌才创建，就永远没有东西去触发它。
        EnsureUi();
    }

    // ==================================================================
    //  拖牌刷新的节流
    //
    //  拖牌期间 BuildValues 每帧全量重算（几十个 GetXxx，内含手牌遍历、
    //  球队列推演、战斗历史统计）。格挡数值不需要 60Hz，降到约 30Hz
    //  肉眼看不出差别，但 CPU 直接减半。Show() 里仍会立刻算一次，
    //  保证选中的第一帧就有正确数字。
    // ==================================================================
    /// <summary>
    /// 面板重算的最小**时间**间隔（毫秒）。
    ///
    /// ⚠️ 原来是「每 2 帧」（`DragRefreshFrameInterval`），只在 60FPS 下才约等于 30Hz：
    ///   · 60FPS  → 每 2 帧 = 33ms → 30Hz  ✅
    ///   · 144FPS → 每 2 帧 = 14ms → 72Hz  ❌
    ///   · 500FPS → 每 2 帧 = 4ms  → 250Hz ❌ 比不节流还密
    /// 游戏帧率上限是 500FPS，所以必须按时间而不是按帧数节流。
    /// 改成固定时间间隔后，60 / 144 / 500FPS 下都稳定在 ~30Hz。
    ///
    /// 想调：改这个值即可（1ms ≈ 1000Hz，等于几乎每帧都算；100ms ≈ 10Hz）。
    /// </summary>
    private const double RefreshIntervalMs = 33.0;

    /// <summary>把间隔换算成 Stopwatch 的 tick 数，避免每帧做浮点乘除。</summary>
    private static readonly long MinRefreshTicks =
        (long)(RefreshIntervalMs * (Stopwatch.Frequency / 1000.0));

    private static long _lastRefreshTicks;

    /// <summary>
    /// 到点了吗？到点返回 true 并记录本次时间；没到返回 false。
    /// 拖牌刷新和常驻刷新共用同一个计时器 —— 同一时刻只会有一条路径在跑。
    /// </summary>
    private static bool RefreshDue()
    {
        long now = Stopwatch.GetTimestamp();

        if (_lastRefreshTicks == 0)
        {
            _lastRefreshTicks = now;
            return true;
        }

        if (now - _lastRefreshTicks < MinRefreshTicks)
        {
            return false;
        }

        _lastRefreshTicks = now;
        return true;
    }

    // ==================================================================
    //  性能检查日志（2026-11-20）
    //
    //  用来验证《性能优化.md》里的优化是否真的生效、以及还剩哪些热点。
    //  统计拖牌期间的关键路径耗时 + 历史快照命中率，每 2 秒汇总一行写进
    //  **mod 自己的日志文件**（不走 godot.log，见 BlockPreviewLog）。
    //
    //  看日志：%APPDATA%\SlayTheSpire2\BlockPreviewLogs\Block Preview mod_Perf_*.log
    //
    //  · 开销：每次埋点只多 2 次 Stopwatch.GetTimestamp()（约 20ns），可忽略
    //  · 关闭：把 PerfLogEnabled 改成 false（或关掉设置里的「开启 mod 日志」）
    //  · 指标含义见 FlushPerfLog 里的注释
    // ==================================================================
    /// <summary>
    /// 性能日志总开关。
    /// 用 `static readonly` 而不是 `const`：`const` 会让编译器把
    /// `if (!PerfLogEnabled) return;` 折叠成死代码并报 CS0162 警告。
    /// 关闭方式：改成 false（首帧赋值一次，运行时分支开销可忽略）。
    /// </summary>
    private static readonly bool PerfLogEnabled = true;

    /// <summary>汇总间隔。太小会刷屏，太大会看不出瞬时卡顿。</summary>
    private const int PerfLogIntervalMs = 2000;

    private static long _perfRefreshCount;
    private static long _perfRefreshTotalUs;
    private static long _perfRefreshMaxUs;
    private static long _perfShowCount;
    private static long _perfShowTotalUs;
    private static long _perfShowMaxUs;
    private static long _perfSnapHit;
    private static long _perfSnapRebuild;
    private static long _perfSnapBuildTotalUs;
    private static long _perfSnapBuildMaxUs;
    private static long _perfTickSkipped;
    private static long _perfPileRebuild;
    private static long _perfPileRebuildTotalUs;
    private static long _perfPileRebuildMaxUs;
    private static long _perfPersistentCount;
    private static long _perfPersistentTotalUs;
    private static long _perfPersistentMaxUs;
    private static long _perfLastFlushTicks;

    // ---- 洗牌诊断（2026-11-21）----
    // 目的：把"洗牌那一瞬间 mod 到底花了多少时间"单独抓出来。
    // 洗牌会连发很多次 ContentsChanged，同时 persistent 每帧都在跑，
    // 只看 2 秒聚合的 Perf 行会把峰值平均掉。
    private static long _lastShuffleMs;
    private static int _pileEventsSinceShuffle;
    private static long _postShuffleMaxPersistentUs;
    private static long _postShuffleMaxRefreshUs;
    private static int _lastGen0, _lastGen1, _lastGen2;

    /// <summary>由 `BlockPreviewLog.TrackShuffle` 调用：记录一次洗牌并汇报上一次洗牌后的统计。</summary>
    public static void NoteShuffle()
    {
        long now = System.Environment.TickCount64;

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        if (_lastShuffleMs > 0)
        {
            BlockPreviewLog.Perf(
                $"[洗牌] 距上次 {now - _lastShuffleMs}ms"
                + $" | 期间牌堆事件 {_pileEventsSinceShuffle} 次"
                + $" | 洗牌后峰值 persistent {_postShuffleMaxPersistentUs / 1000.0:F2}ms"
                + $" / refresh {_postShuffleMaxRefreshUs / 1000.0:F2}ms"
                + $" | GC {gen0 - _lastGen0}/{gen1 - _lastGen1}/{gen2 - _lastGen2}");
        }

        _lastShuffleMs = now;
        _pileEventsSinceShuffle = 0;
        _postShuffleMaxPersistentUs = 0;
        _postShuffleMaxRefreshUs = 0;
        _lastGen0 = gen0;
        _lastGen1 = gen1;
        _lastGen2 = gen2;
    }

    private static long PerfToUs(long elapsedTicks)
    {
        return elapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }

    private static void PerfRecordRefresh(long us)
    {
        if (!PerfLogEnabled)
        {
            return;
        }

        _perfRefreshCount++;
        _perfRefreshTotalUs += us;

        if (us > _perfRefreshMaxUs)
        {
            _perfRefreshMaxUs = us;
        }

        // 洗牌后 300ms 内的峰值单独记（洗牌是牌堆剧变点，2 秒聚合会把它平均掉）
        if (_lastShuffleMs > 0
            && System.Environment.TickCount64 - _lastShuffleMs < 300
            && us > _postShuffleMaxRefreshUs)
        {
            _postShuffleMaxRefreshUs = us;
        }

        // 慢刷新明细：单次 refresh 超过 2ms 时单独记一行。
        // 用来定位「大量加牌 / 洗牌 / 塞眩晕」这类牌堆剧变场景下的卡顿到底落在哪：
        // 如果这行显示牌堆规模正常、重算次数为 0，说明慢的不是牌堆统计。
        if (us >= 2000 && _playerCreature?.Player != null)
        {
            try
            {
                var player = _playerCreature.Player;

                int draw = PileType.Draw.GetPile(player).Cards.Count;
                int discard = PileType.Discard.GetPile(player).Cards.Count;
                int hand = PileType.Hand.GetPile(player).Cards.Count;

                BlockPreviewLog.Perf(
                    $"[{MainFile.ModId}] 慢刷新 {us / 1000.0:F2}ms | 抽牌堆 {draw} 弃牌堆 {discard} 手牌 {hand}"
                    + $" | 牌堆重算 {_perfPileRebuild}次 | 历史 {HistoryEntryCount()}条");
            }
            catch
            {
                // 日志绝不影响游戏
            }
        }
    }

    private static void PerfRecordShow(long us)
    {
        if (!PerfLogEnabled)
        {
            return;
        }

        _perfShowCount++;
        _perfShowTotalUs += us;

        if (us > _perfShowMaxUs)
        {
            _perfShowMaxUs = us;
        }
    }

    private static void PerfRecordSnapBuild(long us)
    {
        if (!PerfLogEnabled)
        {
            return;
        }

        _perfSnapRebuild++;
        _perfSnapBuildTotalUs += us;

        if (us > _perfSnapBuildMaxUs)
        {
            _perfSnapBuildMaxUs = us;
        }
    }

    /// <summary>
    /// 常驻显示（`ShowPersistent`）的耗时。
    /// 之前漏了这个埋点，导致不拖牌时日志只有 "refresh 0次"，看不出常驻刷新有没有开销。
    /// </summary>
    private static void PerfRecordPersistent(long us)
    {
        if (!PerfLogEnabled)
        {
            return;
        }

        _perfPersistentCount++;
        _perfPersistentTotalUs += us;

        if (us > _perfPersistentMaxUs)
        {
            _perfPersistentMaxUs = us;
        }

        // 洗牌后 300ms 内的峰值单独记
        if (_lastShuffleMs > 0
            && System.Environment.TickCount64 - _lastShuffleMs < 300
            && us > _postShuffleMaxPersistentUs)
        {
            _postShuffleMaxPersistentUs = us;
        }
    }

    private static void PerfRecordSnapHit()
    {
        if (PerfLogEnabled)
        {
            _perfSnapHit++;
        }
    }

    /// <summary>
    /// 到点了就把这一段的统计汇总成一行打进日志，然后清零。
    ///
    /// 指标：
    ///   refresh —— 每次 RefreshDragPreview（拖牌期间的主路径，约 30Hz）
    ///   show    —— 每次 Show（选中卡牌那一帧，含一次完整 refresh，是"卡一下"的量化）
    ///   快照    —— 历史缓存命中/重建次数；重建率越高说明缓存判据越容易失效
    ///   节流    —— 被时间节流跳过的刷新次数（省下来的量）
    ///   历史    —— 当前战斗历史条目数（成本随它线性增长，验证"不再随战斗时长劣化"看它）
    /// </summary>
    private static void PerfFlushIfDue()
    {
        if (!PerfLogEnabled)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();

        if (_perfLastFlushTicks == 0)
        {
            _perfLastFlushTicks = now;
            return;
        }

        long elapsedMs = (now - _perfLastFlushTicks) * 1000L / Stopwatch.Frequency;

        if (elapsedMs < PerfLogIntervalMs)
        {
            return;
        }

        _perfLastFlushTicks = now;

        // 这段时间没有任何活动就别打空行（比如站着没动）
        if (_perfRefreshCount == 0 && _perfShowCount == 0 && _perfPersistentCount == 0
            && _perfSnapRebuild == 0 && _perfTickSkipped == 0)
        {
            return;
        }

        double refreshAvg = _perfRefreshCount > 0 ? _perfRefreshTotalUs / 1000.0 / _perfRefreshCount : 0;
        double refreshMax = _perfRefreshMaxUs / 1000.0;
        double refreshHz = elapsedMs > 0 ? _perfRefreshCount * 1000.0 / elapsedMs : 0;
        double showAvg = _perfShowCount > 0 ? _perfShowTotalUs / 1000.0 / _perfShowCount : 0;
        double showMax = _perfShowMaxUs / 1000.0;
        double snapAvg = _perfSnapRebuild > 0 ? _perfSnapBuildTotalUs / 1000.0 / _perfSnapRebuild : 0;
        double snapMax = _perfSnapBuildMaxUs / 1000.0;
        double hitRate = _perfSnapHit + _perfSnapRebuild > 0
            ? 100.0 * _perfSnapHit / (_perfSnapHit + _perfSnapRebuild)
            : 0;

        double persistentAvg = _perfPersistentCount > 0
            ? _perfPersistentTotalUs / 1000.0 / _perfPersistentCount
            : 0;
        double persistentMax = _perfPersistentMaxUs / 1000.0;

        double pileAvg = _perfPileRebuild > 0
            ? _perfPileRebuildTotalUs / 1000.0 / _perfPileRebuild
            : 0;
        double pileMax = _perfPileRebuildMaxUs / 1000.0;

        BlockPreviewLog.Perf(
            $"[{MainFile.ModId}] 性能({elapsedMs}ms): "
            + $"refresh {_perfRefreshCount}次({refreshHz:F1}Hz) 均{refreshAvg:F2}ms 峰{refreshMax:F2}ms | "
            + $"persistent {_perfPersistentCount}次 均{persistentAvg:F2}ms 峰{persistentMax:F2}ms | "
            + $"show {_perfShowCount}次 均{showAvg:F2}ms 峰{showMax:F2}ms | "
            + $"快照 命中{_perfSnapHit}/重建{_perfSnapRebuild}({hitRate:F0}%) 重建均{snapAvg:F2}ms 峰{snapMax:F2}ms | "
            + $"节流跳过{_perfTickSkipped}次 | 牌堆重算{_perfPileRebuild}次 均{pileAvg:F2}ms 峰{pileMax:F2}ms | 历史{HistoryEntryCount()}条");

        _perfRefreshCount = 0;
        _perfRefreshTotalUs = 0;
        _perfRefreshMaxUs = 0;
        _perfShowCount = 0;
        _perfShowTotalUs = 0;
        _perfShowMaxUs = 0;
        _perfSnapHit = 0;
        _perfSnapRebuild = 0;
        _perfSnapBuildTotalUs = 0;
        _perfSnapBuildMaxUs = 0;
        _perfTickSkipped = 0;
        _perfPileRebuild = 0;
        _perfPileRebuildTotalUs = 0;
        _perfPileRebuildMaxUs = 0;
        _perfPersistentCount = 0;
        _perfPersistentTotalUs = 0;
        _perfPersistentMaxUs = 0;
    }

    // ==================================================================
    //  战斗历史查询缓存（2026-11-20）
    //
    //  CombatHistory.CardPlaysFinished / Entries 都是 LINQ 查询：
    //      public IEnumerable<CardPlayFinishedEntry> CardPlaysFinished
    //          => Entries.OfType<CardPlayFinishedEntry>();
    //  每访问一次就把**整场战斗的全部历史条目**（伤害、格挡、抽牌、消耗…全算）
    //  重扫一遍，还额外分配一个迭代器。
    //
    //  BuildValues 每帧要查 4~6 次这类统计，于是：
    //    - 战斗越久越卡（成本随历史条目数线性增长）
    //    - 每次选中卡牌那一下明显掉帧
    //
    //  这里把 BuildValues 需要的 7 项历史统计（本回合攻击牌数 / 是否打过攻击牌 /
    //  是否打过能力牌 / 本回合卡牌来源格挡次数 / 本回合是否施加过灾厄 /
    //  本回合是否生成过牌 / 本场闪电球生成数）合并成**一次遍历**，
    //  并按「历史条目数 + 回合 + 阵营 + 各玩家 TurnNumber」做脏检查缓存。
    // ==================================================================

    private sealed class HistorySnapshot
    {
        public int AttackPlaysThisTurn;
        public bool AttackPlayedThisTurn;
        public bool PowerPlayedEver;
        public int CardBlockGainedThisTurn;
        public bool DoomAppliedThisTurn;
        public bool CardGeneratedThisTurn;
        public int LightningChannelsEver;
    }

    private static int _histSnapVersion = -2;
    private static int _histSnapRound = -1;
    private static int _histSnapTurnSig = int.MinValue;
    private static int _turnSigFallback;
    private static object? _histSnapSide;
    private static readonly Dictionary<Player, HistorySnapshot> _histSnaps = new();

    /// <summary>
    /// 缓存失效判据的一部分。
    /// CombatHistoryEntry.HappenedThisTurn 除了比轮次和阵营，还要比**每个玩家各自的
    /// PlayerCombatState.TurnNumber**（多人时别人过回合也会让结果变），所以这里把它们串起来。
    /// </summary>
    private static int TurnSignature(ICombatState state)
    {
        int sig = 17;

        try
        {
            foreach (var p in state.Players)
            {
                sig = sig * 31 + (p.PlayerCombatState?.TurnNumber ?? -1);
            }

            return sig;
        }
        catch
        {
            // 读不到就给一个新值，让缓存永远失效 —— 宁可慢，也不能显示错的数字。
            return ++_turnSigFallback;
        }
    }

    private static int HistoryEntryCount()
    {
        try
        {
            if (CombatManager.Instance?.History?.Entries is System.Collections.ICollection entries)
            {
                return entries.Count;
            }
        }
        catch
        {
            // 拿不到历史就退化成不缓存（返回 -1），调用方回退到直接查询。
        }

        return -1;
    }

    /// <summary>
    /// 取（必要时重建）某个玩家的战斗历史统计快照。
    /// 拿不到历史时返回 null，调用方回退到原来的直接 LINQ 查询。
    /// </summary>
    private static HistorySnapshot? GetHistorySnapshot(ICombatState state, Player player)
    {
        int version = HistoryEntryCount();

        if (version < 0)
        {
            return null;
        }

        var side = state.CurrentSide;
        int turnSig = TurnSignature(state);

        // 换回合 / 换阵营 / 任何一个玩家 TurnNumber 变化 / 历史被截断
        // → 即使条目数没变，「本回合」相关的结果也会全变，必须全量重算。
        bool hardReset = version < _histSnapVersion
                         || state.RoundNumber != _histSnapRound
                         || !Equals(side, _histSnapSide)
                         || turnSig != _histSnapTurnSig;

        if (hardReset)
        {
            _histSnaps.Clear();

            _histSnapRound = state.RoundNumber;
            _histSnapSide = side;
            _histSnapTurnSig = turnSig;
            _histSnapVersion = version;
        }

        // 只有「本回合内条目增加」这一种情况可以走增量：
        // 把所有已有玩家的快照一起往前推进（一次扫描覆盖全部玩家），
        // 成本是 O(新增条目)，与历史总长度无关。
        if (!hardReset && version > _histSnapVersion && _histSnaps.Count > 0)
        {
            long incStart = Stopwatch.GetTimestamp();

            foreach (var kv in _histSnaps)
            {
                AccumulateHistory(state, kv.Key, kv.Value, _histSnapVersion);
            }

            if (PerfLogEnabled)
            {
                PerfRecordSnapBuild(PerfToUs(Stopwatch.GetTimestamp() - incStart));
            }

            _histSnapVersion = version;
        }

        if (_histSnaps.TryGetValue(player, out var cached))
        {
            PerfRecordSnapHit();
            return cached;
        }

        long perfStart = Stopwatch.GetTimestamp();
        var snap = new HistorySnapshot();
        AccumulateHistory(state, player, snap, 0);

        if (PerfLogEnabled)
        {
            PerfRecordSnapBuild(PerfToUs(Stopwatch.GetTimestamp() - perfStart));
        }

        _histSnaps[player] = snap;
        return snap;
    }

    /// <summary>
    /// 把 `fromIndex` 之后的新条目累加进快照（`fromIndex = 0` 即全量）。
    ///
    /// 为什么要增量：`Entries` 每打一张牌就增长，旧实现「条目数一变就全量重扫」，
    /// 到后期历史四五百条时单次重建要 5ms 以上（实测慢刷新 **5.52ms @ 508 条**）。
    /// 现在只扫新增的那几条，重建成本与历史总长度无关。
    /// </summary>
    private static void AccumulateHistory(ICombatState state, Player player, HistorySnapshot snap, int fromIndex)
    {
        try
        {
            var entries = CombatManager.Instance.History.Entries;

            // 底层是 List<T>，能拿到索引就从 fromIndex 开始，O(新增条目)
            if (entries is IList<CombatHistoryEntry> list)
            {
                for (int i = Math.Max(0, fromIndex); i < list.Count; i++)
                {
                    AccumulateEntry(state, player, snap, list[i]);
                }

                return;
            }

            // 兜底：拿不到索引就整体枚举（极少发生）
            int index = 0;

            foreach (var entry in entries)
            {
                if (index++ < fromIndex)
                {
                    continue;
                }

                AccumulateEntry(state, player, snap, entry);
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 历史快照失败: {e.Message}");
        }
    }

    private static void AccumulateEntry(ICombatState state, Player player, HistorySnapshot snap, CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case CardPlayFinishedEntry play:
            {
                var card = play.CardPlay.Card;

                // 本场（不限回合）是否打出过能力牌：永冻冰晶用
                if (card.Owner == player && card.Type == CardType.Power)
                {
                    snap.PowerPlayedEver = true;
                }

                if (play.CardPlay.Player == player
                    && card.Type == CardType.Attack
                    && play.HappenedThisTurn(state))
                {
                    snap.AttackPlaysThisTurn++;
                    snap.AttackPlayedThisTurn = true;
                }

                break;
            }

            case BlockGainedEntry block:
            {
                // 本回合已结算过几次「卡牌/怪物行动」来源的格挡：不动（Immovable）用
                if (block.CardPlay != null
                    && block.CardPlay.Player.Creature == player.Creature
                    && block.Props.IsCardOrMonsterMove()
                    && block.HappenedThisTurn(state))
                {
                    snap.CardBlockGainedThisTurn++;
                }

                break;
            }

            case PowerReceivedEntry power:
            {
                // 本回合是否由自己施加过灾厄：灾厄之衣用
                if (power.Applier == player.Creature
                    && power.Power is DoomPower
                    && power.HappenedThisTurn(state))
                {
                    snap.DoomAppliedThisTurn = true;
                }

                break;
            }

            case CardGeneratedEntry generated:
            {
                // 本回合是否已经生成过牌：君王矿石（每回合第一次生成牌）用
                if (generated.Creator == player
                    && generated.HappenedThisTurn(state))
                {
                    snap.CardGeneratedThisTurn = true;
                }

                break;
            }

            case OrbChanneledEntry channeled:
            {
                // 本场累计生成过多少闪电球：Voltaic 的 CalculatedChannels 用
                if (channeled.Actor.Player == player && channeled.Orb is LightningOrb)
                {
                    snap.LightningChannelsEver++;
                }

                break;
            }
        }
    }

    // ==================================================================
    //  牌堆统计缓存（2026-11-20）
    //
    //  只给**纯依赖牌堆内容**的统计用：
    //    · 抽牌堆 / 弃牌堆的技能牌数（逃脱计划）
    //    · 抽牌堆里所向无敌的数量与格挡（所向无敌，两处调用合并成一次）
    //    · 牌堆里的非攻击牌数（重振精神重放推演）
    //
    //  失效判据是**双保险**，宁可多算也不漏算：
    //    1. 堆实例变了（换战斗 / 换玩家）→ 重新订阅 + 重算
    //    2. 张数变了（抽牌 / 加牌 / 洗牌都会改张数）
    //    3. 订阅的 ContentsChanged 触发过（覆盖"张数没变但内容变了"）
    //
    //  ⚠️ 为什么不能只靠事件：游戏里有些改动走 silent 路径
    //  （`CardPile.AddInternal(..., silent: true)`）**不触发** ContentsChanged，
    //  只订阅事件会让缓存永久留在旧值上，显示错误数字。
    //  张数比对是 O(1)，比整堆遍历便宜得多，所以两条一起用。
    // ==================================================================

    private sealed class PileStats
    {
        public int Total;
        public int Skills;
        public int NonAttacks;
        public int Invincibles;
        public int InvincibleBlock;
    }

    private static readonly Dictionary<CardPile, PileStats> _pileStats = new();
    private static readonly HashSet<CardPile> _watchedPiles = new();

    /// <summary>牌堆内容变过、需要重建缓存。</summary>
    private static bool _pileStatsDirty = true;

    /// <summary>
    /// 任何一个受监视的牌堆内容变了 → 只**置脏**，不立即清空。
    ///
    /// 为什么不能直接 Clear：**洗牌会连续触发很多次 `ContentsChanged`**，
    /// 每清一次，下一次 `GetPileStats` 就要重新遍历整堆 + 重算所向无敌的 `Hook.ModifyBlock`，
    /// 洗牌瞬间就会出现多次全量重算（实测洗牌时 `persistent` 出现过 3ms 的峰值）。
    /// 置脏后由 `GetPileStats` 在**本帧第一次**调用时统一清一次，一帧最多重建一遍。
    ///
    /// 用无捕获的方法引用是为了能正常 `-=` 退订。
    /// </summary>
    private static void OnAnyPileChanged()
    {
        _pileStatsDirty = true;

        if (PerfLogEnabled)
        {
            _pileEventsSinceShuffle++;
        }
    }

    private static void WatchPile(CardPile pile)
    {
        if (_watchedPiles.Add(pile))
        {
            pile.ContentsChanged += OnAnyPileChanged;
        }
    }

    /// <summary>换战斗时调用：退订旧堆并清空缓存，避免持有已废弃的 CardPile。</summary>
    private static void ResetPileStats()
    {
        foreach (var pile in _watchedPiles)
        {
            try
            {
                pile.ContentsChanged -= OnAnyPileChanged;
            }
            catch
            {
                // 旧堆可能已经随旧战斗一起废掉了，忽略即可
            }
        }

        _watchedPiles.Clear();
        _pileStats.Clear();
        _pileStatsDirty = true;
    }

    /// <summary>
    /// 取某个牌堆的统计（必要时重算）。拿不到堆返回 null，调用方自行兜底。
    /// </summary>
    private static PileStats? GetPileStats(CardPile? pile)
    {
        if (pile == null)
        {
            return null;
        }

        WatchPile(pile);

        // 有牌堆变过 → 统一清一次（见 OnAnyPileChanged 的说明：
        // 洗牌会连发很多次事件，只在这里清，保证一帧最多重建一遍）
        if (_pileStatsDirty)
        {
            _pileStats.Clear();
            _pileStatsDirty = false;
        }

        if (_pileStats.TryGetValue(pile, out var cached) && cached.Total == pile.Cards.Count)
        {
            return cached;
        }

        var stats = BuildPileStats(pile);
        _pileStats[pile] = stats;
        return stats;
    }

    /// <summary>一次遍历同时算出四项统计 —— 这就是"共享遍历"。</summary>
    private static PileStats BuildPileStats(CardPile pile)
    {
        long perfStart = PerfLogEnabled ? Stopwatch.GetTimestamp() : 0;

        var stats = new PileStats();

        foreach (var card in pile.Cards)
        {
            stats.Total++;

            if (card.Type == CardType.Skill)
            {
                stats.Skills++;
            }

            if (card.Type != CardType.Attack)
            {
                stats.NonAttacks++;
            }

            if (card is IAmInvincible invincible)
            {
                stats.Invincibles++;

                if (stats.InvincibleBlock <= 0
                    && invincible.DynamicVars.TryGetValue(BlockVar.defaultName, out var bv)
                    && bv is BlockVar blockVar)
                {
                    // ⚠️ 不能读 `blockVar.IntValue`：那是**不含任何修正**的裸值。
                    // 牌堆里的牌不渲染卡面，`PreviewValue` 也不会被 `UpdateCardPreview` 更新，
                    // 所以这里直接走一次 `Hook.ModifyBlock`（与 `BlockVar.UpdateCardPreview` 完全一致），
                    // 敏捷 / 脆弱 / 不动 / 融入暗影等修正都能算进来。
                    stats.InvincibleBlock = ComputeCardBlockValue(invincible, blockVar);
                }
            }
        }

        if (PerfLogEnabled)
        {
            long us = PerfToUs(Stopwatch.GetTimestamp() - perfStart);

            _perfPileRebuild++;
            _perfPileRebuildTotalUs += us;

            if (us > _perfPileRebuildMaxUs)
            {
                _perfPileRebuildMaxUs = us;
            }
        }

        return stats;
    }

    /// <summary>卡牌被拖出时调用。之后由 TickAlways 每帧刷新数值。</summary>
    public static void Show(CardModel? card)
    {
        long perfStart = Stopwatch.GetTimestamp();

        try
        {
            if (!BlockPreviewConfig.Enabled || card == null)
            {
                Hide();
                return;
            }

            if (!IsBarAlive())
            {
                return;
            }

            EnsureUi();
            if (_panel == null)
            {
                return;
            }

            _draggedCard = card;
            RefreshDragPreview();

            // Show 这一帧已经算过了，让 TickAlways 等一个间隔再来，
            // 否则选中瞬间会连着算两次（Show + 紧随的 TickAlways）。
            _lastRefreshTicks = Stopwatch.GetTimestamp();
        }
        catch (Exception e)
        {
            BlockPreviewLog.Crash("Show failed", e);
            Hide();
        }
        finally
        {
            if (PerfLogEnabled)
            {
                PerfRecordShow(PerfToUs(Stopwatch.GetTimestamp() - perfStart));
            }
        }
    }

    /// <summary>拖拽结束/取消时调用。常驻或调整模式下不隐藏。</summary>
    public static void Hide()
    {
        // 净化打出后会打开选牌界面，这时先别把预览收起来，
        // 让玩家在选牌期间仍能看到上限收益。
        if (_draggedCard is Purity)
        {
            _pendingSelectCard = _draggedCard;
            GD.Print($"[{MainFile.ModId}] pending select card = Purity");
        }
        else
        {
            _pendingSelectCard = null;
        }

        _draggedCard = null;

        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
        {
            return;
        }

        // 选牌界面还开着时（_pendingSelectCard 非空），即使没开常驻也要显示
        if ((IsPersistent() || _pendingSelectCard != null) && IsBarAlive())
        {
            ShowPersistent();
        }
        else
        {
            _panel.Visible = false;
        }
    }

    /// <summary>由面板每帧调用。</summary>
    public static void TickAlways()
    {
        // 日志：检测到**新的战斗状态对象**就轮转文件（每类只保留最近 3 场）
        BlockPreviewLog.ObserveCombat(_playerCreature?.CombatState);

        // 性能日志的汇总点：每帧调一次，内部只在到点时打印（未启用时是 const 分支，零开销）
        PerfFlushIfDue();

        if (!BlockPreviewConfig.Enabled)
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel) && _panel.Visible)
            {
                _panel.Visible = false;
            }

            return;
        }

        if (!IsBarAlive())
        {
            // 不在战斗界面（血条被隐藏）→ 顺手把面板收起来，
            // 避免它停在上一个界面留下的“显示中”状态
            if (_panel != null && GodotObject.IsInstanceValid(_panel) && _panel.Visible)
            {
                _panel.Visible = false;
            }

            return;
        }

        // 拖牌中：按时间节流到约 30Hz，这样目标（易伤/格挡）或血条数值变化时数字能跟上。
        // 每帧全量重算是之前卡顿的主因之一，见 RefreshIntervalMs 的说明。
        if (_draggedCard != null)
        {
            if (!RefreshDue())
            {
                if (PerfLogEnabled)
                {
                    _perfTickSkipped++;
                }

                return;
            }

            RefreshDragPreview();
            return;
        }

        // 选牌界面开着（净化刚打出）：保持显示收益；选完就清掉
        if (_pendingSelectCard != null)
        {
            if (!IsSelectionActive())
            {
                _pendingSelectCard = null;
            }
            else
            {
                RefreshPersistentDue();
                return;
            }
        }

        // 弃牌选择界面：选中的奇巧技能牌弃置后会自动打出，预览它的格挡
        if (IsSelectionActive() && IsDiscardSelection())
        {
            RefreshPersistentDue();
            return;
        }

        // 所向无敌还躺在抽牌堆里时，常驻显示它的自动打出的格挡
        if (BlockPreviewConfig.ShowInvincible && HasInvincibleInDrawPile())
        {
            RefreshPersistentDue();
            return;
        }

        if (!IsPersistent())
        {
            return;
        }

        RefreshPersistentDue();
    }

    /// <summary>
    /// 常驻显示同样要走时间节流。
    /// 原先这里每个分支都直接 `ShowPersistent()`，等于**每帧重算** ——
    /// 60FPS 下是 60Hz，500FPS 下就是 500Hz，是当前最大的热点。
    /// </summary>
    private static void RefreshPersistentDue()
    {
        if (!RefreshDue())
        {
            if (PerfLogEnabled)
            {
                _perfTickSkipped++;
            }

            return;
        }

        ShowPersistent();
    }

    /// <summary>调整模式入口（保留给外部调用）。</summary>
    public static void EnterAdjustMode()
    {
        if (IsBarAlive())
        {
            ShowPersistent();
        }
    }

    /// <summary>
    /// 拖动过程中把面板位置换算回偏移量。
    ///
    /// 写进哪一套取决于当前角色：
    ///   - 「不跟随主位置」→ 写该角色自己的 `OffsetXxx/OffsetYxx`
    ///   - 跟随（或识别不到角色）→ 写全局 `OffsetX/OffsetY`（主位置）
    /// 这样在调整模式里直接拖，就能只改当前角色的位置。
    /// </summary>
    public static void SyncFromPanel(Vector2 panelPosition)
    {
        if (!IsBarAlive())
        {
            return;
        }

        Vector2 offset = panelPosition - GetAnchor();
        double x = Math.Round(offset.X);
        double y = Math.Round(offset.Y);

        // 强固定模式：面板挂在屏幕层上，拖到的位置**就是屏幕坐标**，直接保存
        if (BlockPreviewConfig.PinToScreen)
        {
            BlockPreviewConfig.OffsetX = Math.Round(panelPosition.X);
            BlockPreviewConfig.OffsetY = Math.Round(panelPosition.Y);
            return;
        }

        // 选牌 / 弃牌 / 替换 / 消耗等二级界面：面板钉在屏幕坐标上，单独保存
        if (IsCardSelectScreenVisible())
        {
            Vector2 global = _panel != null && GodotObject.IsInstanceValid(_panel)
                ? _panel.GlobalPosition
                : panelPosition;

            BlockPreviewConfig.SelectScreenX = (int)Math.Round(global.X);
            BlockPreviewConfig.SelectScreenY = (int)Math.Round(global.Y);
            return;
        }

        string key = CurrentCharacterKey();

        if (key.Length > 0 && !IsFollowing(key))
        {
            switch (key)
            {
                case "Ironclad":
                    BlockPreviewConfig.OffsetXIronclad = x;
                    BlockPreviewConfig.OffsetYIronclad = y;
                    return;
                case "Silent":
                    BlockPreviewConfig.OffsetXSilent = x;
                    BlockPreviewConfig.OffsetYSilent = y;
                    return;
                case "Defect":
                    BlockPreviewConfig.OffsetXDefect = x;
                    BlockPreviewConfig.OffsetYDefect = y;
                    return;
                case "Regent":
                    BlockPreviewConfig.OffsetXRegent = x;
                    BlockPreviewConfig.OffsetYRegent = y;
                    return;
                case "Necrobinder":
                    BlockPreviewConfig.OffsetXNecrobinder = x;
                    BlockPreviewConfig.OffsetYNecrobinder = y;
                    return;
                case "Fool":
                    BlockPreviewConfig.OffsetXFool = x;
                    BlockPreviewConfig.OffsetYFool = y;
                    return;
            }
        }

        BlockPreviewConfig.OffsetX = x;
        BlockPreviewConfig.OffsetY = y;
    }

    /// <summary>单击锁定：关闭调整模式并写盘。</summary>
    public static void LockAndSave()
    {
        BlockPreviewConfig.AdjustMode = false;
        try
        {
            MainFile.Config?.Save();

            string key = CurrentCharacterKey();
            Vector2 effective = GetEffectiveOffset();
            GD.Print($"[{MainFile.ModId}] 位置已锁定并保存: 角色={key} 跟随={IsFollowing(key)} " +
                     $"主位置=({BlockPreviewConfig.OffsetX:F0},{BlockPreviewConfig.OffsetY:F0}) " +
                     $"实际=({effective.X:F0},{effective.Y:F0})");
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 保存配置失败: {e.Message}");
        }

        if (!IsPersistent() && _panel != null && GodotObject.IsInstanceValid(_panel))
        {
            _panel.Visible = false;
        }
    }

    private static bool IsPersistent()
    {
        return BlockPreviewConfig.AlwaysVisible || BlockPreviewConfig.AdjustMode;
    }

    /// <summary>
    /// 拖牌中的刷新：重算全部数值然后渲染。
    /// 这张牌当前不给任何格挡时（例如拳斗被敌人格挡完全吃掉），回落到常驻显示或隐藏。
    /// </summary>
    private static void RefreshDragPreview()
    {
        if (!PerfLogEnabled)
        {
            RefreshDragPreviewCore();
            return;
        }

        long perfStart = Stopwatch.GetTimestamp();

        try
        {
            RefreshDragPreviewCore();
        }
        finally
        {
            PerfRecordRefresh(PerfToUs(Stopwatch.GetTimestamp() - perfStart));
        }
    }

    private static void RefreshDragPreviewCore()
    {
        var card = _draggedCard;
        if (card == null || _panel == null || !IsBarAlive())
        {
            return;
        }

        var creature = card.Owner?.Creature;
        if (creature == null)
        {
            Hide();
            return;
        }

        var values = BuildValues(card, creature, out bool cardContributes);

        // 格挡追踪日志：每次预览变化写一行（同卡同数值不重复）
        BlockPreviewLog.TrackPreview(card, values);

        if (!cardContributes)
        {
            if (IsPersistent())
            {
                ShowPersistent(card);
            }
            else if (_panel.Visible)
            {
                _panel.Visible = false;
            }

            return;
        }

        _panel.SetValues(values);

        // 临时诊断：确认最终传给面板的值（用来区分"没算出来"和"算了但没显示"）
        if (values.FrostBlock > 0 || values.TotalBlock > 0)
        {
            string panelDbg = $"面板值: frost={values.FrostBlock} evoke={values.EvokeBlock}"
                              + $" overflow={values.OverflowBlock} total={values.TotalBlock}";

            if (panelDbg != _lastPanelDbg)
            {
                _lastPanelDbg = panelDbg;
                BlockPreviewLog.Perf($"[诊断] {panelDbg}");
            }
        }
        ApplyLayout();

        if (!_panel.Visible)
        {
            _panel.Visible = true;
        }
    }

    /// <summary>显示常驻条：没有手牌在选，手牌格挡记 0。</summary>
    /// <param name="excludeCard">
    /// 正在拖出的牌。它会作为这张牌被打出，不会留到回合结束被虚无消耗，所以统计时排除。
    /// </param>
    private static void ShowPersistent(CardModel? excludeCard = null)
    {
        if (!PerfLogEnabled)
        {
            ShowPersistentCore(excludeCard);
            return;
        }

        long perfStart = Stopwatch.GetTimestamp();

        try
        {
            ShowPersistentCore(excludeCard);
        }
        finally
        {
            PerfRecordPersistent(PerfToUs(Stopwatch.GetTimestamp() - perfStart));
        }
    }

    private static void ShowPersistentCore(CardModel? excludeCard = null)
    {
        if (!IsBarAlive())
        {
            return;
        }

        // 遗物详情 / 生物状态栏 / 单个状态图标出现时先让位，
        // 否则面板的 ZIndex=100 会盖在它们上面（用户反馈 2026-11-21）。
        // 另外「点开某个遗物」的查看界面、牌组查看、地图、设置、牌堆浏览
        // 都会盖住面板，也一并让位。
        if (IsBlockingHoverTipVisible() || IsInspectRelicScreenVisible() || IsFullScreenOverlayVisible())
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel) && _panel.Visible)
            {
                _panel.Visible = false;
            }

            return;
        }

        EnsureUi();
        if (_panel == null)
        {
            return;
        }

        int currentBlock = _playerCreature?.Block ?? 0;
        int platingBlock = GetPlatingBlock(_playerCreature, out var platingIcon);
        // 常驻也推演一遍：吞暗（`ConsumingShadowPower`）的回合结束激发是**确定会发生的**，
        // 不拖牌时也该看得到；它的格挡同样受融入暗影影响（实测确认，见 8.1）。
        SimulateOrbs(_playerCreature, null, 1, out int evokeBlock, out _, out int frostBlock,
            out int goldCablesBlock, out var evokeIcon, out _, out var frostIcon, out var goldCablesIcon);
        int feelNoPainBlock = GetFeelNoPainBlock(excludeCard, _playerCreature, out var feelNoPainIcon);

        int handBlock = 0;

        // 选牌界面还开着时，把那张牌的收益也算进去。
        // 净化按"已选张数 + 自己"实时算，其它牌用默认算法。
        if (_pendingSelectCard != null)
        {
            if (_pendingSelectCard is Purity)
            {
                var fpn = _playerCreature?.GetPower<FeelNoPainPower>();

                if (fpn != null && fpn.Amount > 0)
                {
                    int selected = GetSelectedCardCount();

                    if (selected != _loggedSelectCount)
                    {
                        _loggedSelectCount = selected;
                        GD.Print($"[{MainFile.ModId}] selected = {selected}");
                    }

                    feelNoPainBlock += (selected + 1) * fpn.Amount;
                }
            }
            else
            {
                feelNoPainBlock += GetFeelNoPainBlock(_pendingSelectCard, _playerCreature, out _);
            }
        }
        else if (IsSelectionActive() && IsDiscardSelection())
        {
            // 弃牌选择：选中的奇巧技能牌被弃置后会自动打出，预览它们的格挡（多选时全部累加）。
            handBlock = GetSelectedSlyBlock();

            // 被弃置的虚无牌不会再在回合结束被消耗，无惧疼痛要相应减掉。
            // 注意：弃置本身**不**触发无惧疼痛（那是"消耗"才触发的）。
            var fpn = _playerCreature?.GetPower<FeelNoPainPower>();

            if (fpn != null && fpn.Amount > 0)
            {
                feelNoPainBlock -= GetSelectedEtherealCount() * fpn.Amount;
            }
        }
        else if (IsSelectionActive() && IsExhaustSelection())
        {
            // 消耗选择（燃烧契约 / 坚毅 / 烙印…）：选中的牌会被烧掉，
            // 每烧一张额外触发一次无惧疼痛。虚无牌不重复计（见方法注释）。
            feelNoPainBlock += GetSelectedExhaustExtraBlock();
        }

        // 融入暗影（Shadowmeld）：和 BuildValues 保持一致，统一乘一次。
        // 覆甲 / 冰球被动 / 遗物段都在 Shadowmeld 移除（AfterSideTurnEnd）之前结算，
        // 所以同样要乘 —— 详见 BuildValues 里的时序说明。
        int blockMultiplier = GetBlockGainMultiplier(_playerCreature);
        feelNoPainBlock *= blockMultiplier;
        platingBlock *= blockMultiplier;
        frostBlock *= blockMultiplier;
        goldCablesBlock *= blockMultiplier;
        evokeBlock *= blockMultiplier;

        // 所向无敌：只要抽牌堆里还有这张牌就常驻显示（它会在牌堆顶自动打出）。
        GetInvinciblePreview(_playerCreature?.Player,
            out int invincibleBlock, out int invinciblePercent, out var invincibleIcon);
        invincibleBlock *= blockMultiplier;

        // 覆甲 / 冰球被动 / 无惧疼痛 / 所向无敌都是"本回合结束时才结算"的收益。
        // 敌方回合时这些已经结算过了（冰球触发出来的护盾已经算进当前格挡），
        // 再叠一次就会出现「+4 冰球 =8」这种把同一份护盾算两遍的显示。
        if (!IsPlayerTurn())
        {
            platingBlock = 0;
            frostBlock = 0;
            goldCablesBlock = 0;
            feelNoPainBlock = 0;
            invincibleBlock = 0;
            evokeBlock = 0;
        }

        // 不可格挡（NoBlockPower）：常驻时唯一"卡牌来源"的收益是
        // 选牌界面里选中的奇巧牌（被弃后自动打出），它会被游戏归零。
        if (HasNoBlockPower(_playerCreature))
        {
            handBlock = 0;
        }

        // 按开关过滤
        handBlock = Show(BlockPreviewConfig.ShowHandBlock, handBlock);
        platingBlock = Show(BlockPreviewConfig.ShowPlating, platingBlock);
        frostBlock = Show(BlockPreviewConfig.ShowFrost, frostBlock);
        goldCablesBlock = Show(BlockPreviewConfig.ShowGoldPlatedCables, goldCablesBlock);
        evokeBlock = Show(BlockPreviewConfig.ShowEvoke, evokeBlock);
        feelNoPainBlock = Show(BlockPreviewConfig.ShowFeelNoPain, feelNoPainBlock);
        invincibleBlock = Show(BlockPreviewConfig.ShowInvincible, invincibleBlock);

        // 常驻显示时也要带上「回合结束才结算」的遗物段，
        // 否则玩家买了奥里哈刚/斗篷扣/波纹水盆却永远看不到它们。
        // 常驻时没有正在拖的牌，所以：
        //   斗篷扣按**当前**手牌数算（afterPlay: false）
        //   奥里哈刚用当前格挡判断（它检查的正是回合结束那一刻的 Block）
        var persistentPlayer = _playerCreature?.Player;

        int cloakClaspBlock = 0;
        Texture2D? cloakClaspIcon = null;
        int orichalcumBlock = 0;
        Texture2D? orichalcumIcon = null;
        int fakeOrichalcumBlock = 0;
        Texture2D? fakeOrichalcumIcon = null;
        int rippleBasinBlock = 0;
        Texture2D? rippleBasinIcon = null;

        if (IsPlayerTurn())
        {
            // 选牌界面里被拿起的牌还挂在 Hand pile 里，但它马上会被弃掉，
            // 回合结束时已经不在手牌了 —— 斗篷扣要把它扣掉，否则会多算一张。
            int pendingSelection = IsSelectionActive() ? GetSelectedCardCount() : 0;

            cloakClaspBlock = Show(BlockPreviewConfig.ShowCloakClasp,
                GetCloakClaspBlock(persistentPlayer, out cloakClaspIcon,
                    afterPlay: false, extraHandDelta: -pendingSelection));

            // 奥里哈刚的"已有格挡"必须把选牌界面里**已选中**的收益算进来：
            //   - 弃牌界面选中奇巧牌（触不可及等）→ handBlock 里已经有那 6 点
            //   - 消耗界面烧牌 → 无惧疼痛会**立即**给格挡
            // 注意：背景的 feelNoPainBlock 含「手牌虚无牌」，那是回合结束之后才消耗的，
            // 赶不上奥里哈刚的判断，所以这里不能用它，只能用"选中即烧"那部分。
            int persistentImmediate = currentBlock + handBlock;

            if (IsSelectionActive() && IsExhaustSelection())
            {
                persistentImmediate += GetSelectedExhaustTriggerBlock();
            }

            orichalcumBlock = Show(BlockPreviewConfig.ShowOrichalcum,
                GetOrichalcumLikeBlock<Orichalcum>(persistentPlayer, persistentImmediate, out orichalcumIcon));

            fakeOrichalcumBlock = Show(BlockPreviewConfig.ShowOrichalcum,
                GetOrichalcumLikeBlock<FakeOrichalcum>(persistentPlayer, persistentImmediate, out fakeOrichalcumIcon));

            rippleBasinBlock = Show(BlockPreviewConfig.ShowRippleBasin,
                GetRippleBasinBlock(null, persistentPlayer, out rippleBasinIcon));
        }

        // 遗物段同样在 Shadowmeld 移除之前结算（BeforeSideTurnEnd），要乘融入暗影。
        // 放在 Show 之后乘也一样：被开关关掉的段是 0，乘多少还是 0。
        cloakClaspBlock *= blockMultiplier;
        orichalcumBlock *= blockMultiplier;
        fakeOrichalcumBlock *= blockMultiplier;
        rippleBasinBlock *= blockMultiplier;

        // 奥斯提生命：替玩家挨伤害，等于一层自带壁垒的护盾。
        // 与"打哪张牌"无关，常驻显示时也要有（否则买了骨手却看不到那部分护盾）。
        int ostyHpBlock = Show(BlockPreviewConfig.ShowOstyHp,
            GetOstyHpBlock(_playerCreature, out var ostyHpIcon));

        // ⚠️ 所向无敌是**概率性**收益（在抽牌堆里只代表"洗牌后落到顶部的概率"），
        // 绝不能直接算进第一行的"确定格挡" —— 否则会出现
        // 「面板显示有护盾，但回合结束它根本没触发」的误导。
        // 只有概率 100%（抽牌堆里全是它）才算确定值，此时才进第一行。
        bool invincibleCertain = invincibleBlock > 0 && invinciblePercent >= 100;

        int persistentCertain = currentBlock + handBlock + platingBlock + frostBlock
                                + goldCablesBlock + feelNoPainBlock + evokeBlock
                                + cloakClaspBlock + orichalcumBlock + fakeOrichalcumBlock
                                + rippleBasinBlock + ostyHpBlock
                                + (invincibleCertain ? invincibleBlock : 0);

        int persistentTotal = persistentCertain + (invincibleCertain ? 0 : invincibleBlock);

        // 药水常驻显示在**第三行**（固定值，和"打牌的估算"分开）。
        var persistentPotions = BlockPreviewConfig.ShowPotion
            ? GetPotionPreviews(persistentPlayer)
            : new List<PotionPreview>();

        _panel.SetValues(new BlockPreviewValues
        {
            HandBlock = handBlock,
            PlatingBlock = platingBlock,
            FrostBlock = frostBlock,
            GoldCablesBlock = goldCablesBlock,
            GoldCablesIcon = goldCablesBlock > 0 ? goldCablesIcon : null,
            EvokeBlock = evokeBlock,
            EvokeIcon = evokeBlock > 0 ? evokeIcon : null,
            FeelNoPainBlock = feelNoPainBlock,
            InvincibleBlock = invincibleBlock,
            InvinciblePercent = invincibleBlock > 0 ? invinciblePercent : -1,
            InvincibleIcon = invincibleBlock > 0 ? invincibleIcon : null,
            CloakClaspBlock = cloakClaspBlock,
            CloakClaspIcon = cloakClaspBlock > 0 ? cloakClaspIcon : null,
            OrichalcumBlock = orichalcumBlock,
            OrichalcumIcon = orichalcumBlock > 0 ? orichalcumIcon : null,
            FakeOrichalcumBlock = fakeOrichalcumBlock,
            FakeOrichalcumIcon = fakeOrichalcumBlock > 0 ? fakeOrichalcumIcon : null,
            RippleBasinBlock = rippleBasinBlock,
            RippleBasinIcon = rippleBasinBlock > 0 ? rippleBasinIcon : null,
            Potions = persistentPotions,
            // 第一行只放**确定**格挡；所向无敌的概率收益不进这里
            TotalBlock = persistentCertain,
            // 第二行（估算行）收容所向无敌的概率收益
            EstimatedTotalBlock = persistentTotal,
            // 常驻时只有奥斯提生命这一项是非格挡（召唤/回复不拖牌就不会有）
            RealBlock = Math.Max(0, persistentCertain - ostyHpBlock),
            OstyHpBlock = ostyHpBlock,
            OstyHpIcon = ostyHpBlock > 0 ? ostyHpIcon : null,
            PlatingIcon = platingIcon,
            FrostIcon = frostIcon,
            FeelNoPainIcon = feelNoPainIcon,
        });

        ApplyLayout();

        if (!_panel.Visible)
        {
            _panel.Visible = true;
        }
    }

    /// <summary>
    /// 玩家身上是否有「不可格挡」（NoBlockPower）。
    ///
    /// 它的 <c>ModifyBlockMultiplicative</c> 只在**卡牌来源**上返回 0：
    ///   props 带 `Unpowered`（遗物 / 能力 / 药水给的格挡）→ 不受影响
    ///   cardSource == null（非卡牌触发）              → 不受影响
    ///   其余（卡牌直接给的格挡）                      → 归零
    /// 所以适配时**只归零卡牌来源的段**，覆甲/冰球/遗物/药水照常显示。
    /// </summary>
    private static bool HasNoBlockPower(Creature? creature)
    {
        return creature != null && creature.GetPowerAmount<NoBlockPower>() > 0;
    }

    /// <summary>
    /// 把「卡牌来源」的格挡段归零（不可格挡状态生效时）。
    /// 能力/遗物/药水给的段（Unpowered）不在此列，它们不受该状态影响。
    /// </summary>
    private static void ZeroCardSourcedBlock(
        ref int handBlock, ref int damageBlock, ref int mirageBlock, ref int escapePlanBlock,
        ref int extraPlayBlock, ref int fiendFireBlock,
        ref int fiendFireReplayBlock, ref int secondWindReplayBlock)
    {
        handBlock = 0;
        damageBlock = 0;
        mirageBlock = 0;
        escapePlanBlock = 0;
        extraPlayBlock = 0;
        fiendFireBlock = 0;
        fiendFireReplayBlock = 0;
        secondWindReplayBlock = 0;
    }

    /// <summary>
    /// 勒紧（FastenPower）：给带 <c>CardTag.Defend</c> 标签的牌**额外 +「层数」格挡**。
    ///
    /// 判定照抄游戏：`props.IsPoweredCardOrMonsterMoveBlock()` 且 `cardSource.Tags` 含 `Defend`。
    ///
    /// ⚠️ 卡面的 `BlockVar.PreviewValue` 里**已经包含**这个加成
    /// （`BlockVar.UpdateCardPreview` 会走 `Hook.ModifyBlock`，`cardSource` 就是这张牌），
    /// 所以这里返回的是**单次播放的加成量**，调用方必须从手牌段里扣除后再单独展示，
    /// 否则会把同一份格挡算两遍。
    /// </summary>
    private static int GetFastenBlockPerPlay(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (!card.Tags.Contains(CardTag.Defend))
        {
            return 0;
        }

        var power = creature?.GetPower<FastenPower>();

        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        icon = TryGetPowerIcon<FastenPower>();
        return power.Amount;
    }

    /// <summary>
    /// 灰烬之灵（SpiritOfAshPower）：每打出一张**虚无（Ethereal）**牌获得「层数」点格挡。
    ///
    /// 游戏实现是 `BeforeCardPlayed`（打出当场），所以：
    ///   - 拖的牌**自己**带 `CardKeyword.Ethereal` 才会触发
    ///   - 手牌里没打的虚无牌**不会**触发（它们是在回合结束被消耗，不是打出）
    ///   - 有多少次播放就给多少次
    ///
    /// 它属于「打牌当场」的那批，受融入暗影影响（`ValueProp.Unpowered` 也照乘，
    /// 由外层统一处理）。
    /// </summary>
    private static int GetSpiritOfAshBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (totalPlays <= 0)
        {
            return 0;
        }

        // 只有"正在打出的牌本身是虚无牌"才触发
        if (!card.Keywords.Contains(CardKeyword.Ethereal))
        {
            return 0;
        }

        var power = creature?.GetPower<SpiritOfAshPower>();

        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        icon = TryGetPowerIcon<SpiritOfAshPower>();
        return power.Amount * totalPlays;
    }

    /// <summary>
    /// 把一张牌在当前局面下会产生的全部格挡算出来。
    /// cardContributes 表示"这张牌本身是否带来格挡"（激发的冰球、死亡之舞也算）。
    /// 覆甲和冰球被动是持续状态，不算作这张牌的贡献。
    /// </summary>
    private static BlockPreviewValues BuildValues(CardModel card, Creature creature, out bool cardContributes)
    {
        // 先取"单次播放"的各段值。爆发（Burst）会让技能牌多播放几次，
        // 那部分要按结算顺序单独算（见 GetExtraPlayBlock），不能直接乘。
        int handBlockSingle = ComputeHandBlock(card, out int damageBlockSingle);

        // 勒紧（FastenPower）：卡面 PreviewValue 里已经含了它的加成，
        // 这里先把它扣出来，之后单独成段展示 —— 否则同一份格挡会被算两遍。
        int fastenPerPlay = GetFastenBlockPerPlay(card, creature, out var fastenIcon);
        handBlockSingle = Math.Max(0, handBlockSingle - fastenPerPlay);

        handBlockSingle += GetDiscardSlyBlock(card);
        int mirageBlockSingle = GetMirageBlock(card, out var mirageIcon);
        int escapePlanBlockSingle = GetEscapePlanBlock(card, out int escapePlanPercent);
        int danseBlockSingle = GetDanseMacabreBlock(card, creature, out var danseIcon);
        int rageBlockSingle = GetRageBlock(card, creature, out var rageIcon);
        int afterimageBlockSingle = GetAfterimageBlock(creature, out var afterimageIcon);
        int feelNoPainBlock = GetFeelNoPainBlock(card, creature, out var feelNoPainIcon);

        int fiendFireBlock = GetFiendFireBlock(card, creature, out var fiendFireIcon);
        int fiendFireReplayBlock = GetFiendFireReplayBlock(card, creature, out var fiendFireReplayIcon);
        int secondWindReplayBlock = GetSecondWindReplayBlock(card, creature, out var secondWindReplayIcon);
        int platingBlock = GetPlatingBlock(creature, out var platingIcon);

        // 播放次数分两层看：
        //   replayPlays = 重放 / 华彩 / 附魔（卡面上那条"重放 X 次"）
        //   totalPlays  = 再加上复制药水 / 爆发 / 回声形态 / 遗物等 ModifyCardPlayCount 来源
        // 额外的那几次状态会变（球被消耗、牌被烧掉、牌堆被抽走），交给 GetExtraPlayBlock 逐次补算。
        int replayCount = Math.Max(0, card.GetEnchantedReplayCount());
        int replayPlays = card is SecondWind ? 1 : 1 + replayCount;

        // 柔嫩（TenderPower）：每播放一次力量/敏捷各 -1，重放的后续播放格挡会跟着掉。
        // 没有柔嫩时 tenderStep == 0，下面的求和会退化成原来的 `× replayPlays`。
        int tenderStep = GetTenderDexLossStep(card, creature);
        int totalPlays = GetPlayCount(card, creature, out var playModifiers);
        int extraPlays = Math.Max(0, totalPlays - 1 - replayCount);

        // 这张牌自己会额外给的覆甲（永恒铠甲 / 中子神盾 / 石甲）：直接叠加到覆甲段。
        // 覆甲是 Counter 叠加，且在本回合末 BeforeSideTurnEndEarly 按当时的层数换格挡，
        // 所以「已有层数 + 这张牌给的层数」就是本回合末的总格挡。
        int cardPlatingBlock = GetCardPlatingBlock(card, totalPlays, out var cardPlatingIcon);
        platingBlock += cardPlatingBlock;
        platingIcon ??= cardPlatingIcon;

        // 勒紧：每次播放都会加一次，所以按总播放次数展开
        int fastenBlock = fastenPerPlay * totalPlays;

        int starPillarBlock = GetPillarOfCreationBlock(card, creature, totalPlays, out var starPillarIcon);

        // 灰烬之灵：拖的牌自己是虚无牌时，每次打出都给「层数」格挡
        int spiritOfAshBlock = GetSpiritOfAshBlock(card, creature, totalPlays, out var spiritOfAshIcon);

        int ostySummonBlock = GetOstySummonBlock(card, creature, totalPlays, out var ostySummonIcon);
        int ostyHealBlock = GetOstyHealBlock(card, creature, totalPlays, out var ostyHealIcon);

        // 奥斯提生命：它替玩家挨伤害，等于一层自带壁垒的护盾（背景状态，与打哪张牌无关）
        int ostyHpBlock = GetOstyHpBlock(creature, out var ostyHpIcon);

        // 牺牲会把骨手直接杀掉，打完这张牌那层「生命护盾」就不存在了，
        // 不能还挂在总值里误导玩家（它换来的是一次性的 3 倍最大生命格挡）。
        if (card is Sacrifice)
        {
            ostyHpBlock = 0;
            ostyHpIcon = null;
        }
        int deathDoorBlock = GetDeathDoorExtraBlock(card, out var deathDoorIcon);
        int doomCloakBlock = GetDoomCloakBlock(card, creature, totalPlays, out var doomCloakIcon);

        // ---------- 遗物 ----------
        var owner = creature.Player;

        int boneFluteBlock = GetBoneFluteBlock(card, owner, totalPlays, out var boneFluteIcon);
        int windDaughterBlock = GetWindDaughterBlock(card, owner, totalPlays, out var windDaughterIcon);
        int toughBandagesBlock = GetToughBandagesBlock(card, owner, totalPlays, out var toughBandagesIcon);

        // 4 个"计数器型"遗物：状态都可读，能算准
        int galacticDustBlock = GetGalacticDustBlock(card, creature, out var galacticDustIcon);
        int ornamentalFanBlock = GetOrnamentalFanBlock(card, creature, totalPlays, out var ornamentalFanIcon);
        int tuningForkBlock = GetTuningForkBlock(card, creature, totalPlays, out var tuningForkIcon);
        int intimidatingHelmetBlock = GetIntimidatingHelmetBlock(card, creature, totalPlays, out var intimidatingHelmetIcon);

        // 永冻冰晶（每场第一次能力牌）/ 君王矿石（每回合第一次生成牌）
        int permafrostBlock = GetPermafrostBlock(card, creature, out var permafrostIcon);
        int regaliteBlock = GetRegaliteBlock(card, owner, totalPlays, out var regaliteIcon);

        // 算盘：抽牌堆不够抽、必然洗牌时的一次格挡
        int abacusBlock = GetAbacusBlock(card, owner, totalPlays, out var abacusIcon);

        // 合股线（愚者）：箭被松脱时的格挡。只算拖攻击牌，且目标身上得刺着自己的箭
        int twineBlock = GetTwineBlock(card, owner, totalPlays, out var twineIcon);

        // 愚者旋律能力：歌之路（本回合首张啾牌）/ 摇篮曲（集齐三种旋律）
        int songPathBlock = GetSongPathBlock(card, creature, out var songPathIcon);
        int lullabyBlock = GetLullabyBlock(card, creature, out var lullabyIcon);

        // 万事无忧：每生成一张「悲伤」获得「层数」点格挡
        int allIsWellBlock = GetAllIsWellBlock(card, creature, totalPlays, out var allIsWellIcon);

        // 斗篷扣和波纹水盆都是"回合结束才结算"，只在玩家回合显示（同覆甲）
        bool playerTurn = IsPlayerTurn();

        int cloakClaspBlock = 0;
        Texture2D? cloakClaspIcon = null;
        int rippleBasinBlock = 0;
        Texture2D? rippleBasinIcon = null;

        if (playerTurn)
        {
            cloakClaspBlock = GetCloakClaspBlock(owner, out cloakClaspIcon);
            rippleBasinBlock = GetRippleBasinBlock(card, owner, out rippleBasinIcon);
        }

        GetInvinciblePreview(card.Owner, out int invincibleBlock, out int invinciblePercent, out var invincibleIcon);

        // 球被激发就会消耗，所以"剩余冰球被动"和"激发格挡"必须按总播放次数逐次推演。
        // 统一推演同时覆盖：生成新球（Channel）、被挤出的溢出激发、玩家主动激发。
        //
        // 单独 try/catch 是为了让异常能出现在日志里：以前它抛出去被上层吞掉，
        // 表现就是"面板直接 =0"，完全看不出原因（2026-11-23）。
        int evokeBlock = 0;
        int overflowBlock = 0;
        int frostBlock = 0;
        int goldCablesBlock = 0;
        Texture2D? evokeIcon = null;
        Texture2D? overflowIcon = null;
        Texture2D? frostIcon = null;
        Texture2D? goldCablesIcon = null;

        try
        {
            SimulateOrbs(creature, card, totalPlays,
                out evokeBlock, out overflowBlock, out frostBlock, out goldCablesBlock,
                out evokeIcon, out overflowIcon, out frostIcon, out goldCablesIcon);
        }
        catch (Exception e)
        {
            BlockPreviewLog.Perf($"[诊断] SimulateOrbs 异常: {e}");
        }

        // 临时诊断：把球推演结果直接打到 godot.log（不受"开启 mod 日志"影响），
        // 用来确认「拖生成冰球的牌时 frostBlock 到底算出来没有」（2026-11-23）。
        if (frostBlock > 0 || evokeBlock > 0 || overflowBlock > 0)
        {
            string orbDbg = $"球推演: frost={frostBlock} evoke={evokeBlock} overflow={overflowBlock}"
                            + $" plays={totalPlays} card={card?.GetType().Name ?? "null"}";

            if (orbDbg != _lastOrbDbg)
            {
                _lastOrbDbg = orbDbg;
                BlockPreviewLog.Perf($"[诊断] {orbDbg}");
            }
        }

        // 融入暗影（ShadowmeldPower）：本回合获得的格挡翻倍。
        // 卡面 BlockVar / CalculatedBlockVar 的 PreviewValue 已经过 Hook.ModifyBlock，
        // 里面已经含了这个乘数；只有本文件自己算的段需要手动乘。
        //
        // ⚠️ 2026-11-19 修正：原先认为「覆甲/冰球被动在回合结束时结算，那时 Shadowmeld 已移除」
        // 是**错的**。实际时序（CombatManager.TurnEnd）：
        //   EndPlayerTurnPhaseOne
        //     ├─ BeforeSideTurnEnd ─┬─ VeryEarly → 奥里哈刚判定
        //     │                     ├─ Early     → 覆甲
        //     │                     └─ End       → 斗篷扣 / 波纹水盆
        //     └─ DoTurnEnd         ─┬─ 冰球被动
        //                           └─ 虚无牌消耗 → 无惧疼痛
        //   EndPlayerTurnPhaseTwo
        //     └─ AfterSideTurnEnd  → ShadowmeldPower 在这里才移除
        // 也就是说上面**全部**都发生在 Shadowmeld 生效期内，都会被翻倍。
        // 而 ShadowmeldPower.ModifyBlockMultiplicative **不检查 ValueProp**，
        // 遗物的 Unpowered 格挡同样吃这个乘数（敏捷则不吃，它检查 props）。
        //
        // 还有一个容易漏的点：**正在拖的这张牌本身可能就是「融入暗影」**。
        // 它打出去后层数 +1，而覆甲 / 冰球被动 / 遗物段 / 余像都在它 OnPlay 之后结算，
        // 所以要按"提升后的倍率"预览（重放一次就 +1 层）。
        int shadowmeldExtraStacks = ShadowmeldStacksFromCard(card, totalPlays);
        int blockMultiplier = GetBlockGainMultiplier(creature, shadowmeldExtraStacks);

        int handBlock = SumBlockWithTenderDecay(handBlockSingle, replayPlays, tenderStep);
        int damageBlock = SumBlockWithTenderDecay(damageBlockSingle * blockMultiplier, replayPlays, tenderStep);
        int mirageBlock = mirageBlockSingle * replayPlays;
        int escapePlanBlock = SumBlockWithTenderDecay(escapePlanBlockSingle, replayPlays, tenderStep);
        int danseBlock = danseBlockSingle * blockMultiplier * replayPlays;
        int rageBlock = rageBlockSingle * blockMultiplier * replayPlays;
        int afterimageBlock = afterimageBlockSingle * blockMultiplier * replayPlays;

        evokeBlock *= blockMultiplier;

        // 溢出激发也是"获得格挡"，同样受融入暗影影响
        overflowBlock *= blockMultiplier;

        feelNoPainBlock *= blockMultiplier;
        fiendFireBlock *= blockMultiplier;
        fiendFireReplayBlock *= blockMultiplier;

        // 灰烬之灵是"打牌当场"给的格挡，同样受融入暗影影响
        spiritOfAshBlock *= blockMultiplier;

        // 勒紧是 ModifyBlockAdditive 加的格挡，会被融入暗影的 Multiplicative 一起乘
        fastenBlock *= blockMultiplier;

        int starChildBlock = GetChildOfTheStarsBlock(card, creature, totalPlays, out var starChildIcon);
        starChildBlock *= blockMultiplier;

        // 这几段是真的"获得格挡"，受融入暗影影响
        starPillarBlock *= blockMultiplier;
        deathDoorBlock *= blockMultiplier;
        doomCloakBlock *= blockMultiplier;
        invincibleBlock *= blockMultiplier;

        // 覆甲 / 冰球被动 / 全部 14 个遗物段：同样是"真的获得格挡"，
        // 且结算时机全部早于 ShadowmeldPower 的移除点（见上方时序说明），所以也要乘。
        // 2026-11-19 实测确认：融入暗影 1 层 + 覆甲，结束回合实际获得的就是 2 倍。
        platingBlock *= blockMultiplier;
        frostBlock *= blockMultiplier;

        // 镀金缆线让队首冰球的被动多触发一次 —— 也是"获得格挡"，受融入暗影影响
        goldCablesBlock *= blockMultiplier;

        // 算盘在打牌途中（洗牌时）给格挡，此刻融入暗影仍在生效，同样要乘
        abacusBlock *= blockMultiplier;
        boneFluteBlock *= blockMultiplier;
        cloakClaspBlock *= blockMultiplier;
        windDaughterBlock *= blockMultiplier;
        rippleBasinBlock *= blockMultiplier;
        toughBandagesBlock *= blockMultiplier;

        // 4 个"计数器型"遗物同样是真实获得格挡，受融入暗影影响
        galacticDustBlock *= blockMultiplier;
        ornamentalFanBlock *= blockMultiplier;
        tuningForkBlock *= blockMultiplier;
        intimidatingHelmetBlock *= blockMultiplier;
        permafrostBlock *= blockMultiplier;
        regaliteBlock *= blockMultiplier;

        // 愚者：歌之路 / 摇篮曲 都是 `ValueProp.Unpowered` 的能力格挡
        // （不吃敏捷、但吃融入暗影，与遗物段同规则）
        songPathBlock *= blockMultiplier;
        lullabyBlock *= blockMultiplier;

        // 万事无忧也是 `ValueProp.Unpowered`：不吃敏捷、吃融入暗影
        allIsWellBlock *= blockMultiplier;

        // 合股线在命中/击杀那一瞬间给格挡，仍在融入暗影生效期内
        twineBlock *= blockMultiplier;

        // 召唤量 / 骨手回复不是格挡，不乘融入暗影

        // 额外播放（爆发/复制药水/回声/遗物）那几次：第一次的收益已经在上面各段里，这里补剩下的。
        GetExtraPlayBlock(card, extraPlays, playModifiers,
            handBlockSingle, mirageBlockSingle,
            danseBlockSingle * blockMultiplier,
            rageBlockSingle * blockMultiplier,
            afterimageBlockSingle * blockMultiplier,
            escapePlanBlockSingle, escapePlanPercent, tenderStep,
            out int extraPlayBlock, out int extraPlayPercent, out var extraPlayIcon);

        // 不可格挡（NoBlockPower）：游戏只把**卡牌来源**的格挡乘 0，
        // 能力/遗物/药水（Unpowered）给的格挡不受影响。
        // 所以预览也只归零卡牌来源的段，覆甲/冰球/遗物段照常显示。
        if (HasNoBlockPower(creature))
        {
            ZeroCardSourcedBlock(ref handBlock, ref damageBlock, ref mirageBlock,
                ref escapePlanBlock, ref extraPlayBlock, ref fiendFireBlock,
                ref fiendFireReplayBlock, ref secondWindReplayBlock);
        }

        // 按模组配置的开关过滤各段：玩家觉得某段吵眼可以单独关掉
        handBlock = Show(BlockPreviewConfig.ShowHandBlock, handBlock);
        damageBlock = Show(BlockPreviewConfig.ShowDamageBlock, damageBlock);
        mirageBlock = Show(BlockPreviewConfig.ShowMirage, mirageBlock);
        escapePlanBlock = Show(BlockPreviewConfig.ShowEscapePlan, escapePlanBlock);
        extraPlayBlock = Show(BlockPreviewConfig.ShowExtraPlay, extraPlayBlock);
        starChildBlock = Show(BlockPreviewConfig.ShowStarChild, starChildBlock);
        starPillarBlock = Show(BlockPreviewConfig.ShowStarPillar, starPillarBlock);
        ostySummonBlock = Show(BlockPreviewConfig.ShowOstySummon, ostySummonBlock);
        ostyHealBlock = Show(BlockPreviewConfig.ShowOstyHeal, ostyHealBlock);
        ostyHpBlock = Show(BlockPreviewConfig.ShowOstyHp, ostyHpBlock);
        deathDoorBlock = Show(BlockPreviewConfig.ShowDeathDoor, deathDoorBlock);
        doomCloakBlock = Show(BlockPreviewConfig.ShowDoomCloak, doomCloakBlock);
        invincibleBlock = Show(BlockPreviewConfig.ShowInvincible, invincibleBlock);
        platingBlock = Show(BlockPreviewConfig.ShowPlating, platingBlock);
        cardPlatingBlock = Show(BlockPreviewConfig.ShowPlating, cardPlatingBlock);
        frostBlock = Show(BlockPreviewConfig.ShowFrost, frostBlock);
        evokeBlock = Show(BlockPreviewConfig.ShowEvoke, evokeBlock);
        overflowBlock = Show(BlockPreviewConfig.ShowOverflow, overflowBlock);
        danseBlock = Show(BlockPreviewConfig.ShowDanse, danseBlock);
        rageBlock = Show(BlockPreviewConfig.ShowRage, rageBlock);
        afterimageBlock = Show(BlockPreviewConfig.ShowAfterimage, afterimageBlock);
        feelNoPainBlock = Show(BlockPreviewConfig.ShowFeelNoPain, feelNoPainBlock);
        spiritOfAshBlock = Show(BlockPreviewConfig.ShowSpiritOfAsh, spiritOfAshBlock);
        fastenBlock = Show(BlockPreviewConfig.ShowFasten, fastenBlock);
        fiendFireBlock = Show(BlockPreviewConfig.ShowFiendFire, fiendFireBlock);
        fiendFireReplayBlock = Show(BlockPreviewConfig.ShowFiendFire, fiendFireReplayBlock);
        secondWindReplayBlock = Show(BlockPreviewConfig.ShowSecondWindReplay, secondWindReplayBlock);

        // 遗物段开关
        boneFluteBlock = Show(BlockPreviewConfig.ShowBoneFlute, boneFluteBlock);
        cloakClaspBlock = Show(BlockPreviewConfig.ShowCloakClasp, cloakClaspBlock);
        windDaughterBlock = Show(BlockPreviewConfig.ShowWindDaughter, windDaughterBlock);
        rippleBasinBlock = Show(BlockPreviewConfig.ShowRippleBasin, rippleBasinBlock);
        toughBandagesBlock = Show(BlockPreviewConfig.ShowToughBandages, toughBandagesBlock);
        galacticDustBlock = Show(BlockPreviewConfig.ShowGalacticDust, galacticDustBlock);
        ornamentalFanBlock = Show(BlockPreviewConfig.ShowOrnamentalFan, ornamentalFanBlock);
        tuningForkBlock = Show(BlockPreviewConfig.ShowTuningFork, tuningForkBlock);
        intimidatingHelmetBlock = Show(BlockPreviewConfig.ShowIntimidatingHelmet, intimidatingHelmetBlock);
        permafrostBlock = Show(BlockPreviewConfig.ShowPermafrost, permafrostBlock);
        regaliteBlock = Show(BlockPreviewConfig.ShowRegalite, regaliteBlock);
        goldCablesBlock = Show(BlockPreviewConfig.ShowGoldPlatedCables, goldCablesBlock);
        abacusBlock = Show(BlockPreviewConfig.ShowAbacus, abacusBlock);

        // 愚者段开关
        twineBlock = Show(BlockPreviewConfig.ShowTwine, twineBlock);
        songPathBlock = Show(BlockPreviewConfig.ShowSongPath, songPathBlock);
        lullabyBlock = Show(BlockPreviewConfig.ShowLullaby, lullabyBlock);
        allIsWellBlock = Show(BlockPreviewConfig.ShowAllIsWell, allIsWellBlock);

        // 药水是**固定值**，不属于"打这张牌算出来的期望"，
        // 所以它单独占一行（第三行），不进 uncertainTotal / =Y?。
        // 只受 ShowPotion 控制，不跟估算行总闸联动。
        var potionPreviews = BlockPreviewConfig.ShowPotion
            ? GetPotionPreviews(owner, shadowmeldExtraStacks)
            : new List<PotionPreview>();

        // 估算行总开关：关掉后第二行整条消失。
        // 只归零"专属第二行"的项 —— 逃脱计划是**动态归属**的：
        // 命中率 100% 时它已经是第一行的确定值，不该跟着被关掉。
        if (!BlockPreviewConfig.ShowEstimateLine)
        {
            extraPlayBlock = 0;
            invincibleBlock = 0;
            fiendFireReplayBlock = 0;
            secondWindReplayBlock = 0;

            // 命中率 < 100% 的逃脱计划只存在于第二行，第二行一关就没地方显示了
            if (escapePlanPercent < 100)
            {
                escapePlanBlock = 0;
            }
        }

        cardContributes = handBlock > 0 || damageBlock > 0 || mirageBlock > 0
                          || escapePlanBlock > 0 || extraPlayBlock > 0
                          || starChildBlock > 0 || starPillarBlock > 0
                          || ostySummonBlock > 0 || ostyHealBlock > 0
                          || deathDoorBlock > 0 || doomCloakBlock > 0
                          || invincibleBlock > 0
                          || cardPlatingBlock > 0
                          // ⚠️ 冰球被动必须算进来：只产冰球的牌（长枪 / 寒流 / 冰霜新星…）
                          // 没有手牌格挡，漏掉这条会被判成"无收益"，面板直接不显示（2026-11-23）。
                          || frostBlock > 0
                          || evokeBlock > 0 || overflowBlock > 0 || danseBlock > 0 || rageBlock > 0
                          || afterimageBlock > 0 || fiendFireBlock > 0
                          || spiritOfAshBlock > 0 || fastenBlock > 0
                          || fiendFireReplayBlock > 0 || secondWindReplayBlock > 0;

        // 第一行只放「确定」的段。
        // 第二行（估算行）收容所有**不确定**的项：
        //   带 ? 的估算值        → FiendFireReplayBlock / SecondWindReplayBlock
        //   带 % 的概率值        → EscapePlanBlock / InvincibleBlock / ExtraPlayBlock
        // 这些的数值在总护盾里仍按"满额/命中"计入，只是不写进第一行的准数。
        // 逃脱计划要分情况：
        //   命中率 100%（抽牌堆全是技能牌、或抽牌堆空但弃牌堆全是技能牌）
        //   → 这一抽必定给格挡，属于**确定收益**，归第一行的准数
        //   命中率 < 100% → 吃不吃得到看运气，算不确定项进第二行
        bool escapePlanCertain = escapePlanBlock > 0 && escapePlanPercent >= 100;

        // 奥里哈刚（Orichalcum）在回合结束的最早期判断 `Creature.Block > 0`，
        // 时机**早于**回合结束阶段才结算的那些效果。
        //
        // 所以"已有格挡"= 当前格挡 + 打这张牌**立即**拿到的一切格挡，包括
        // 间接配合的收益（死亡之舞、狂怒、余像、奇巧弃牌、遗物段…）——
        // 按需求，这些统统算"有格挡了"。
        //
        // 无惧疼痛要拆开用：只有"打这张牌立即烧掉的牌"才算；
        // 「手牌里的虚无牌」是回合结束之后才消耗的，赶不上这次判断（见辅助函数注释）。
        // 其余排除项：覆甲 / 冰球被动 / 所向无敌 / 斗篷扣 / 波纹水盆 / 奥里哈刚自己。
        int feelNoPainImmediate = GetFeelNoPainImmediateBlock(
            card, creature, feelNoPainBlock, blockMultiplier);

        int immediateBlock = creature.Block + handBlock + damageBlock + mirageBlock
                             + (escapePlanCertain ? escapePlanBlock : 0)
                             + extraPlayBlock + starChildBlock + starPillarBlock
                             + ostySummonBlock + ostyHealBlock
                             + deathDoorBlock + doomCloakBlock
                             + evokeBlock + overflowBlock + danseBlock + rageBlock + afterimageBlock
                             + fiendFireBlock + feelNoPainImmediate
                             + boneFluteBlock + windDaughterBlock + toughBandagesBlock;

        int orichalcumBlock = 0;
        Texture2D? orichalcumIcon = null;
        int fakeOrichalcumBlock = 0;
        Texture2D? fakeOrichalcumIcon = null;

        if (playerTurn)
        {
            // 真品 6 点 / 假商人赝品 3 点，逻辑一样，各算一次
            orichalcumBlock = GetOrichalcumLikeBlock<Orichalcum>(owner, immediateBlock, out orichalcumIcon);
            fakeOrichalcumBlock = GetOrichalcumLikeBlock<FakeOrichalcum>(owner, immediateBlock, out fakeOrichalcumIcon);

            // 它的 GainBlock 在 BeforeSideTurnEnd 里执行（判定在 VeryEarly），
            // 早于 Shadowmeld 移除，所以同样要乘融入暗影
            orichalcumBlock *= blockMultiplier;
            fakeOrichalcumBlock *= blockMultiplier;

            orichalcumBlock = Show(BlockPreviewConfig.ShowOrichalcum, orichalcumBlock);
            fakeOrichalcumBlock = Show(BlockPreviewConfig.ShowOrichalcum, fakeOrichalcumBlock);
        }

        // 遗物段也要算进"这张牌是否带来格挡"的判定
        cardContributes = cardContributes
                          || boneFluteBlock > 0 || cloakClaspBlock > 0
                          || windDaughterBlock > 0 || orichalcumBlock > 0
                          || fakeOrichalcumBlock > 0
                          || rippleBasinBlock > 0 || toughBandagesBlock > 0
                          || galacticDustBlock > 0 || ornamentalFanBlock > 0
                          || tuningForkBlock > 0 || intimidatingHelmetBlock > 0
                          || permafrostBlock > 0 || regaliteBlock > 0
                          || goldCablesBlock > 0 || abacusBlock > 0
                          || twineBlock > 0 || songPathBlock > 0 || lullabyBlock > 0
                          || allIsWellBlock > 0;

        // 所向无敌和逃脱计划一样是**概率性**收益：只有 100% 才算确定值进第一行。
        bool invincibleCertain = invincibleBlock > 0 && invinciblePercent >= 100;

        int certainTotal = creature.Block + handBlock + damageBlock + mirageBlock
                           + (escapePlanCertain ? escapePlanBlock : 0)
                           + (invincibleCertain ? invincibleBlock : 0)
                           + starChildBlock + starPillarBlock
                           + ostySummonBlock + ostyHealBlock + ostyHpBlock
                           + deathDoorBlock + doomCloakBlock
                           + platingBlock
                           + frostBlock + evokeBlock + overflowBlock + danseBlock + rageBlock
                           + afterimageBlock + feelNoPainBlock + fiendFireBlock + spiritOfAshBlock + fastenBlock
                           + boneFluteBlock + cloakClaspBlock + windDaughterBlock
                           + galacticDustBlock + ornamentalFanBlock + tuningForkBlock + intimidatingHelmetBlock
                           + permafrostBlock + regaliteBlock + goldCablesBlock + abacusBlock
                           + twineBlock + songPathBlock + lullabyBlock + allIsWellBlock
                           + orichalcumBlock + fakeOrichalcumBlock
                           + rippleBasinBlock + toughBandagesBlock;

        int uncertainTotal = (escapePlanCertain ? 0 : escapePlanBlock)
                             + extraPlayBlock + (invincibleCertain ? 0 : invincibleBlock)
                             + fiendFireReplayBlock + secondWindReplayBlock;

        int estimatedTotal = certainTotal + uncertainTotal;

        // 真实格挡：剔掉奥斯提那三项非格挡收益（召唤量提升上限、回复是治疗、生命是承伤）。
        // 药水行只能基于它算，否则"召唤 5"会被当成格挡又被药水当成基数。
        int realBlock = Math.Max(0,
            certainTotal - ostySummonBlock - ostyHealBlock - ostyHpBlock);

        return new BlockPreviewValues
        {
            HandBlock = handBlock,
            DamageBlock = damageBlock,
            PoisonBlock = mirageBlock,
            EscapePlanBlock = escapePlanBlock,
            EscapePlanPercent = escapePlanBlock > 0 ? escapePlanPercent : -1,
            ExtraPlayBlock = extraPlayBlock,
            ExtraPlayPercent = extraPlayBlock > 0 ? extraPlayPercent : -1,
            ExtraPlayIcon = extraPlayBlock > 0 ? extraPlayIcon : null,
            StarChildBlock = starChildBlock,
            StarChildIcon = starChildBlock > 0 ? starChildIcon : null,
            StarPillarBlock = starPillarBlock,
            StarPillarIcon = starPillarBlock > 0 ? starPillarIcon : null,
            OstySummonBlock = ostySummonBlock,
            OstySummonIcon = ostySummonBlock > 0 ? ostySummonIcon : null,
            OstyHealBlock = ostyHealBlock,
            OstyHealIcon = ostyHealBlock > 0 ? ostyHealIcon : null,
            OstyHpBlock = ostyHpBlock,
            OstyHpIcon = ostyHpBlock > 0 ? ostyHpIcon : null,
            DeathDoorBlock = deathDoorBlock,
            DeathDoorIcon = deathDoorBlock > 0 ? deathDoorIcon : null,
            DoomCloakBlock = doomCloakBlock,
            DoomCloakIcon = doomCloakBlock > 0 ? doomCloakIcon : null,
            InvincibleBlock = invincibleBlock,
            InvinciblePercent = invincibleBlock > 0 ? invinciblePercent : -1,
            InvincibleIcon = invincibleBlock > 0 ? invincibleIcon : null,
            PlatingBlock = platingBlock,
            FrostBlock = frostBlock,
            EvokeBlock = evokeBlock,
            OverflowBlock = overflowBlock,
            OverflowIcon = overflowBlock > 0 ? overflowIcon : null,
            DanseBlock = danseBlock,
            RageBlock = rageBlock,
            AfterimageBlock = afterimageBlock,
            FeelNoPainBlock = feelNoPainBlock,
            SpiritOfAshBlock = spiritOfAshBlock,
            SpiritOfAshIcon = spiritOfAshBlock > 0 ? spiritOfAshIcon : null,
            FastenBlock = fastenBlock,
            FastenIcon = fastenBlock > 0 ? fastenIcon : null,
            FiendFireBlock = fiendFireBlock,
            FiendFireReplayBlock = fiendFireReplayBlock,
            SecondWindReplayBlock = secondWindReplayBlock,
            SecondWindReplayIcon = secondWindReplayIcon,
            BoneFluteBlock = boneFluteBlock,
            BoneFluteIcon = boneFluteBlock > 0 ? boneFluteIcon : null,
            CloakClaspBlock = cloakClaspBlock,
            CloakClaspIcon = cloakClaspBlock > 0 ? cloakClaspIcon : null,
            WindDaughterBlock = windDaughterBlock,
            WindDaughterIcon = windDaughterBlock > 0 ? windDaughterIcon : null,
            OrichalcumBlock = orichalcumBlock,
            OrichalcumIcon = orichalcumBlock > 0 ? orichalcumIcon : null,
            FakeOrichalcumBlock = fakeOrichalcumBlock,
            FakeOrichalcumIcon = fakeOrichalcumBlock > 0 ? fakeOrichalcumIcon : null,
            RippleBasinBlock = rippleBasinBlock,
            RippleBasinIcon = rippleBasinBlock > 0 ? rippleBasinIcon : null,
            ToughBandagesBlock = toughBandagesBlock,
            ToughBandagesIcon = toughBandagesBlock > 0 ? toughBandagesIcon : null,
            GalacticDustBlock = galacticDustBlock,
            GalacticDustIcon = galacticDustBlock > 0 ? galacticDustIcon : null,
            OrnamentalFanBlock = ornamentalFanBlock,
            OrnamentalFanIcon = ornamentalFanBlock > 0 ? ornamentalFanIcon : null,
            TuningForkBlock = tuningForkBlock,
            TuningForkIcon = tuningForkBlock > 0 ? tuningForkIcon : null,
            IntimidatingHelmetBlock = intimidatingHelmetBlock,
            IntimidatingHelmetIcon = intimidatingHelmetBlock > 0 ? intimidatingHelmetIcon : null,
            PermafrostBlock = permafrostBlock,
            PermafrostIcon = permafrostBlock > 0 ? permafrostIcon : null,
            RegaliteBlock = regaliteBlock,
            RegaliteIcon = regaliteBlock > 0 ? regaliteIcon : null,
            GoldCablesBlock = goldCablesBlock,
            GoldCablesIcon = goldCablesBlock > 0 ? goldCablesIcon : null,
            AbacusBlock = abacusBlock,
            AbacusIcon = abacusBlock > 0 ? abacusIcon : null,
            TwineBlock = twineBlock,
            TwineIcon = twineBlock > 0 ? twineIcon : null,
            SongPathBlock = songPathBlock,
            SongPathIcon = songPathBlock > 0 ? songPathIcon : null,
            LullabyBlock = lullabyBlock,
            LullabyIcon = lullabyBlock > 0 ? lullabyIcon : null,
            AllIsWellBlock = allIsWellBlock,
            AllIsWellIcon = allIsWellBlock > 0 ? allIsWellIcon : null,
            TotalBlock = certainTotal,
            EstimatedTotalBlock = estimatedTotal,
            RealBlock = realBlock,
            Potions = potionPreviews,
            PlatingIcon = platingIcon,
            FrostIcon = frostIcon,
            EvokeIcon = evokeIcon,
            DanseIcon = danseIcon,
            RageIcon = rageIcon,
            AfterimageIcon = afterimageIcon,
            FeelNoPainIcon = feelNoPainIcon,
            FiendFireIcon = fiendFireIcon,
            FiendFireReplayIcon = fiendFireReplayIcon,
            DamageIcon = damageBlock > 0 ? GetStrengthIcon() : null,
            PoisonIcon = mirageBlock > 0 ? mirageIcon : null,
        };
    }

    /// <summary>
    /// 融入暗影（ShadowmeldPower）：本回合获得的格挡 ×2^层数。
    ///
    /// 注意卡面 BlockVar / CalculatedBlockVar 的 PreviewValue 已经过 Hook.ModifyBlock，
    /// 里面已经含了这个乘数，所以只有"自己算"的段需要手动乘。
    ///
    /// <para>
    /// <paramref name="extraStacks"/>：正在拖的这张牌**打出去之后**会额外增加的层数。
    /// 典型场景就是拖「融入暗影」本身——它的 OnPlay 会 Apply 1 层，
    /// 而覆甲 / 冰球被动 / 遗物段 / 余像这些都在 OnPlay **之后**才结算，
    /// 所以它们应当按"提升后的倍率"预览，否则玩家看到的 `=X` 会偏小一倍。
    /// 重放 / 爆发等会让它播多次，每次都 +1 层，所以传的是总播放次数。
    /// </para>
    /// </summary>
    private static int GetBlockGainMultiplier(Creature? creature, int extraStacks = 0)
    {
        var power = creature?.GetPower<ShadowmeldPower>();
        int stacks = (power?.Amount ?? 0) + Math.Max(0, extraStacks);

        if (stacks <= 0)
        {
            return 1;
        }

        int multiplier = 1;

        for (int i = 0; i < stacks && multiplier <= 1024; i++)
        {
            multiplier *= 2;
        }

        return multiplier;
    }

    /// <summary>
    /// 正在拖的这张牌打出去之后会额外叠几层融入暗影。
    /// 只有「融入暗影」自己会改变层数（未升级 / 升级都是 +1，升级只降费），
    /// 每播放一次叠一层（重放 / 爆发 / 复制 / 回声都算）。
    /// </summary>
    private static int ShadowmeldStacksFromCard(CardModel? card, int totalPlays)
    {
        return card is Shadowmeld ? Math.Max(0, totalPlays) : 0;
    }

    /// <summary>
    /// 群星之子（ChildOfTheStarsPower）：每消耗 1 颗星获得「层数」点格挡。
    ///
    /// 星费取 GetStarCostWithModifiers()：
    ///   普通星费牌 → 含修正后的星费；X 星费牌 → 当前星数（会把星花光）
    /// 星不够时只能花掉现有的，所以：
    ///   实际消耗 = min(单次星费 × 总播放次数, 当前星数)
    ///   格挡     = 实际消耗 × 层数
    /// </summary>
    private static int GetChildOfTheStarsBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<ChildOfTheStarsPower>();

        if (power == null || power.Amount <= 0 || totalPlays <= 0)
        {
            return 0;
        }

        int starsPerPlay;

        try
        {
            starsPerPlay = Math.Max(0, card.GetStarCostWithModifiers());
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 计算星费失败: {e.Message}");
            return 0;
        }

        if (starsPerPlay <= 0)
        {
            return 0;
        }

        int totalStars = creature?.Player?.PlayerCombatState?.Stars ?? 0;

        if (totalStars <= 0)
        {
            return 0;
        }

        int spent = Math.Min(starsPerPlay * totalPlays, totalStars);

        if (spent <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return spent * power.Amount;
    }

    /// <summary>
    /// 创世之柱（PillarOfCreationPower）：每生成 1 张卡牌获得「层数」点格挡。
    ///
    /// 游戏里它挂的是 AfterCardGeneratedForCombat，而 CardPileCmd.AddGeneratedCardsToCombat
    /// 会对每一张加进来的牌各触发一次，所以只要数出这张牌总共会生成几张就行。
    /// </summary>
    private static int GetPillarOfCreationBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<PillarOfCreationPower>();

        if (power == null || power.Amount <= 0 || totalPlays <= 0)
        {
            return 0;
        }

        int generated = EstimateGeneratedCardsTotal(card, totalPlays);

        if (generated <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return generated * power.Amount;
    }

    /// <summary>
    /// 这张牌按 totalPlays 次播放一共会生成几张卡牌。
    ///
    /// 分三类处理：
    ///   1) 补满手牌类（迫降 / 恋人）：第一轮就填满，之后每轮只补"打出后腾出的那 1 格"
    ///      → 总量受手牌上限封顶，不随播放次数线性增长
    ///   2) 烧光手牌类（添柴）：每轮先烧空再生成同样张数，不占用手牌余量
    ///      → 直接乘播放次数
    ///   3) 其他：每轮固定张数。生成到「手牌」的部分受上限约束，
    ///      生成到抽牌堆 / 弃牌堆的部分不受约束
    /// </summary>
    private static int EstimateGeneratedCardsTotal(CardModel card, int totalPlays)
    {
        var owner = card.Owner;

        if (owner == null)
        {
            return 0;
        }

        int handCount = PileType.Hand.GetPile(owner).Cards.Count;

        // 打出后这张牌离开手牌
        int handAfterPlay = Math.Max(0, handCount - 1);
        int capacity = Math.Max(0, MaxHandSize - handAfterPlay);

        // 1) 补满手牌：填满之后，后面几轮打出去又腾出 1 格、再补 1 张，
        //    所以第一轮就把 capacity 用光，总量直接取 capacity
#if STS2_FOOL
        if (card is CrashLanding || card is TheLovers)
#else
        if (card is CrashLanding)
#endif
        {
            return capacity;
        }

        // 2) 烧光手牌：添柴先把手牌烧空再生成同样张数，不占用手牌余量
        if (card is Stoke)
        {
            return handAfterPlay * totalPlays;
        }

        // 3) 其余牌按「每次播放生成几张」计算
        var spec = GetGeneratedCardsPerPlay(card);

        if (spec.Count <= 0)
        {
            return 0;
        }

        int total = spec.Count * totalPlays;

        // ⚠️ **不要按手牌上限封顶**：
        // 手牌满时 `CardPileCmd.Add` 会把新牌**改放进弃牌堆**
        // （`CardPileCmd.cs:450-454`，同时提示 "HAND_FULL"），卡牌照样进战斗；
        // 而 `AfterCardGeneratedForCombat` 是在 `Add` **之后**无条件调用的
        // （`CardPileCmd.cs:272-278`），所以满手也照样触发创世之柱 / 万事无忧。
        // 原来的 `Math.Min(total, capacity)` 会少算。
        return total;
    }

    /// <summary>每播放一次生成几张卡牌，以及是否生成到手牌。</summary>
    private readonly record struct GeneratedCardsSpec(int Count, bool ToHand);

    /// <summary>
    /// 这张牌每播放一次生成几张卡牌。
    ///
    /// 数据来自逐牌核对游戏反编译实现，一共覆盖 48 张会生成卡牌的牌。
    /// 张数只依赖卡面变量的牌查表；依赖局面（X 费、补满手牌、烧光手牌）的
    /// 在 EstimateGeneratedCardsTotal 或下面的分支里单独处理。
    /// 没收录的牌一律返回 0 —— 宁可不显示，也不要多算而误导玩家。
    /// </summary>
    private static GeneratedCardsSpec GetGeneratedCardsPerPlay(CardModel card)
    {
        string name = card.GetType().Name;

        // 张数 = DynamicVars.Cards（会随升级变化）
        if (_generatedByCardsVar.TryGetValue(name, out bool cardsToHand))
        {
            return new GeneratedCardsSpec(ReadCardsVar(card), cardsToHand);
        }

#if STS2_FOOL
        // 显化 BrightenUp：两个具名变量相加（悲伤 Sadness + 喜悦 Joy）
        if (card is BrightenUp)
        {
            return new GeneratedCardsSpec(ReadDynamicVar(card, "Sadness") + ReadDynamicVar(card, "Joy"), true);
        }
#endif

        // 遗物锤 HeirloomHammer：张数读 Repeat 变量
        if (card is HeirloomHammer)
        {
            return new GeneratedCardsSpec(ReadDynamicVar(card, "Repeat"), true);
        }

#if STS2_FOOL
        // 苦痛自贬 SelfDeprecate：自己 1 张悲伤 + 每位"其他"队友 Joy 张
        // GetTeammatesOf 包含自己，所以额外队友数 = 队友数 - 1，单人时为 0
        if (card is SelfDeprecate)
        {
            int otherTeammates = Math.Max(0, CountPlayerTeammates(card) - 1);
            int extra = otherTeammates * ReadDynamicVar(card, "Joy");
            return new GeneratedCardsSpec(ReadDynamicVar(card, "Sadness") + extra, true);
        }
#endif

        // 瞥见彼岸 GlimpseBeyond：每位队友各生成 Cards 张灵魂，放进抽牌堆
        if (card is GlimpseBeyond)
        {
            return new GeneratedCardsSpec(ReadCardsVar(card) * CountPlayerTeammates(card), false);
        }

        // 暴怒 Outrage：给每位队友（含自己）各克隆 1 张本牌，放进弃牌堆
        if (card is Outrage)
        {
            return new GeneratedCardsSpec(CountPlayerTeammates(card), false);
        }

        // 星象仪 Quasar：从 3 张候选里选 1 张，可跳过 → 按选 1 张算
        if (card is Quasar)
        {
            return new GeneratedCardsSpec(1, true);
        }

        // 疯狂科学 MadScience：只有 Chaos 变体才生成 1 张
        if (card is MadScience)
        {
            return new GeneratedCardsSpec(1, true);
        }

        // 齐奏 Dirge：X 费，生成 X 张灵魂放进抽牌堆
        if (card is Dirge)
        {
            return new GeneratedCardsSpec(EstimateXValue(card), false);
        }

        // 张数写死、且不依赖局面的牌
        if (_generatedFixed.TryGetValue(name, out var spec))
        {
            return spec;
        }

        return new GeneratedCardsSpec(0, false);
    }

    /// <summary>
    /// 「每播放一次固定生成 N 张」的牌。键是卡牌类名（MegaCrit.Sts2.Core.Models.Cards 下的类）。
    /// 第二项表示是否生成到手牌 —— 只有生成到手牌的部分才需要受手牌上限约束。
    /// </summary>
    private static readonly Dictionary<string, GeneratedCardsSpec> _generatedFixed = new()
    {
        // ---- 固定 1 张 ----
        ["Abundance"] = new(1, true),
        ["AdaptiveStrike"] = new(1, false),      // 复制自身进弃牌堆
        ["Anger"] = new(1, false),               // 复制自身进弃牌堆
        ["BlotOutTheSky"] = new(1, true),
        ["BoostAway"] = new(1, false),           // 眩晕进弃牌堆
        ["CollisionCourse"] = new(1, true),      // 碎屑
        ["Discovery"] = new(1, true),
        ["Distraction"] = new(1, true),
        ["Florescence"] = new(1, true),
        ["GunkUp"] = new(1, false),              // 黏液进弃牌堆
        ["InfernalBlade"] = new(1, true),
        ["Largesse"] = new(1, true),
        ["ManifestAuthority"] = new(1, true),
        ["Overclock"] = new(1, false),           // 灼烧进弃牌堆
        ["Rummage"] = new(1, true),              // 悲伤
        ["Splash"] = new(1, true),
        ["Turbo"] = new(1, false),               // 虚空进弃牌堆
        ["Undeath"] = new(1, false),             // 复制自身进弃牌堆
        ["Vitriol"] = new(1, true),              // 悲伤
        ["WhiteNoise"] = new(1, true),

        // ---- 固定多张 ----
        ["FightThrough"] = new(2, false),        // 2 张伤口进弃牌堆
        ["Severance"] = new(3, false),           // 3 张灵魂：抽牌堆 / 弃牌堆 / 手牌各 1
    };

    /// <summary>
    /// 张数读 DynamicVars.Cards 的牌。值表示是否生成到手牌。
    /// </summary>
    private static readonly Dictionary<string, bool> _generatedByCardsVar = new()
    {
        ["BundleOfJoy"] = true,
        ["CaptureSpirit"] = false,       // 灵魂进抽牌堆
        ["DualWield"] = true,
        ["GraveWarden"] = false,         // 灵魂进抽牌堆
        ["JackOfAllTrades"] = true,
        ["Jackpot"] = true,
        ["Jest"] = true,
        ["Metamorphosis"] = false,       // 进抽牌堆
        ["MightyShield"] = true,         // 眩晕
        ["MightyWeapon"] = true,         // 眩晕
        ["Reave"] = false,               // 灵魂进抽牌堆
        ["SorrowfulStrike"] = true,      // 悲伤
        ["TheHierophant"] = true,
    };

    /// <summary>读 DynamicVars.Cards 的整数值（CardsVar 会随升级变化）。</summary>
    private static int ReadCardsVar(CardModel card)
    {
        if (card.DynamicVars.TryGetValue("Cards", out var cardsVar) && cardsVar is CardsVar cards)
        {
            return Math.Max(0, cards.IntValue);
        }

        return 0;
    }

    /// <summary>读一个具名 DynamicVar 的整数值，取不到返回 0。</summary>
    private static int ReadDynamicVar(CardModel card, string key)
    {
        if (card.DynamicVars.TryGetValue(key, out var dynamicVar) && dynamicVar != null)
        {
            return Math.Max(0, dynamicVar.IntValue);
        }

        return 0;
    }

    /// <summary>
    /// X 费牌的 X 值按当前能量预估。
    /// CapturedXValue 只在真正打出时才赋值，预览阶段读到的是上一局的残留值。
    /// </summary>
    private static int EstimateXValue(CardModel card)
    {
        if (!card.EnergyCost.CostsX)
        {
            return 0;
        }

        return Math.Max(0, card.Owner?.PlayerCombatState?.Energy ?? 0);
    }

    /// <summary>玩家一侧的存活 creature 数（GetTeammatesOf 包含自己，单人局恒为 1）。</summary>
    private static int CountPlayerTeammates(CardModel card)
    {
        try
        {
            var combatState = card.CombatState;

            if (combatState == null || card.Owner == null)
            {
                return 1;
            }

            int count = 0;

            foreach (var teammate in combatState.GetTeammatesOf(card.Owner.Creature))
            {
                if (teammate.IsAlive)
                {
                    count++;
                }
            }

            return Math.Max(1, count);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计队友数失败: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 召唤量：显示成"这回合会召唤多少"的等效护盾，图标用「下回合召唤」状态。
    ///   1) 牌自带 SummonVar（护卫 / 拉仇恨 / 增生 等）
    ///   2) 吞噬生命（DevourLifePower）：手牌里每张灵魂被打出时召唤「层数」次
    /// 牺牲（Sacrifice）会直接杀死骨手，不预览。
    /// </summary>
    private static int GetOstySummonBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (totalPlays <= 0)
        {
            return 0;
        }

        int total = 0;

        if (card is not Sacrifice
            && card.DynamicVars.TryGetValue("Summon", out var summonVar)
            && summonVar is SummonVar summon)
        {
            int perPlay = Math.Max(0, summon.IntValue);
            int plays = totalPlays;

            // X 费牌（挽歌 `Dirge`）：`OnPlay` 里是 `for (i < X) Summon(Summon.BaseValue)`，
            // 所以总量 = 召唤量 × X，只算一次会少算 X 倍。
            // 重放时能量已在第一次就耗尽了（第二次 X=0），因此 X 费只按第一次播放算。
            if (card.EnergyCost.CostsX)
            {
                perPlay *= EstimateXValue(card);
                plays = 1;
            }

            total += perPlay * plays;
        }

        var devour = creature?.GetPower<DevourLifePower>();

        if (devour != null && devour.Amount > 0)
        {
            int souls = 0;
            var player = card.Owner;

            if (player != null)
            {
                foreach (var handCard in PileType.Hand.GetPile(player).Cards)
                {
                    if (handCard is Soul)
                    {
                        souls++;
                    }
                }
            }

            total += souls * devour.Amount;
        }

        // 紧追不放（SicEmPower）：奥斯提攻击**带有该状态的敌人**时，
        // 每次伤害都会触发一次 `OstyCmd.Summon(层数)`（`SicEmPower.AfterDamageGiven`）。
        // 状态是 `InstancedPerApplier`，只认玩家自己挂的那一份。
        // 注意它和牌自带的 SummonVar 是**叠加**关系（打「紧追不放」本身也会先吃到旧层数）。
        if (card.Tags.Contains(CardTag.OstyAttack))
        {
            var sicEmTarget = GetHoverTarget(card);

            if (sicEmTarget != null)
            {
                int stacks = 0;

                foreach (var power in sicEmTarget.GetPowerInstances<SicEmPower>())
                {
                    if (power.Applier == card.Owner?.Creature)
                    {
                        stacks += power.Amount;
                    }
                }

                if (stacks > 0)
                {
                    // 多段奥斯提攻击每次伤害都触发
                    total += stacks * GetCardHitCount(card) * totalPlays;
                }
            }
        }

        if (total <= 0)
        {
            return 0;
        }

        // 图标用「下回合召唤」（SummonNextTurnPower）——
        // 它才是"接下来会召唤多少"的状态载体，比"为你而死"更贴切。
        icon = TryGetPowerIcon<SummonNextTurnPower>();
        return total;
    }

    /// <summary>
    /// 奥斯提（骨手）的当前生命值，折算成"等效护盾"。
    ///
    /// 它会替玩家挨伤害：`DieForYouPower.ModifyUnblockedDamageTarget` 把打向玩家的
    /// **未格挡伤害**改指向 Osty，所以 Osty 的生命（+ 它自己的格挡）就等于
    /// 一层「自带壁垒的护盾」—— 不会像普通格挡那样回合结束清空，直到被打死。
    ///
    /// 这是**背景状态**（和覆甲、冰球被动一样），不属于"这张牌的贡献"，
    /// 所以只计入总值、不参与 `cardContributes`，也不受融入暗影影响
    /// （它不是"获得的格挡"，而是已经在那儿的生命）。
    /// </summary>
    private static int GetOstyHpBlock(Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var osty = creature?.Player?.Osty;

        if (osty == null || !osty.IsAlive)
        {
            return 0;
        }

        int total = Math.Max(0, osty.CurrentHp) + Math.Max(0, osty.Block);

        if (total <= 0)
        {
            return 0;
        }

        icon = TryGetPowerIcon<DieForYouPower>();
        return total;
    }

    /// <summary>
    /// 骨手的有效回复（增生 Spur 这类带 HealVar 的牌）：
    ///   损失     = 骨手上限 - 当前血量
    ///   有效回复 = min(回复量, 损失)      —— 溢出部分会被丢掉
    ///
    /// 关键：`OstyCmd.Summon` 在骨手存活时走 `CreatureCmd.GainMaxHp`，
    /// 而 `GainMaxHp` = `SetMaxHp(+X)` 之后**再 `Heal(X)`** —— 上限和当前生命**同步**上涨，
    /// 所以召唤**不改变**「上限 − 当前生命」的缺口。
    /// （旧实现误以为只涨上限，导致治疗被高估。）
    ///
    /// 例：骨手 5/10、增生(召唤 3 / 治疗 5)、播放 2 次
    ///   第 1 轮：上限 13、当前 8（召唤同步回血）→ 损失 5 → 治 5，当前 13
    ///   第 2 轮：上限 16、当前 16 → 损失 0 → 治 0
    ///   合计 5 —— 也就是打出前那 5 点缺口，重放不会凭空增加治疗量。
    /// </summary>
    private static int GetOstyHealBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (totalPlays <= 0)
        {
            return 0;
        }

        if (!card.DynamicVars.TryGetValue("Heal", out var healVar) || healVar is not HealVar heal)
        {
            return 0;
        }

        int healAmount = heal.IntValue;

        if (healAmount <= 0)
        {
            return 0;
        }

        var osty = card.Owner?.Osty;

        if (osty == null || !osty.IsAlive)
        {
            return 0;
        }

        // 每次播放先召唤：骨手存活时只提升上限，不治疗
        int summonPerPlay = 0;

        if (card.DynamicVars.TryGetValue("Summon", out var summonVar) && summonVar is SummonVar summon)
        {
            summonPerPlay = Math.Max(0, summon.IntValue);
        }

        int maxHp = osty.MaxHp;
        int currentHp = osty.CurrentHp;
        int totalHeal = 0;

        for (int i = 0; i < totalPlays; i++)
        {
            // ⚠️ 召唤是「上限 +X **且当前生命 +X**」：
            //   `OstyCmd.Summon` 在骨手存活时调 `CreatureCmd.GainMaxHp`，
            //   而 `GainMaxHp` = `SetMaxHp(+X)` 之后**再 `Heal(X)`**（`CreatureCmd.cs:889-895`）。
            //   所以召唤**不会**扩大"上限 − 当前生命"的缺口。
            //   漏掉下面这行会把治疗算多 —— 例：骨手 3/3 打增生（召唤3/治疗5），
            //   实际是 6/6 满血、治疗全溢出（+0），却会被算成 +3。
            maxHp += summonPerPlay;
            currentHp += summonPerPlay;

            int loss = Math.Max(0, maxHp - currentHp);

            if (loss <= 0)
            {
                // 已经满血：后面几轮召唤也同步填满，治疗依然溢不进去
                break;
            }

            int healed = Math.Min(healAmount, loss);
            currentHp += healed;
            totalHeal += healed;
        }

        if (totalHeal <= 0)
        {
            return 0;
        }

        icon = TryGetPowerIcon<RegenPower>();
        return totalHeal;
    }

    /// <summary>
    /// 死亡之门（DeathsDoor）：本回合给过灾厄时，额外再获得 RepeatVar 次格挡。
    /// 第一次的格挡已经由手牌段（BlockVar.PreviewValue）显示。
    /// </summary>
    private static int GetDeathDoorExtraBlock(CardModel card, out Texture2D? icon)
    {
        icon = null;

        if (card is not DeathsDoor || !WasDoomAppliedThisTurn(card))
        {
            return 0;
        }

        int block = 0;

        if (card.DynamicVars.TryGetValue(BlockVar.defaultName, out var blockVar) && blockVar is BlockVar bv)
        {
            block = (int)Math.Floor(bv.PreviewValue);
        }

        if (block <= 0)
        {
            return 0;
        }

        int repeat = 2;

        if (card.DynamicVars.TryGetValue("Repeat", out var repeatVar) && repeatVar is RepeatVar rv)
        {
            repeat = Math.Max(0, rv.IntValue);
        }

        if (repeat <= 0)
        {
            return 0;
        }

        icon = TryGetPowerIcon<DoomPower>();
        return block * repeat;
    }

    private static bool WasDoomAppliedThisTurn(CardModel card)
    {
        var combatState = card.CombatState;

        if (combatState == null)
        {
            return false;
        }

        try
        {
            var doomOwner = card.Owner;
            var doomSnap = doomOwner != null ? GetHistorySnapshot(combatState, doomOwner) : null;

            if (doomSnap != null)
            {
                return doomSnap.DoomAppliedThisTurn;
            }

            return CombatManager.Instance.History.Entries
                .OfType<PowerReceivedEntry>()
                .Any(e => e.HappenedThisTurn(combatState)
                          && e.Power is DoomPower
                          && e.Applier == card.Owner.Creature);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 查询灾厄记录失败: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 灾厄之衣（ShroudPower）：每次敌人的 DoomPower 层数发生变化获得「层数」点格挡。
    ///
    /// 游戏里它挂的是 AfterPowerAmountChanged（不是"每次打牌"），所以：
    ///   一张牌对 N 个敌人各上 1 次灾厄 → 触发 N 次，不是 1 次。
    /// 而 PowerCmd.Apply&lt;T&gt;(IEnumerable&lt;Creature&gt;) 内部就是 foreach 逐个 Apply，
    /// 所以 AoE 牌确实是按敌人个数触发。
    ///
    /// 逐牌核对游戏实现（反编译 sts2.decompiled.cs）：
    ///   Death         TargetType.AnyEnemy   → 1 次
    ///   Scourge       TargetType.AnyEnemy   → 1 次
    ///   Deathbringer  TargetType.AllEnemies → 敌人数（Apply 传 HittableEnemies 集合）
    ///   EndOfDays     TargetType.AllEnemies → 敌人数（显式 foreach）
    ///   NegativePulse TargetType.AllEnemies → 敌人数（显式 foreach）
    ///   Oblivion      AnyEnemy，但它 Apply 的是 OblivionPower，
    ///                 Doom 变量只是 OblivionPower 的数值参数 → 本身不上灾厄，0 次
    /// </summary>
    private static int GetDoomCloakBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<ShroudPower>();

        if (power == null || power.Amount <= 0 || totalPlays <= 0)
        {
            return 0;
        }

        int applicationsPerPlay = EstimateDoomApplicationsPerPlay(card);

        if (applicationsPerPlay <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return power.Amount * applicationsPerPlay * totalPlays;
    }

    /// <summary>
    /// 这张牌每播放一次会让多少个敌人的灾厄层数发生变化（= 灾厄之衣触发几次）。
    /// 返回 0 表示这张牌根本不上灾厄。
    /// </summary>
    private static int EstimateDoomApplicationsPerPlay(CardModel card)
    {
        // 不带 Doom 变量的牌不会上灾厄
        if (!card.DynamicVars.TryGetValue("Doom", out var doomVar) || doomVar is not PowerVar<DoomPower>)
        {
            return 0;
        }

        // Oblivion 的 Doom 变量只是喂给 OblivionPower 的数值，牌本身不上灾厄
        if (card is Oblivion)
        {
            return 0;
        }

        // 这三张是全体敌人，逐个 Apply
        if (card is Deathbringer || card is EndOfDays || card is NegativePulse)
        {
            return CountHittableEnemies(card);
        }

        // Death / Scourge 以及任何未知的带 Doom 变量的牌：按单目标算 1 次
        return 1;
    }

    /// <summary>可命中的存活敌人数量（AoE 上灾厄按这个数触发）。</summary>
    private static int CountHittableEnemies(CardModel card)
    {
        try
        {
            var combatState = card.CombatState;

            if (combatState == null)
            {
                return 0;
            }

            int count = 0;

            foreach (var enemy in combatState.HittableEnemies)
            {
                if (enemy.IsAlive)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计可命中敌人失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 所向无敌（IAmInvincible）：它在抽牌堆顶时，会于回合末的 PostPlay 阶段自动打出，白给格挡。
    ///
    /// 拖任意牌时检查抽牌堆：
    ///   A% = 所向无敌数量 / 抽牌堆数量（分母包含它自己）；已经在顶部时直接 100%
    ///   X  = 它在牌堆里那张卡的 BlockVar（10 / 升级 13）
    /// 图标用「完美契合」附魔——那张附魔的作用就是把牌洗到牌堆顶，用作"顶部生效"的提示。
    /// </summary>
    // ==================================================================
    //  药水带来的格挡
    //
    //  药水是**可选使用**的，所以归到第二行（不确定项）：
    //  玩家知道手里这瓶药能加多少，但喝不喝由他决定。
    // ==================================================================

    /// <summary>读药水自己声明的 BlockVar 数值（没有则 0）。</summary>
    private static int ReadPotionBlock(PotionModel potion)
    {
        if (potion.DynamicVars.TryGetValue(BlockVar.defaultName, out var blockVar)
            && blockVar is BlockVar bv)
        {
            return Math.Max(0, bv.IntValue);
        }

        return 0;
    }

    /// <summary>
    /// 读药水里某个 PowerVar 的整数值（如 <c>PlatingPower</c>）。
    /// 有些药水不直接给格挡，而是给一个"回合结束时转成格挡"的能力，
    /// 这时数值在 <c>PowerVar&lt;T&gt;</c> 里，不在 <c>BlockVar</c> 里。
    /// </summary>
    private static int ReadPotionPower<P>(PotionModel potion) where P : PowerModel
    {
        if (potion.DynamicVars.TryGetValue(typeof(P).Name, out var powerVar) && powerVar != null)
        {
            return Math.Max(0, powerVar.IntValue);
        }

        return 0;
    }

    /// <summary>
    /// 玩家药水槽里「能直接给格挡」的药水，**每瓶一项**。
    /// 逐瓶核对游戏实现：
    ///   BlockPotion   → BlockVar(12)
    ///   ShipInABottle → BlockVar(10)（另有一份"下回合格挡"，不计入本回合）
    ///   Fortifier     → `GainBlock(当前格挡 × 2)`，效果是"格挡变三倍"，
    ///                    所以净增量是**当前格挡的 2 倍**
    ///   Ashwater      → 消耗任意张手牌，格挡取决于选几张 → 只显示手牌数并标 ?（见循环里）
    /// </summary>
    /// <param name="extraShadowmeldStacks">
    /// 正在拖的牌打出去之后会额外叠的融入暗影层数（拖「融入暗影」时 > 0）。
    /// 打牌后倍率已经提升，再喝药水自然也会被翻倍，所以这里要跟着用同一个倍率。
    /// </param>
    private static List<PotionPreview> GetPotionPreviews(Player? player, int extraShadowmeldStacks = 0)
    {
        var list = new List<PotionPreview>();

        if (player == null)
        {
            return list;
        }

        // 融入暗影影响"本回合获得的格挡"，喝药同样发生在回合内
        int blockMultiplier = GetBlockGainMultiplier(player.Creature, extraShadowmeldStacks);

        try
        {
            foreach (var potion in player.PotionSlots)
            {
                if (potion == null)
                {
                    continue;
                }

                // 灰水（Ashwater）特殊：它消耗任意张手牌，能拿多少格挡完全取决于玩家选几张。
                // 所以这里不显示格挡（算不出），只显示**最多能烧几张**（手牌数），并标 ?
                if (potion is Ashwater)
                {
                    int handCards = PileType.Hand.GetPile(player).Cards.Count;

                    if (handCards > 0)
                    {
                        list.Add(new PotionPreview(handCards, potion.Image, Uncertain: true));
                    }

                    continue;
                }

                int value = potion switch
                {
                    BlockPotion => ReadPotionBlock(potion),
                    ShipInABottle => ReadPotionBlock(potion),
                    // 固化药水：把格挡变成三倍 → 净增 = 当前格挡 × 2
                    Fortifier => Math.Max(0, player.Creature?.Block ?? 0) * 2,
                    // 铁心药水（覆甲药水）：给「层数」点 Plating（默认 7），
                    // 它在回合结束时转成等量格挡，所以按"等效格挡"计入这一行。
                    HeartOfIron => ReadPotionPower<PlatingPower>(potion),
                    // 发光水：把**手中全部牌**逐张 Exhaust（然后抽 10 张）。
                    // 每消耗一张就触发一次无惧疼痛，所以收益 = 手牌数 × 无惧疼痛层数。
                    // 没有无惧疼痛时是 0，这一项不会出现。
                    GlowwaterPotion => PileType.Hand.GetPile(player).Cards.Count
                                       * Math.Max(0, player.Creature?.GetPowerAmount<FeelNoPainPower>() ?? 0),
                    _ => 0,
                };

                if (value <= 0)
                {
                    continue;
                }

                // 药水没有 Icon 属性，图标是 Image（Texture2D）
                list.Add(new PotionPreview(value * blockMultiplier, potion.Image));
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取药水槽失败: {e.Message}");
        }

        return list;
    }

    // ==================================================================
    //  遗物带来的格挡
    //
    //  统一从玩家身上取遗物实例，再读它自己的 BlockVar —— 不同遗物数值不同
    //  （2 / 1 / 1 / 6 / 4 / 3），而且将来可能被别的效果改，所以不硬编码数字。
    // ==================================================================

    /// <summary>取玩家身上的某个遗物实例，没有则 null。</summary>
    private static T? GetRelic<T>(Player? player) where T : RelicModel
    {
        if (player == null)
        {
            return null;
        }

        try
        {
            foreach (var relic in player.Relics)
            {
                if (relic is T typed)
                {
                    return typed;
                }
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取遗物失败: {e.Message}");
        }

        return null;
    }

    /// <summary>读遗物自己声明的 BlockVar 数值（没有则 0）。</summary>
    private static int ReadRelicBlock(RelicModel? relic)
    {
        if (relic != null
            && relic.DynamicVars.TryGetValue(BlockVar.defaultName, out var blockVar)
            && blockVar is BlockVar bv)
        {
            return Math.Max(0, bv.IntValue);
        }

        return 0;
    }

    /// <summary>读遗物任意具名变量的整数值（阈值类，如 Stars / Cards / Energy）。</summary>
    private static int ReadRelicVar(RelicModel? relic, string key)
    {
        if (relic != null
            && relic.DynamicVars.TryGetValue(key, out var v)
            && v != null)
        {
            return Math.Max(0, v.IntValue);
        }

        return 0;
    }

    /// <summary>本回合已经打出的攻击牌数量（精致折扇用，计数器本身是 private）。</summary>
    private static int CountAttackPlaysThisTurn(Creature? creature)
    {
        var combatState = creature?.CombatState;
        var player = creature?.Player;

        if (combatState == null || player == null)
        {
            return 0;
        }

        var attackSnap = GetHistorySnapshot(combatState, player);
        if (attackSnap != null)
        {
            return attackSnap.AttackPlaysThisTurn;
        }

        try
        {
            return CombatManager.Instance.History.CardPlaysFinished.Count(
                e => e.HappenedThisTurn(combatState)
                     && e.CardPlay.Card.Type == CardType.Attack
                     && e.CardPlay.Player == player);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计本回合攻击牌失败: {e.Message}");
            return 0;
        }
    }

    // ==================================================================
    //  4 个"计数器型"遗物（2026-11-20 接入）
    //
    //  它们的共同点：**状态是 public 可读或可用战斗历史推出**，所以能算准。
    //  算不准的（永冻冰晶 / 君王矿石 / 佩尔的士兵）见 待开发遗物.md，
    //  它们需要读 private 字段，留给用户决定。
    // ==================================================================

    /// <summary>
    /// 星系尘埃（GalacticDust）：每累计消耗「阈值」点星（默认 10），获得「层数」格挡（默认 10）。
    ///
    /// `StarsSpent` 是 `public [SavedProperty]`，直接可读；余数会保留（`StarsSpent %= 阈值`），
    /// 所以单张牌消耗 12 星只触发 1 次。
    /// </summary>
    private static int GetGalacticDustBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<GalacticDust>(creature?.Player);

        if (relic == null)
        {
            return 0;
        }

        int threshold = Math.Max(1, ReadRelicVar(relic, "Stars"));
        int perBlock = ReadRelicBlock(relic);

        if (perBlock <= 0)
        {
            return 0;
        }

        int starsCost = Math.Max(0, card.GetStarCostWithModifiers());

        if (starsCost <= 0)
        {
            return 0;
        }

        int triggers = (relic.StarsSpent + starsCost) / threshold;

        if (triggers <= 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return triggers * perBlock;
    }

    /// <summary>
    /// 精致折扇（OrnamentalFan）：每回合每打出「阈值」张攻击牌（默认 3），获得「层数」格挡（默认 4）。
    ///
    /// 计数器**每回合重置**，且是 private，所以用战斗历史数本回合攻击牌再算差值，
    /// 避免把"本回合已经拿过的奖励"重复计入。
    /// </summary>
    private static int GetOrnamentalFanBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (card.Type != CardType.Attack)
        {
            return 0;
        }

        var relic = GetRelic<OrnamentalFan>(creature?.Player);

        if (relic == null)
        {
            return 0;
        }

        int threshold = Math.Max(1, ReadRelicVar(relic, "Cards"));
        int perBlock = ReadRelicBlock(relic);

        if (perBlock <= 0)
        {
            return 0;
        }

        int cur = CountAttackPlaysThisTurn(creature);
        int triggers = (cur + totalPlays) / threshold - cur / threshold;

        if (triggers <= 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return triggers * perBlock;
    }

    /// <summary>
    /// 音叉（TuningFork）：每打出「阈值」张技能牌（默认 10），获得「层数」格挡（默认 7）。
    ///
    /// `SkillsPlayed` 是 `public [SavedProperty]`，**跨回合累计**（不重置），
    /// 触发后减阈值、溢出保留。
    /// </summary>
    private static int GetTuningForkBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (card.Type != CardType.Skill)
        {
            return 0;
        }

        var relic = GetRelic<TuningFork>(creature?.Player);

        if (relic == null)
        {
            return 0;
        }

        int threshold = Math.Max(1, ReadRelicVar(relic, "Cards"));
        int perBlock = ReadRelicBlock(relic);

        if (perBlock <= 0)
        {
            return 0;
        }

        int cur = Math.Max(0, relic.SkillsPlayed);
        int triggers = (cur + totalPlays) / threshold - cur / threshold;

        if (triggers <= 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return triggers * perBlock;
    }

    /// <summary>
    /// 永冻冰晶（Permafrost）：**每场战斗第一次打出能力牌**时获得「层数」格挡（默认 7）。
    ///
    /// 触发点是 `AfterCardPlayed`，条件是 `CardType.Power && !ActivatedThisCombat`。
    /// 判据"本场是否已打出过自己的能力牌"在 `History.CardPlaysFinished` 里是 public 的，
    /// 不需要反射私有字段。
    /// </summary>
    private static int GetPermafrostBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (card.Type != CardType.Power)
        {
            return 0;
        }

        var player = creature?.Player;
        var relic = GetRelic<Permafrost>(player);

        if (relic == null)
        {
            return 0;
        }

        try
        {
            bool alreadyActivated;

            var permafrostState = creature?.CombatState;
            var permafrostSnap = permafrostState != null && player != null
                ? GetHistorySnapshot(permafrostState, player)
                : null;

            if (permafrostSnap != null)
            {
                alreadyActivated = permafrostSnap.PowerPlayedEver;
            }
            else
            {
                alreadyActivated = CombatManager.Instance.History.CardPlaysFinished.Any(
                    e => e.CardPlay.Card.Owner == player
                         && e.CardPlay.Card.Type == CardType.Power);
            }

            if (alreadyActivated)
            {
                return 0;
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 查询能力牌历史失败: {e.Message}");
            return 0;
        }

        icon = relic.Icon;
        return ReadRelicBlock(relic);
    }

    /// <summary>
    /// 君王矿石（Regalite）：**每回合第一次生成牌**时获得「层数」格挡（默认 4）。
    ///
    /// 游戏里 `AfterCardGeneratedForCombat` 每张各触发一次，但 `UsedThisTurn` 在第一次就置位，
    /// 所以**一次生成多张也只给一次**（子智能体已核对 `CardPileCmd.AddGeneratedCardsToCombat`）。
    ///
    /// 判据：
    ///   - "这张牌会不会生成牌" → 复用创世之柱的 `EstimateGeneratedCardsTotal`
    ///     （顺带覆盖 `StormOfSteel` 空手生成 0 张这种边界）
    ///   - "本回合是否已触发" → `History` 的 `CardGeneratedEntry`，`Creator` 是 public
    /// </summary>
    private static int GetRegaliteBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<Regalite>(player);

        if (relic == null)
        {
            return 0;
        }

        if (EstimateGeneratedCardsTotal(card, Math.Max(1, totalPlays)) <= 0)
        {
            return 0;
        }

        try
        {
            var state = player?.Creature?.CombatState;

            if (state == null)
            {
                return 0;
            }

            var genSnap = player != null ? GetHistorySnapshot(state, player) : null;

            bool usedThisTurn;

            if (genSnap != null)
            {
                usedThisTurn = genSnap.CardGeneratedThisTurn;
            }
            else
            {
                usedThisTurn = CombatManager.Instance.History.Entries
                    .OfType<CardGeneratedEntry>()
                    .Any(e => e.Creator == player && e.HappenedThisTurn(state));
            }

            if (usedThisTurn)
            {
                return 0;
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 查询生成牌历史失败: {e.Message}");
            return 0;
        }

        icon = relic.Icon;
        return ReadRelicBlock(relic);
    }

    // ==================================================================
    //  算盘（TheAbacus）—— 只做「必然洗牌」的确定场景
    //
    //  游戏里算盘挂 AfterShuffle（每次洗牌 +BlockVar，Unpowered）。
    //  洗牌点只有一个：CardPileCmd.DrawInternal 的循环里，**每抽一张之前**
    //  调 ShuffleIfNecessary —— 判据是「抽牌堆空 && 弃牌堆非空」。
    //  所以「这张牌要抽的张数 > 当前抽牌堆张数」且「弃牌堆还有牌」时，
    //  这次抽牌中途**必然**洗一次牌 → 算盘给一次格挡。
    //
    //  手牌上限会先削掉一部分抽牌（DrawInternal 里 num = 10 - 手牌数），
    //  手牌满了就一张都不抽、也就不洗，所以必须先按空位封顶。
    // ==================================================================

    /// <summary>
    /// 抽牌数直接由 `DynamicVars.Cards` 决定的牌（从 `sts2-src` 全量扫出）。
    /// 带条件分支、依赖目标/牌序的牌**不**收进来，避免误报（宁可不显示）。
    /// </summary>
    private static readonly HashSet<string> _drawCountCardsVar = new(StringComparer.Ordinal)
    {
        "Acrobatics", "Adrenaline", "Backflip", "BattleTrance", "BigBang", "BrightestFlame",
        "BurningPact", "Constellation", "Coolheaded", "DecisionsDecisions", "DeepStretch",
        "DrumOfBattle", "Encore", "Expertise", "Finesse", "FlashOfSteel", "Glimmer", "Glow",
        "Joy", "MasterOfStrategy", "MinionStrike", "Modded", "Neurosurge", "Offering",
        "Overclock", "Parse", "PhotonCut", "PommelStrike", "Prepared", "Prophesize",
        "Reboot", "Reflex", "RocketPunch", "Rummage", "Scourge", "Scrape", "ShrugItOff",
        "Skim", "Slimed", "Soul", "SpoilsOfBattle", "SweepingBeam", "TheHermit",
        "TheHighPriestess", "TheSun", "ThinkingAhead", "WahChoKi",
    };

    /// <summary>固定抽 1 张、没有 `Cards` 变量的牌（`CardPileCmd.Draw(ctx, player)` 单张版）。</summary>
    private static readonly HashSet<string> _drawCountOne = new(StringComparer.Ordinal)
    {
        // Pillage 是「循环抽到非攻击牌」，单次播放时由 GetAbacusBlock 里的专门分支精确判定；
        // 这里只是**重放**（plays > 1）时的保守下限
        "DaggerThrow", "EscapePlan", "Pillage",
    };

    /// <summary>抽牌数由 `CalculatedCards` 决定（读不到就按 0）。</summary>
    private static readonly HashSet<string> _drawCountCalculated = new(StringComparer.Ordinal)
    {
        "CompileDriver", "SteadyCadence",
    };

    /// <summary>这张牌自己会抽几张（每次播放）。返回 0 表示"不确定/不抽"。</summary>
    private static int GetDrawCount(CardModel card)
    {
        var name = card.GetType().Name;

        try
        {
            if (_drawCountCardsVar.Contains(name))
            {
                return ReadDynamicVar(card, "Cards");
            }

            if (_drawCountOne.Contains(name))
            {
                return 1;
            }

            if (_drawCountCalculated.Contains(name)
                && card.DynamicVars.TryGetValue("CalculatedCards", out var calcVar)
                && calcVar is CalculatedVar calculated)
            {
                // 这两张的 multiplier 只看自己（球种类 / 牌型），与目标无关
                return Math.Max(0, (int)Math.Floor(calculated.Calculate(null)));
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读抽牌数失败({name}): {e.Message}");
        }

        return 0;
    }

    /// <summary>
    /// 算盘（TheAbacus）：抽牌堆不够抽、必然洗牌时给 `BlockVar` 点格挡。
    ///
    /// 只做**必然场景**；其余（依赖牌序、条件分支、多轮重放第二次洗牌）一律返回 0。
    /// </summary>
    private static int GetAbacusBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<TheAbacus>(player);

        if (relic == null || player == null)
        {
            return 0;
        }

        // 本回合不能抽牌（NoDrawPower）→ 压根不抽，也就不会洗
        if (player.Creature.HasPower<NoDrawPower>())
        {
            return 0;
        }

        try
        {
            var handPile = PileType.Hand.GetPile(player);
            var drawPile = PileType.Draw.GetPile(player);
            var discardPile = PileType.Discard.GetPile(player);

            // 打出这张牌的瞬间它已经离开手牌，先空出一格
            int handAfterPlay = Math.Max(0, handPile.Cards.Count - 1);
            int room = Math.Max(0, CardPile.MaxCardsInHand - handAfterPlay);

            if (room <= 0)
            {
                return 0;
            }

            // Pillage 是「循环抽牌直到抽到非攻击牌」的特例，判据和普通抽牌不同
            if (card is Pillage && totalPlays <= 1)
            {
                // 游戏里的循环：Draw(1) → 抽到非攻击牌 或 抽不到 或 手牌满 就停。
                // 所以会不会洗牌只看三件事：
                //   1) 抽牌堆里还有没有非攻击牌 —— 有的话抽到它就停，压根走不到洗牌
                //   2) 手牌余量够不够抽完整个抽牌堆 —— 不够就是手牌先满、循环先停
                //   3) 弃牌堆非空 —— 空了洗了也没牌
                // 注意 1) 查的是**抽牌堆**不是弃牌堆：弃牌堆里有没有非攻击牌
                // 只决定洗牌之后还能抽几张，不影响"抽牌堆一空就洗"这件事本身。
                bool hasNonAttack = drawPile.Cards.Any(c => c.Type != CardType.Attack);

                if (hasNonAttack
                    || room <= drawPile.Cards.Count
                    || discardPile.Cards.Count == 0)
                {
                    return 0;
                }

                icon = relic.Icon;
                return ReadRelicBlock(relic);
            }

            int wanted;

            if (card is Scrawl)
            {
                // Scrawl 自己就是「抽到手牌满」：num = 10 - 手牌数
                wanted = room;
            }
            else if (card is Restlessness)
            {
                // 只有它是手里唯一一张时才抽
                if (handPile.Cards.Count != 1)
                {
                    return 0;
                }

                wanted = ReadDynamicVar(card, "Cards") * Math.Max(1, totalPlays);
            }
            else
            {
                int perPlay = GetDrawCount(card);

                if (perPlay <= 0)
                {
                    return 0;
                }

                wanted = perPlay * Math.Max(1, totalPlays);
            }

            // 手牌空位封顶：抽不进手牌的不会发生，也就不会把牌堆抽空
            int effective = Math.Min(wanted, room);

            if (effective <= 0)
            {
                return 0;
            }

            // 要抽的比抽牌堆还多 → 抽到中途抽牌堆必空；
            // 只要弃牌堆还有牌，ShuffleIfNecessary 就会洗一次
            if (drawPile.Cards.Count < effective && discardPile.Cards.Count > 0)
            {
                icon = relic.Icon;
                return ReadRelicBlock(relic);
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 算盘读取牌堆失败: {e.Message}");
        }

        return 0;
    }

    // ==================================================================
    //  愚者（The Fool）：弓箭（Arrow）与旋律（Melody）
    // ==================================================================

    // ---------- 旋律状态缓存 ----------
    // 性能要求：拖牌选目标期间**不能每帧扫历史**。
    // `MelodyCmd.PlayedXxxThisTurn` 每次都是 O(历史条目) 的 LINQ 全扫，
    // 一帧调 4~7 次在长战斗里会明显掉帧（用户实测反馈）。
    // 这里按「玩家 + 回合签名 + 历史条目数」缓存：只有打出牌（历史增长）或换回合才重算。

    private struct MelodyState
    {
        public bool Wah;
        public bool Cho;
        public bool Ki;
        public int Count;
    }

    private static MelodyState? _melodyCache;
    private static Player? _melodyCachePlayer;
    private static int _melodyCacheSig = int.MinValue;
    private static int _melodyCacheVersion = -2;

    /// <summary>本回合的旋律状态（三种是否已激活 + 种类数），带缓存。</summary>
    private static MelodyState GetMelodyState(Player player, ICombatState? state)
    {
        if (state == null)
        {
            return default;
        }

        int sig = TurnSignature(state);
        int version = HistoryEntryCount();

        if (_melodyCache.HasValue
            && ReferenceEquals(player, _melodyCachePlayer)
            && sig == _melodyCacheSig
            && version == _melodyCacheVersion)
        {
            return _melodyCache.Value;
        }

        var result = default(MelodyState);

        try
        {
#if STS2_FOOL
            result.Wah = MelodyCmd.PlayedWahThisTurn(player);
            result.Cho = MelodyCmd.PlayedChoThisTurn(player);
            result.Ki = MelodyCmd.PlayedKiThisTurn(player);
#endif
            result.Count = (result.Wah ? 1 : 0) + (result.Cho ? 1 : 0) + (result.Ki ? 1 : 0);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取旋律状态失败: {e.Message}");
            return default;
        }

        _melodyCache = result;
        _melodyCachePlayer = player;
        _melodyCacheSig = sig;
        _melodyCacheVersion = version;

        return result;
    }

    /// <summary>
    /// 合股线（Twine）：箭被**松脱**时给 `BlockVar(4)` 点格挡（`ValueProp.Unpowered`）。
    ///
    /// 松脱只有两条路（`ArrowCmd` / `ImpaledPower`）：
    ///   ① 目标挨满 3 次**强力攻击** —— `HitCounter` 从 3 递减到 0，
    ///      触发松脱后**重置回 3**（`ImpaledPower.AfterDamageReceived`）
    ///   ② 目标**死亡** —— `DislodgeAllArrows` 把身上每根箭各松脱一次
    ///      （`ImpaledPower.AfterDeath` / `ArrowCmd.ImpaleTarget` 里的死亡分支）
    ///
    /// 预览口径：
    ///   - 只算**拖攻击牌**（只有攻击才会减 `HitCounter`）；常驻显示不显示
    ///   - 目标身上必须刺着**自己的**箭（`Applier == 玩家`）。
    ///     手里还有可用箭时目标身上必然是空的，所以两者天然互斥，不用额外判断
    ///   - ① 按命中次数逐次推演：多段攻击会更快凑满 3 次，但松脱后箭少一根、计数重置
    ///   - ② 用 `DamageVar.PreviewValue × 命中次数` 与「当前生命 + 格挡」比较，
    ///     判定"这一击能打死"就把剩余箭数一次性全算上
    /// </summary>
    private static int GetTwineBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
#if STS2_FOOL
        icon = null;

        if (player == null || card.Type != CardType.Attack)
        {
            return 0;
        }

        // 奥斯提攻击（OstyAttack）的 dealer 是骨手而不是玩家自己，
        // 不满足 `dealer == Applier`，不会减 HitCounter —— 排除掉免得虚增
        if (card.Tags.Contains(CardTag.OstyAttack))
        {
            return 0;
        }

        // 射击牌（CardTag.Shoot）在没有可用箭时**完全打不出去**：
        // `ArrowCmd.ShootArrowAtTarget` 拿不到箭就直接返回，连攻击都不执行
        // （见 FoolishShot.OnPlay 与 ArrowCmd.TryShootArrow），
        // 所以这种状态下它不会命中任何东西，不该显示松脱收益
        if (card.Tags.Contains(CardTag.Shoot) && !HasAvailableArrow(player))
        {
            return 0;
        }

        var relic = GetRelic<Twine>(player);

        if (relic == null)
        {
            return 0;
        }

        int perLoose = ReadRelicBlock(relic);

        if (perLoose <= 0)
        {
            return 0;
        }

        var combatState = card.CombatState;

        if (combatState == null)
        {
            return 0;
        }

        try
        {
            // 悬停目标是缓存 key 的一部分：玩家把鼠标从 A 移到 B 必须重算
            var hoverTarget = GetHoverTarget(card);
            int sig = TurnSignature(combatState);
            int version = HistoryEntryCount();

            if (ReferenceEquals(card, _twineCard)
                && ReferenceEquals(hoverTarget, _twineHoverTarget)
                && version == _twineVersion
                && sig == _twineSig)
            {
                icon = _twineIconCache;
                return _twineValue;
            }

            // 射击牌最多只会命中"一次播放"的量：射完这一箭手里就没箭了，
            // 重放 / 额外播放的那几次根本打不出去（`ShootArrowAtTarget` 拿不到箭直接返回）
            int playsForHits = card.Tags.Contains(CardTag.Shoot) ? 1 : Math.Max(1, totalPlays);
            int hits = GetCardHitCount(card) * playsForHits;

            int damagePerHit = GetCardDamagePreview(card);
            int loosed = 0;

            // 射击牌会射出一支**新箭**：若这一箭直接打死目标，
            // `ArrowCmd.ImpaleTarget` 里 `target.IsDead` 分支会当场把它松脱（+4）
            bool shootsArrow = card.Tags.Contains(CardTag.Shoot) && HasAvailableArrow(player);

            foreach (var enemy in GetHittableTargets(card, combatState, hoverTarget))
            {
                var impaled = enemy.GetPowerInstances<ImpaledPower>()
                    .FirstOrDefault(p => p.Applier == player.Creature);

                // 身上刺着几根自己的箭。0 不代表一定没收益 ——
                // 射击牌自己那支箭会在击杀时立即松脱
                int arrows = impaled?.Arrows.Count ?? 0;
                int counter = Math.Max(1, impaled?.HitCounter ?? 3);
                int remainingHits = hits;

                // ① 命中把计数打空 → 松脱一根，计数重置为 3，箭少一根
                while (arrows > 0 && remainingHits >= counter)
                {
                    remainingHits -= counter;
                    arrows--;
                    loosed++;
                    counter = 3;
                }

                if (damagePerHit <= 0)
                {
                    continue;
                }

                // 硬性免疫 / 减伤：这些状态下不能按「伤害 ≥ 生命 + 格挡」判击杀
                //   · `IntangiblePower`（伤害上限 1）、`HardToKillPower`（上限 = 层数）
                //     走的是 `ModifyDamageCap`，而 `Hook.ModifyDamage` 用
                //     `ModifyDamageHookType.All` 已经把上限算进 `DamageVar.PreviewValue`，
                //     所以这里不用再处理
                //   · `BufferPower` 是 `ModifyHpLostAfterOstyLate`（HP 损失层），**不在**伤害预览里，
                //     每层抵消一次命中，必须手动扣
                int bufferStacks = Math.Max(0, enemy.GetPowerAmount<BufferPower>());
                long effectiveHits = Math.Max(0, (long)hits - bufferStacks);
                long totalDamage = (long)damagePerHit * effectiveHits;

                if (totalDamage < enemy.CurrentHp + enemy.Block)
                {
                    continue;
                }

                // ② 这一击能把目标打死：
                //    · 身上剩下的旧箭一次性全松脱（`ImpaledPower.AfterDeath` → DislodgeAllArrows）
                //    · 射击牌**刚射出去的这一支**也会立刻松脱
                loosed += arrows;

                if (shootsArrow)
                {
                    loosed++;
                }
            }

            int result = loosed > 0 ? loosed * perLoose : 0;
            icon = loosed > 0 ? relic.Icon : null;

            _twineCard = card;
            _twineHoverTarget = hoverTarget;
            _twineVersion = version;
            _twineSig = sig;
            _twineValue = result;
            _twineIconCache = icon;

            return result;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 合股线读取箭矢失败: {e.Message}");
            return 0;
        }
#else
        icon = null;
        return 0;
#endif
    }

    /// <summary>
    /// 歌之路（`SongPathPower`）：本回合**第一张**带 `CardTag.Cho` 的牌打出时获得格挡。
    ///
    /// 游戏判据是 `MelodyCmd.IsFirstPlayThisTurn(cardPlay, Cho)`；
    /// 预览时这张牌还没开始播放，所以等价于「本回合还没打过 Cho 牌」。
    /// 重放（replayPlays &gt; 1）时只有第一次算——第一次就是这回合的第一张。
    /// </summary>
    private static int GetSongPathBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
#if STS2_FOOL
        icon = null;

        if (creature == null)
        {
            return 0;
        }

        int amount = creature.GetPowerAmount<SongPathPower>();

        if (amount <= 0)
        {
            return 0;
        }

        if (!card.Tags.Contains(CardTag.Cho))
        {
            return 0;
        }

        var player = creature.Player;

        if (player == null)
        {
            return 0;
        }

        // 本回合已经打过 Cho 牌 → 这张不是第一张，不触发（走缓存，不每帧扫历史）
        var melody = GetMelodyState(player, creature.CombatState);

        if (melody.Cho)
        {
            return 0;
        }

        icon = GetSongPathIcon();
        return amount;
#else
        icon = null;
        return 0;
#endif
    }

    /// <summary>
    /// 摇篮曲（`LullabyPower`）：本回合集齐**哇/啾/叽三种旋律**时获得一次格挡。
    ///
    /// 游戏在 `AfterCardPlayed` 里判「三种齐 且 本回合未触发」。
    /// 预览时把这张牌自己的标签也算进"打出后"的状态：
    ///   - 打完后三种齐（当前已有 2 种 + 这张牌补上最后一种）
    ///   - 且当前还没齐（`MelodiesThisTurn &lt; 3`）——
    ///     一旦齐了就会立刻触发并把 `_proccdThisTurn` 置 true（该字段 private，无法直接读，
    ///     但"当前已齐"与"已触发过"等价：补齐的那一刻就触发了；
    ///     刚获得 Lullaby 时 `AfterApplied` 也会把标志置 true）
    /// </summary>
    private static int GetLullabyBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
#if STS2_FOOL
        icon = null;

        if (creature == null)
        {
            return 0;
        }

        int amount = creature.GetPowerAmount<LullabyPower>();

        if (amount <= 0)
        {
            return 0;
        }

        var player = creature.Player;

        if (player == null)
        {
            return 0;
        }

        // 走缓存，不每帧扫历史
        var melody = GetMelodyState(player, creature.CombatState);

        bool wah = melody.Wah || card.Tags.Contains(CardTag.Wah);
        bool cho = melody.Cho || card.Tags.Contains(CardTag.Cho);
        bool ki = melody.Ki || card.Tags.Contains(CardTag.Ki);

        if (!wah || !cho || !ki)
        {
            return 0;
        }

        // 当前就已经三种齐了 → 本回合已经触发过（或获得时已置标志）
        if (melody.Count >= 3)
        {
            return 0;
        }

        icon = GetLullabyIcon();
        return amount;
#else
        icon = null;
        return 0;
#endif
    }

    /// <summary>
    /// 万事无忧（`AllIsWellPower`）：**每生成一张「悲伤」（Sadness）**获得 `层数` 点格挡。
    ///
    /// 触发点是 `AfterCardGeneratedForCombat`，而 `CardPileCmd.AddGeneratedCardsToCombat`
    /// 对**每一张**卡各调一次 Hook（`CardPileCmd.cs:272-278`），所以张数直接乘即可；
    /// 而且 Hook 是在 `Add` **之后**无条件调用的，**手牌满了照样给格挡**，不用封顶。
    ///
    /// 会生成悲伤的牌只有 6 张（`CreateCard&lt;Sadness&gt;` + 生成接口）：
    ///   `BrightenUp` 2 / `SorrowfulStrike` 2 / `Jest` 1 / `SelfDeprecate` 1 / `Rummage` 1 / `Vitriol` 1
    ///
    /// 两个容易误算的：
    ///   - `MourningBlow` 用 `TransformIntoAfterPlay` **转化**成悲伤（走 `CardCmd.Transform`），
    ///     不走生成 Hook → **不算**
    ///   - `Ennui` 靠 `EnnuiPower` 在**回合开始抽牌前**生成，和拖哪张牌无关 → 不算
    /// </summary>
    private static int GetAllIsWellBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
#if STS2_FOOL
        icon = null;

        if (creature == null)
        {
            return 0;
        }

        int amount = creature.GetPowerAmount<AllIsWellPower>();

        if (amount <= 0)
        {
            return 0;
        }

        int perPlay = GetSadnessCardsGenerated(card);

        if (perPlay <= 0)
        {
            return 0;
        }

        icon = GetAllIsWellIcon();
        return amount * perPlay * Math.Max(1, totalPlays);
#else
        icon = null;
        return 0;
#endif
    }

    /// <summary>这张牌**每次播放**会生成几张「悲伤」。只认真正走生成接口的那几张牌。</summary>
    private static int GetSadnessCardsGenerated(CardModel card)
    {
#if STS2_FOOL
        return card switch
        {
            BrightenUp => Math.Max(0, ReadDynamicVar(card, "Sadness")),
            SelfDeprecate => Math.Max(0, ReadDynamicVar(card, "Sadness")),
            Jest => Math.Max(0, ReadDynamicVar(card, "Cards")),
            SorrowfulStrike => Math.Max(0, ReadDynamicVar(card, "Cards")),
            Rummage => 1,
            Vitriol => 1,
            _ => 0,
        };
#else
        return 0;
#endif
    }

    private static Texture2D? GetAllIsWellIcon()
    {
#if STS2_FOOL
        if (_allIsWellIconResolved)
        {
            return _allIsWellIcon;
        }

        _allIsWellIconResolved = true;

        try
        {
            _allIsWellIcon = ModelDb.Power<AllIsWellPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取万事无忧图标失败: {e.Message}");
            _allIsWellIcon = null;
        }

        return _allIsWellIcon;
#else
        return null;
#endif
    }

    /// <summary>
    /// 这张牌实际会命中的敌人集合：
    /// AoE 牌 = 全部可命中敌人；单体牌 = 传入的**悬停目标**（还没指向任何敌人时为空）。
    /// </summary>
    private static IEnumerable<Creature> GetHittableTargets(
        CardModel card,
        ICombatState combatState,
        Creature? hoverTarget)
    {
        if (card.TargetType == TargetType.AllEnemies)
        {
            return combatState.HittableEnemies.Where(e => e.IsAlive).ToList();
        }

        return hoverTarget != null
            ? new List<Creature> { hoverTarget }
            : new List<Creature>();
    }

    /// <summary>缓存 `NCard._previewTarget` 字段，避免每次反射都查一遍类型。</summary>
    private static readonly FieldInfo? PreviewTargetField =
        typeof(NCard).GetField("_previewTarget", BindingFlags.NonPublic | BindingFlags.Instance);

    private static CardModel? _hoverNodeCard;
    private static NCard? _hoverNode;

    /// <summary>
    /// 拖牌阶段**当前悬停的敌人**。
    ///
    /// ⚠️ 不能用 `card.CurrentTarget`：它是 `private set`，只在
    /// `CardModel.OnPlayWrapper`（真正打出这张牌时）才赋值，
    /// **拖牌选目标阶段恒为 null**（这就是合股线预览不出来的根因）。
    /// 游戏自己的卡面预览走的是 `NCard._previewTarget`
    /// （`NCard.UpdateVisuals` 里 `_previewTarget ?? Model.CurrentTarget`），
    /// 所以这里同理：先读 `_previewTarget`，读不到再退回 `CurrentTarget`。
    /// </summary>
    private static Creature? GetHoverTarget(CardModel card)
    {
        try
        {
            // 节点缓存：拖牌期间都是同一张牌，不用每帧 FindOnTable
            if (!ReferenceEquals(card, _hoverNodeCard)
                || _hoverNode == null
                || !GodotObject.IsInstanceValid(_hoverNode))
            {
                _hoverNode = NCard.FindOnTable(card);
                _hoverNodeCard = card;
            }

            if (_hoverNode != null
                && PreviewTargetField?.GetValue(_hoverNode) is Creature hovered
                && hovered.IsAlive)
            {
                return hovered;
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取悬停目标失败: {e.Message}");
        }

        var fallback = card.CurrentTarget;

        return fallback != null && fallback.IsAlive ? fallback : null;
    }

    /// <summary>
    /// 这张攻击牌**每次播放**会命中几次。
    /// 优先读 `HitCount`（多段攻击，如 RapidShot），
    /// 其次读 `CalculatedHits`（随局面变化的段数，如 MelodicShot / Barrage），
    /// 都没有就是 1 段。
    ///
    /// ⚠️ 带缓存：`HitCountVar.PreviewValue` 会走 `Hook.ModifyAttackHitCount`
    /// （遍历所有 hook 监听者），`CalculatedVar.Calculate` 也可能不便宜，
    /// 所以按「卡牌 + 悬停目标 + 历史条目数」缓存，避免每帧重算。
    /// </summary>
    private static int GetCardHitCount(CardModel card)
    {
        var target = GetHoverTarget(card);
        int version = HistoryEntryCount();

        if (ReferenceEquals(card, _hitCountCard)
            && ReferenceEquals(target, _hitCountTarget)
            && version == _hitCountVersion)
        {
            return _hitCountValue;
        }

        int hits = 1;

        try
        {
            var vars = card.DynamicVars;

#if STS2_FOOL
            if (vars != null
                && vars.TryGetValue(HitCountVar.defaultName, out var hv)
                && hv is HitCountVar hitCount)
            {
                hits = Math.Max(1, (int)Math.Floor(hitCount.PreviewValue));
            }
            else
#endif
            if (vars != null
                && vars.TryGetValue("CalculatedHits", out var chv)
                && chv is CalculatedVar calculatedHits)
            {
                hits = Math.Max(1, (int)Math.Floor(calculatedHits.Calculate(target)));
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取命中次数失败: {e.Message}");
        }

        _hitCountCard = card;
        _hitCountTarget = target;
        _hitCountVersion = version;
        _hitCountValue = hits;

        return hits;
    }

    private static CardModel? _hitCountCard;
    private static Creature? _hitCountTarget;
    private static int _hitCountVersion = -2;
    private static int _hitCountValue = 1;

    // 合股线结果缓存：key = 卡牌 + 悬停目标 + 历史条目数 + 回合签名
    private static CardModel? _twineCard;
    private static Creature? _twineHoverTarget;
    private static int _twineVersion = -2;
    private static int _twineSig = int.MinValue;
    private static int _twineValue;
    private static Texture2D? _twineIconCache;

    /// <summary>卡面伤害（已含易伤/虚弱等修正），没有伤害变量返回 0。</summary>
    private static int GetCardDamagePreview(CardModel card)
    {
        try
        {
            if (card.DynamicVars != null
                && card.DynamicVars.TryGetValue(DamageVar.defaultName, out var dv)
                && dv is DamageVar damage)
            {
                return Math.Max(0, (int)Math.Floor(damage.PreviewValue));
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取伤害失败: {e.Message}");
        }

        return 0;
    }

    private static Texture2D? GetSongPathIcon()
    {
#if STS2_FOOL
        if (_songPathIconResolved)
        {
            return _songPathIcon;
        }

        _songPathIconResolved = true;

        try
        {
            _songPathIcon = ModelDb.Power<SongPathPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取歌之路图标失败: {e.Message}");
            _songPathIcon = null;
        }

        return _songPathIcon;
#else
        return null;
#endif
    }

    private static Texture2D? GetLullabyIcon()
    {
#if STS2_FOOL
        if (_lullabyIconResolved)
        {
            return _lullabyIcon;
        }

        _lullabyIconResolved = true;

        try
        {
            _lullabyIcon = ModelDb.Power<LullabyPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取摇篮曲图标失败: {e.Message}");
            _lullabyIcon = null;
        }

        return _lullabyIcon;
#else
        return null;
#endif
    }

    /// <summary>
    /// 佩尔的士兵（PaelsLegion）：清醒时，**卡牌来源**（`ValueProp.Move`）的格挡 ×2，
    /// 触发一次后休眠 2 回合。
    ///
    /// ⚠️ 卡面的 `PreviewValue` 里**已经含**这次翻倍（`ModifyBlockMultiplicative` 在
    /// `Hook.ModifyBlock` 里跑），所以这里只能用于**自己算**的卡牌来源段 ——
    /// 目前只有拳斗的"伤害转格挡"。
    ///
    /// `DisplayAmount` 是 public：>0 表示休眠中（值 = 剩余回合），-1 表示可以翻倍。
    /// 不要用 `Status`（战斗开始时不置 Active，不可靠）。
    /// </summary>
    private static int GetPaelsLegionMultiplier(Player? player)
    {
        var relic = GetRelic<PaelsLegion>(player);

        if (relic == null)
        {
            return 1;
        }

        try
        {
            if (!CombatManager.Instance.IsInProgress)
            {
                return 1;
            }

            return relic.DisplayAmount > 0 ? 1 : 2;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 不动（`UnmovablePower`）：本回合**前 `Amount` 次**「从卡牌获得的格挡」数值翻倍。
    ///
    /// 游戏里 `UnmovablePower.ModifyBlockMultiplicative` 的判据：
    ///   1. `target.IsMonster` → 不生效
    ///   2. `!props.IsCardOrMonsterMove()`（即没有 `ValueProp.Move`）→ 不生效
    ///      —— 所以**遗物/能力/药水（`Unpowered`）全部不吃**，冰球、覆甲、狂怒、余像…
    ///         也都不吃，只有"卡牌直接给的格挡"和"伤害转格挡"这类才算
    ///   3. 用 `History` 数「本回合已经发生了几条 Move 格挡」，
    ///      且统计时**排除当前这次 `cardPlay`** —— 所以同一次打牌里的多条格挡会一起翻倍
    ///
    /// 预览时按"本回合已发生的条数 < 层数"判断即可：
    /// 当前 cardPlay 还没进 History，等价于游戏里"num 不含本次"的算法。
    ///
    /// ⚠️ 卡面 `BlockVar.PreviewValue` 里**已经含**这次翻倍
    /// （`UpdateCardPreview` 会调 `Hook.ModifyBlock(..., card, null, ...)`，
    ///  cardPlay 传 null 时所有本回合条目都被计入，与游戏判定一致），
    /// 所以这个乘数只能用于**自己算**的卡牌来源段：目前是拳斗的"伤害转格挡"
    /// 和蜃景的毒层手算回退路径。
    /// </summary>
    private static int GetUnmovableMultiplier(Creature? creature)
    {
        if (creature == null)
        {
            return 1;
        }

        var power = creature.GetPower<UnmovablePower>();

        if (power == null || power.Amount <= 0)
        {
            return 1;
        }

        try
        {
            var state = creature.CombatState;

            if (state == null)
            {
                return 1;
            }

            var blockSnap = creature.Player != null ? GetHistorySnapshot(state, creature.Player) : null;

            int already;

            if (blockSnap != null)
            {
                already = blockSnap.CardBlockGainedThisTurn;
            }
            else
            {
                already = CombatManager.Instance.History.Entries
                    .OfType<BlockGainedEntry>()
                    .Count(e => e.HappenedThisTurn(state)
                                && e.CardPlay != null
                                && e.CardPlay.Player.Creature == creature
                                && e.Props.IsCardOrMonsterMove());
            }

            return already < power.Amount ? 2 : 1;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 不动判定失败: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 骇人头盔（IntimidatingHelmet）：打出耗能 ≥ 「阈值」的牌（默认 2），获得「层数」格挡（默认 4）。
    ///
    /// 游戏里判的是 `cardPlay.Resources.EnergyValue`（实际消耗，含减费），
    /// 预览用 `EnergyCost.GetResolved()`；X 费牌按当前能量估。
    /// </summary>
    private static int GetIntimidatingHelmetBlock(CardModel card, Creature? creature, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<IntimidatingHelmet>(creature?.Player);

        if (relic == null)
        {
            return 0;
        }

        int threshold = Math.Max(0, ReadRelicVar(relic, "Energy"));
        int perBlock = ReadRelicBlock(relic);

        if (perBlock <= 0)
        {
            return 0;
        }

        int cost = card.EnergyCost.CostsX
            ? Math.Max(0, creature?.Player?.PlayerCombatState?.Energy ?? 0)
            : card.EnergyCost.GetResolved();

        if (cost < threshold)
        {
            return 0;
        }

        icon = relic.Icon;
        return perBlock * Math.Max(1, totalPlays);
    }

    /// <summary>这张牌打出去会不会让奥斯提（骨手）发动攻击。</summary>
    private static bool IsOstyAttackCard(CardModel card)
    {
        try
        {
            foreach (var tag in card.Tags)
            {
                if (tag == CardTag.OstyAttack)
                {
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取卡牌标签失败: {e.Message}");
        }

        return false;
    }

    /// <summary>
    /// 骨笛（BoneFlute）：挂在 AfterAttack —— 攻击者是奥斯提、主人是自己，
    /// 每次攻击命令就 +BlockVar(2) 点格挡。
    ///
    /// 判断用 <c>CardTag.OstyAttack</c>：所有"让奥斯提攻击"的牌都带这个标签
    /// （游戏里它们清一色 `CanonicalTags => { CardTag.OstyAttack }`
    /// + `DamageCmd.Attack(...).FromOsty(...)`）。
    /// 比逐个匹配卡牌类型稳，mod 新卡只要打了这个标签也能自动覆盖。
    /// </summary>
    private static int GetBoneFluteBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<BoneFlute>(player);

        if (relic == null)
        {
            return 0;
        }

        int perAttack = ReadRelicBlock(relic);

        if (perAttack <= 0 || !IsOstyAttackCard(card))
        {
            return 0;
        }

        icon = relic.Icon;
        return perAttack * Math.Max(1, totalPlays);
    }

    /// <summary>
    /// 斗篷扣（CloakClasp）：BeforeSideTurnEnd —— 回合结束时手牌每有 1 张 +BlockVar(1)。
    ///
    /// 属于"回合结束才结算"，所以只在玩家回合显示（同覆甲）。
    /// <paramref name="afterPlay"/> = true 时按"打出这张牌之后"的手牌数算（它自己已离手）；
    /// 常驻显示（没在拖牌）时传 false，直接用当前手牌数。
    /// <paramref name="extraHandDelta"/> 用于选牌界面：被拿起的牌虽然还挂在 Hand pile 里，
    /// 但它马上会被弃掉，回合结束时不在了，所以要按负数扣减。
    /// 游戏里有 `if (cards.Count != 0)` 的守卫，空手牌不给格挡。
    /// </summary>
    private static int GetCloakClaspBlock(Player? player, out Texture2D? icon, bool afterPlay = true, int extraHandDelta = 0)
    {
        icon = null;

        var relic = GetRelic<CloakClasp>(player);

        if (relic == null || player == null)
        {
            return 0;
        }

        int perCard = ReadRelicBlock(relic);

        if (perCard <= 0)
        {
            return 0;
        }

        int hand = PileType.Hand.GetPile(player).Cards.Count;
        int effective = Math.Max(0, (afterPlay ? hand - 1 : hand) + extraHandDelta);

        if (effective <= 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return effective * perCard;
    }

    /// <summary>
    /// 风的女儿（DaughterOfTheWind）：AfterCardPlayed —— 每打出一张攻击牌 +BlockVar(1)。
    /// </summary>
    private static int GetWindDaughterBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<DaughterOfTheWind>(player);

        if (relic == null)
        {
            return 0;
        }

        int perAttack = ReadRelicBlock(relic);

        if (perAttack <= 0 || card.Type != CardType.Attack)
        {
            return 0;
        }

        icon = relic.Icon;
        return perAttack * Math.Max(1, totalPlays);
    }

    /// <summary>
    /// 奥里哈刚类遗物：回合结束时**一点格挡都没有**才给格挡。
    ///
    /// 真品 <see cref="Orichalcum"/>（6 点）和假商人卖的赝品
    /// <see cref="FakeOrichalcum"/>（3 点）**逻辑完全一样**，只是数值和图标不同，
    /// 所以用同一个泛型函数各算一次；两个都持有的话游戏里会各触发一次。
    ///
    /// 游戏里它在 <c>BeforeSideTurnEndVeryEarly</c> 判断 `Owner.Creature.Block > 0`，
    /// 时机**早于覆甲**（代码注释明说这样排序是为了让覆甲不会挡住它）。
    /// 所以"覆甲 / 冰球被动 / 无惧疼痛 / 斗篷扣 / 波纹水盆"这些回合结束才加的格挡
    /// **不能**算进"已有格挡"，否则会误判成"被挡住了"。
    ///
    /// otherBlock 由调用方传入：当前格挡 + 打这张牌**立即**获得的格挡
    /// （不含任何回合结束才结算的段），从而避免和本函数互相依赖。
    /// </summary>
    private static int GetOrichalcumLikeBlock<T>(Player? player, int otherBlock, out Texture2D? icon)
        where T : RelicModel
    {
        icon = null;

        var relic = GetRelic<T>(player);

        if (relic == null)
        {
            return 0;
        }

        int amount = ReadRelicBlock(relic);

        if (amount <= 0)
        {
            return 0;
        }

        // 已经有格挡了 → 这一回合不会触发
        if (otherBlock > 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return amount;
    }

    /// <summary>
    /// 波纹水盆（RippleBasin）：回合结束时**本回合没打出过攻击牌**才 +BlockVar(4)。
    ///
    /// 判定照抄游戏：查 History.CardPlaysFinished 里本回合有没有自己打出的攻击牌；
    /// 另外正在拖的这张如果本身就是攻击牌，打出后就"有攻击牌了"，也不该触发。
    /// 常驻显示（<paramref name="card"/> 为 null）时只看历史记录。
    /// </summary>
    private static int GetRippleBasinBlock(CardModel? card, Player? player, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<RippleBasin>(player);

        if (relic == null || player == null)
        {
            return 0;
        }

        int amount = ReadRelicBlock(relic);

        if (amount <= 0)
        {
            return 0;
        }

        if (card != null && card.Type == CardType.Attack)
        {
            return 0;
        }

        if (WasAttackPlayedThisTurn(player.Creature.CombatState, player))
        {
            return 0;
        }

        icon = relic.Icon;
        return amount;
    }

    /// <summary>本回合玩家已经打出过攻击牌吗（波纹水盆的判据）。</summary>
    private static bool WasAttackPlayedThisTurn(ICombatState? combatState, Player? player)
    {
        if (combatState == null || player == null)
        {
            return false;
        }

        var playedSnap = GetHistorySnapshot(combatState, player);
        if (playedSnap != null)
        {
            return playedSnap.AttackPlayedThisTurn;
        }

        try
        {
            return CombatManager.Instance.History.CardPlaysFinished.Any(
                e => e.HappenedThisTurn(combatState)
                     && e.CardPlay.Card.Type == CardType.Attack
                     && e.CardPlay.Player == player);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 查询本回合攻击牌失败: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 结实绷带（ToughBandages）：AfterCardDiscarded —— 回合内每弃 1 张牌 +BlockVar(3)。
    ///
    /// 弃牌张数沿用奇巧那套 <see cref="GetDiscardCount"/>：
    /// 弃全部、或手牌数不足时张数是确定的；
    /// 会弹二级选牌界面的情况交给选牌界面去算（和奇巧一致）。
    /// </summary>
    private static int GetToughBandagesBlock(CardModel card, Player? player, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        var relic = GetRelic<ToughBandages>(player);

        if (relic == null)
        {
            return 0;
        }

        int perCard = ReadRelicBlock(relic);

        if (perCard <= 0)
        {
            return 0;
        }

        int discarded = GetDiscardCount(card);

        if (discarded <= 0)
        {
            return 0;
        }

        icon = relic.Icon;
        return perCard * discarded * Math.Max(1, totalPlays);
    }

    /// <summary>
    /// 用游戏自己的 `Hook.ModifyBlock` 算出某张牌 `BlockVar` 的**最终格挡值**。
    ///
    /// 用于牌堆里的牌（如抽牌堆中的「所向无敌」）：它们不渲染卡面，
    /// `UpdateCardPreview` 不会跑，`PreviewValue` 可能停在未更新的默认值。
    /// 这里手工走一次 Hook，敏捷 / 脆弱 / 不动 / 融入暗影等修正都能算进来。
    /// `cardPlay` 传 null，与游戏"排除当前正在打出的 cardPlay"等价。
    /// </summary>
    private static int ComputeCardBlockValue(CardModel card, BlockVar blockVar)
    {
        try
        {
            var creature = card.Owner?.Creature;
            var combatState = card.CombatState;

            if (creature == null || combatState == null)
            {
                return Math.Max(0, blockVar.IntValue);
            }

            decimal value = Hook.ModifyBlock(
                combatState, creature, blockVar.BaseValue, blockVar.Props, card, null, out _);

            return Math.Max(0, (int)Math.Floor(value));
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 计算卡牌格挡值失败: {e.Message}");
            return Math.Max(0, blockVar.IntValue);
        }
    }

    private static void GetInvinciblePreview(Player? player, out int block, out int percent, out Texture2D? icon)
    {
        block = 0;
        percent = -1;
        icon = null;

        if (player == null)
        {
            return;
        }

        PileStats? stats;

        try
        {
            stats = GetPileStats(PileType.Draw.GetPile(player));
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取抽牌堆失败: {e.Message}");
            return;
        }

        if (stats == null || stats.Total <= 0 || stats.Invincibles <= 0 || stats.InvincibleBlock <= 0)
        {
            return;
        }

        // 故意不判断"是否已经在牌堆顶部"：那样会变成透视，告诉玩家顶上就是所向无敌。
        // 一律按"它在洗牌后落到顶部的概率"显示。
        percent = (int)Math.Round(100.0 * stats.Invincibles / stats.Total, MidpointRounding.AwayFromZero);
        block = stats.InvincibleBlock;
        icon = TryGetEnchantmentIcon<PerfectFit>();
    }

    /// <summary>抽牌堆里还有没有「所向无敌」（IAmInvincible）。</summary>
    private static bool HasInvincibleInDrawPile()
    {
        var player = _playerCreature?.Player;

        if (player == null)
        {
            return false;
        }

        try
        {
            var stats = GetPileStats(PileType.Draw.GetPile(player));
            return stats != null && stats.Invincibles > 0;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 检查所向无敌失败: {e.Message}");
        }

        return false;
    }

    private static Texture2D? TryGetEnchantmentIcon<TEnchantment>() where TEnchantment : EnchantmentModel
    {
        try
        {
            return ModelDb.Enchantment<TEnchantment>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取 {typeof(TEnchantment).Name} 附魔图标失败: {e.Message}");
            return null;
        }
    }

    private static Texture2D? TryGetPowerIcon<TPower>() where TPower : PowerModel
    {
        try
        {
            return ModelDb.Power<TPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取 {typeof(TPower).Name} 图标失败: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 当前是不是"玩家还在自己的回合里"。
    ///
    /// 覆甲/冰球这类"回合结束结算"的段只在这时才有意义。
    /// 注意要额外看 <see cref="CombatManager.IsPlayerReadyToEndTurn"/>：
    /// 玩家按下结束回合后，冰球会先结算、格挡先涨上去，而 <c>CurrentSide</c>
    /// 要等 PhaseTwo 才切到 Enemy——这段窗口里如果只看 CurrentSide，
    /// 会把刚结算的护盾再叠加一次（表现为 +6 冰球 =14 这种瞬间跳变）。
    /// </summary>
    private static bool IsPlayerTurn()
    {
        try
        {
            var combatState = _playerCreature?.CombatState;

            if (combatState == null || combatState.CurrentSide != CombatSide.Player)
            {
                return false;
            }

            var player = _playerCreature?.Player;

            if (player != null && CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按配置开关决定是否显示某一段。</summary>
    private static int Show(bool enabled, int value)
    {
        return enabled ? value : 0;
    }

    /// <summary>
    /// 覆甲（PlatingPower）：每回合开始时减少 1 层，回合结束时获得等同层数的格挡。
    /// 没有这个 buff 时返回 0，面板上就不显示这一段。
    /// </summary>
    private static int GetPlatingBlock(Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (creature == null)
        {
            return 0;
        }

        var power = creature.GetPower<PlatingPower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return power.Amount;
    }

    /// <summary>
    /// 打出这张牌会**额外给多少层覆甲**（`PlatingPower`）。
    ///
    /// 覆甲在 `BeforeSideTurnEndEarly` 用**当时的 `Amount`** 换成等量格挡
    /// （`CreatureCmd.GainBlock(..., base.Amount, ValueProp.Unpowered, null)`），
    /// 所以"这张牌给的层数"就是它本回合末额外带来的护盾，
    /// 而 `StackType` 是 `Counter`，会与玩家身上已有的覆甲**直接相加**。
    ///
    /// 游戏里直接给覆甲的只有这 3 张（全量扫 `PowerCmd.Apply&lt;PlatingPower&gt;`）：
    ///   - `EternalArmor` 9（升级 12）
    ///   - `NeutronAegis` 8（升级 11）
    ///   - `StoneArmor` 4（升级 6）
    ///
    /// 用类型白名单而不是"有 `PlatingPower` 变量就算"——数值仍读变量（升级自动跟上），
    /// 但触发条件认牌型，避免将来出现"带变量却不给覆甲"的牌被误报。
    /// </summary>
    private static int GetCardPlatingBlock(CardModel card, int totalPlays, out Texture2D? icon)
    {
        icon = null;

        if (card is not (EternalArmor or NeutronAegis or StoneArmor))
        {
            return 0;
        }

        int perPlay = ReadDynamicVar(card, "PlatingPower");

        if (perPlay <= 0)
        {
            return 0;
        }

        icon = ModelDb.Power<PlatingPower>().Icon;
        return perPlay * Math.Max(1, totalPlays);
    }

    /// <summary>
    /// 蜃景（Mirage）：获得等同于所有存活敌人中毒层数之和的格挡。
    ///
    /// 游戏里它是个 CalculatedBlockVar，multiplier 取「所有存活敌人毒层总和」，
    /// 所以卡面数字本身就是这个值（可能还含敏捷等 ModifyBlock 修正）。
    /// 这里优先用卡面算好的 PreviewValue 保证和卡面一致；
    /// 卡面还没刷新（PreviewValue 偏小）时回退到手算的毒层总和。
    /// </summary>
    private static int GetMirageBlock(CardModel card, out Texture2D? icon)
    {
        icon = null;

        if (card is not Mirage)
        {
            return 0;
        }

        int poison = 0;
        var combatState = card.CombatState;

        if (combatState != null)
        {
            foreach (var enemy in combatState.Enemies)
            {
                if (enemy.IsAlive)
                {
                    poison += enemy.GetPowerAmount<PoisonPower>();
                }
            }
        }

        int value = poison * GetBlockGainMultiplier(card.Owner?.Creature);

        // 蜃景是 CalculatedBlockVar(Move)，属于「卡牌来源格挡」，
        // 上面的 PreviewValue 分支已含「不动」的翻倍；这条手算回退路径要自己补。
        value *= GetUnmovableMultiplier(card.Owner?.Creature);

        if (card.DynamicVars.TryGetValue(CalculatedBlockVar.defaultName, out var cbv)
            && cbv is CalculatedBlockVar calcBlock)
        {
            int preview = (int)Math.Floor(calcBlock.PreviewValue);
            if (preview > value)
            {
                value = preview;
            }
        }

        if (value <= 0)
        {
            return 0;
        }

        icon = GetPoisonIcon();
        return value;
    }

    /// <summary>
    /// 逃脱计划（EscapePlan）：抽 1 张，抽到的牌是技能牌时才获得 BlockVar 点格挡。
    ///
    /// 按要求面板显示「满额格挡 + 命中概率」，不按期望折算（不乘概率）。
    /// 概率 = 抽牌堆技能牌数 / 抽牌堆总数；
    /// 抽牌堆空时抽牌会先把弃牌堆洗回来，所以改用弃牌堆的比例。
    ///
    /// 只要这一抽根本不会发生（抽牌堆和弃牌堆都空、本回合不能抽牌），
    /// 就整段隐藏——连格挡数字也不显示，因为实际打出去拿不到格挡。
    /// </summary>
    private static int GetEscapePlanBlock(CardModel card, out int percent)
    {
        percent = -1;

        if (card is not EscapePlan)
        {
            return 0;
        }

        var player = card.Owner;

        if (player == null)
        {
            return 0;
        }

        int chance = GetDrawSkillPercent(player);

        if (chance < 0)
        {
            return 0;
        }

        int block = 0;

        if (card.DynamicVars.TryGetValue(BlockVar.defaultName, out var bv) && bv is BlockVar blockVar)
        {
            block = (int)Math.Floor(blockVar.PreviewValue);
        }

        if (block <= 0)
        {
            return 0;
        }

        percent = chance;
        return block;
    }

    /// <summary>
    /// 抽到的牌是技能牌的概率（百分比）。
    /// 返回 -1 表示这一抽不会发生，调用方应整段隐藏：
    ///   抽牌堆和弃牌堆都空 / 本回合不能抽牌（NoDrawPower）。
    ///
    /// 注意：这里不检查手牌是否已满——打出这张牌的瞬间它已经离开手牌，
    /// 手牌 9 张时会先空出一格再抽，所以手牌满不构成阻塞。
    /// </summary>
    private static int GetDrawSkillPercent(Player player)
    {
        if (player.Creature.HasPower<NoDrawPower>())
        {
            return -1;
        }

        var drawStats = GetPileStats(PileType.Draw.GetPile(player));
        int total = drawStats?.Total ?? 0;
        int skills = drawStats?.Skills ?? 0;

        if (total == 0)
        {
            var discardStats = GetPileStats(PileType.Discard.GetPile(player));
            total = discardStats?.Total ?? 0;
            skills = discardStats?.Skills ?? 0;
        }

        if (total == 0)
        {
            return -1;
        }

        int rounded = (int)Math.Round(100.0 * skills / total, MidpointRounding.AwayFromZero);

        // 只有「抽牌堆 / 弃牌堆里全都是技能牌」才算真正 100%。
        // 四舍五入会把 199/200 = 99.5% 舍成 100%，那会被上层误判成"确定收益"
        // 塞进第一行，所以这里把它压到 99 —— 100% 这个值必须严格可信。
        if (skills < total && rounded >= 100)
        {
            rounded = 99;
        }

        return rounded;
    }

    /// <summary>
    /// 死亡之舞（DanseMacabrePower）：打出费用 >= 阈值的牌时，获得等同于层数的格挡。
    /// 判定条件照抄游戏里的 BeforeCardPlayed：
    ///   card.EnergyCost.GetResolved() >= power.DynamicVars.Energy.IntValue
    ///
    /// X 费卡要特殊处理：CapturedXValue 只在打出时（SpendEnergy）才被赋值，
    /// 而卡牌实例是持久的——一张打出过又被洗回手牌的 X 费卡，会残留上一次的 X 值。
    /// 直接读 GetResolved() 就会"有时显示、有时不显示"。这里改用当前能量作为上限预估。
    /// </summary>
    private static int GetDanseMacabreBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<DanseMacabrePower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        int threshold = power.DynamicVars.Energy.IntValue;

        int cost;
        if (card.EnergyCost.CostsX)
        {
            cost = creature?.Player?.PlayerCombatState?.Energy ?? 0;
        }
        else
        {
            cost = card.EnergyCost.GetResolved();
        }

        if (cost < threshold)
        {
            return 0;
        }

        icon = power.Icon;
        return power.Amount;
    }

    /// <summary>
    /// 狂怒（RagePower）：打出攻击牌时获得等同于层数的格挡，己方回合结束时移除。
    /// 判定照抄 AfterCardPlayed：cardPlay.Card.Type == CardType.Attack。
    /// </summary>
    private static int GetRageBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<RagePower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        if (card.Type != CardType.Attack)
        {
            return 0;
        }

        icon = power.Icon;
        return power.Amount;
    }

    /// <summary>
    /// 余像（AfterimagePower）：打出任意一张牌后获得等同于层数的格挡。
    /// 判定照抄 AfterCardPlayed：只要是自己打出的牌就触发。
    /// </summary>
    private static int GetAfterimageBlock(Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<AfterimagePower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return power.Amount;
    }

    /// <summary>
    /// 无惧疼痛（FeelNoPainPower）：卡牌被消耗时获得等同于层数的格挡。
    /// 这里预估的是"回合结束时手牌里的虚无卡会消耗"带来的收益：
    ///   手牌中虚无（CardKeyword.Ethereal）数量 × 层数
    /// 正在拖出的那张牌不算进去（它会被打出而不是留到回合结束）。
    /// </summary>
    private static int GetFeelNoPainBlock(CardModel? card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        var power = creature?.GetPower<FeelNoPainPower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        // 恶魔之焰会把整手牌烧掉，那部分统一由 FiendFireBlock 计算。
        // 这里只算恶魔之焰自身那一次消耗，避免两边重复计算。
        if (card is FiendFire)
        {
            icon = power.Icon;
            return power.Amount;
        }

        var player = creature?.Player;
        if (player == null)
        {
            return 0;
        }

        int handCount = 0;
        int etherealCount = 0;

        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            if (handCard == card)
            {
                continue;
            }

            handCount++;

            if (handCard.Keywords.Contains(CardKeyword.Ethereal))
            {
                etherealCount++;
            }
        }

        // 添材（Stoke）：烧光手牌，但自身不消耗。
        // 每烧一张：黑暗之拥抽 1 张（× 层数），添材自己再生成 1 张，
        // 所以下一次重放时手上的牌会变多（受 10 张上限约束）。
        // 例：手牌 5、黑暗之拥 1 层、重放 2 次 → 烧 5、再烧 10，合计 15 次触发。
        if (card is Stoke)
        {
            if (handCount <= 0)
            {
                return 0;
            }

            int darkEmbrace = Math.Max(0, creature!.GetPowerAmount<DarkEmbracePower>());
            int plays = GetPlayCount(card, creature);

            int totalBurned = 0;
            int currentHand = handCount;

            for (int i = 0; i < plays; i++)
            {
                totalBurned += currentHand;
                currentHand = Math.Min(currentHand * (1 + darkEmbrace), MaxHandSize);
            }

            icon = power.Icon;
            return totalBurned * power.Amount;
        }

        // 破灭（Havoc）：打出抽牌堆顶部一张并强制消耗。
        // 抽牌堆空了就没有牌可消耗，也就不会触发无惧疼痛。
        if (card is Havoc)
        {
            if (PileType.Draw.GetPile(player).Cards.Count == 0)
            {
                return 0;
            }

            icon = power.Icon;
            return power.Amount * GetPlayCount(card, creature);
        }

        // 痛殴（Thrash）：从手牌随机消耗一张攻击牌。
        // 手里没有攻击牌（除了它自己）就不消耗。
        if (card is Thrash)
        {
            bool hasAttack = false;

            foreach (var handCard in PileType.Hand.GetPile(player).Cards)
            {
                if (handCard != card && handCard.Type == CardType.Attack)
                {
                    hasAttack = true;
                    break;
                }
            }

            if (!hasAttack)
            {
                return 0;
            }

            icon = power.Icon;
            return power.Amount * GetPlayCount(card, creature);
        }

        // 重振精神（SecondWind）：消耗手牌里所有非攻击牌，每张触发一次无惧疼痛。
        // 同样受黑暗之拥影响：没有补牌时，第二轮就没有牌可烧了。
        if (card is SecondWind)
        {
            int nonAttackCount = 0;

            foreach (var handCard in PileType.Hand.GetPile(player).Cards)
            {
                if (handCard != card && handCard.Type != CardType.Attack)
                {
                    nonAttackCount++;
                }
            }

            if (nonAttackCount <= 0)
            {
                return 0;
            }

            // 只算第一轮；重放轮的期望值单独显示
            icon = power.Icon;
            return nonAttackCount * power.Amount;
        }

        // 净化（Purity）：打出后选择最多 Cards 张牌消耗。
        // 拖牌阶段还没选，就按"选满"给上限收益，再加上它自身被消耗的那一次。
        if (card is Purity)
        {
            int maxSelect = 0;

            if (card.DynamicVars.TryGetValue(CardsVar.defaultName, out var cardsVar) && cardsVar is CardsVar cv)
            {
                maxSelect = cv.IntValue;
            }

            int total = maxSelect + 1;
            icon = power.Icon;
            return total * power.Amount * GetPlayCount(card, creature);
        }

        // 怒放（Florescence）：消耗**所有**带 `CardTag.Shoot` 且不在消耗堆的牌
        // （`Florescence.OnPlay` 里遍历 `PlayerCombatState.AllCards`）——
        // 手牌 / 抽牌堆 / 弃牌堆 / 打出堆全算，**包括以前生成过、还没消耗的 `TheShot`**。
        // 每张触发一次无惧疼痛，然后它自己也会因为自带 Exhaust 被消耗一次。
        //
        // 两个细节：
        //   - 手牌里的**虚无** Shoot 牌本来回合结束也要消耗，提前烧掉是净增 0，
        //     它们已经计入 `etherealCount`，这里要减掉避免重复
        //   - 多段播放时第一次就烧光了，后面的播放没有 Shoot 牌可烧，
        //     所以 Shoot 张数只算一次（自身 Exhaust 仍按播放次数算）
#if STS2_FOOL
        if (card is Florescence)
        {
            int plays = GetPlayCount(card, creature);
            int shootCount = CountShootCardsOutsideExhaust(player);
            int etherealShoot = CountEtherealShootInHand(card, player);

            int total = etherealCount + Math.Max(0, shootCount - etherealShoot) + plays;

            if (total <= 0)
            {
                return 0;
            }

            icon = power.Icon;
            return total * power.Amount;
        }
#endif

        int count = etherealCount;

        if (card != null)
        {
            int plays = GetPlayCount(card, creature);

            // 这张牌打出后自身会消耗（每次播放都触发一次）
            if (card.ExhaustOnNextPlay || card.Keywords.Contains(CardKeyword.Exhaust))
            {
                count += plays;
            }

            // 这张牌会额外消耗一张"别的"牌（坚毅 / 燃烧契约 / 烙印 / 净化 / 拾荒 等），
            // 但前提是手里确实还有别的牌可烧 —— 手牌只剩它自己时，
            // 打出去手牌就空了，根本没有牌可选，无惧疼痛也就不会触发。
            if (ExhaustsOneExtraCard(card) && handCount > 0)
            {
                count += plays;
            }
        }

        if (count <= 0)
        {
            return 0;
        }

        icon = power.Icon;
        return count * power.Amount;
    }

    /// <summary>
    /// 「怒放」会消耗的牌数：所有带 `CardTag.Shoot` 且**不在消耗堆**里的牌。
    /// 复刻 `Florescence.OnPlay` 的判据（遍历 `PlayerCombatState.AllCards`），
    /// 所以手牌 / 抽牌堆 / 弃牌堆 / 打出堆里的射击牌都算，
    /// **包括以前生成过、还没消耗的 `TheShot`**（它自己也带 `CardTag.Shoot`）。
    /// </summary>
    private static int CountShootCardsOutsideExhaust(Player player)
    {
#if STS2_FOOL
        try
        {
            var state = player.PlayerCombatState;

            if (state == null)
            {
                return 0;
            }

            int count = 0;

            foreach (var c in state.AllCards)
            {
                if (c.Tags.Contains(CardTag.Shoot) && c.Pile?.Type != PileType.Exhaust)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计射击牌失败: {e.Message}");
            return 0;
        }
#else
        return 0;
#endif
    }

    /// <summary>
    /// 手牌里「**既是虚无、又带 Shoot 标签**」的牌数。
    /// 这些牌被怒放提前烧掉是**净增 0**（回合结束本来也要烧），
    /// 它们已经计入 `etherealCount`，所以要从怒放的消耗数里减掉，避免重复。
    /// </summary>
    private static int CountEtherealShootInHand(CardModel? card, Player player)
    {
#if STS2_FOOL
        try
        {
            int count = 0;

            foreach (var handCard in PileType.Hand.GetPile(player).Cards)
            {
                if (handCard == card)
                {
                    continue;
                }

                if (handCard.Keywords.Contains(CardKeyword.Ethereal)
                    && handCard.Tags.Contains(CardTag.Shoot))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计虚无射击牌失败: {e.Message}");
            return 0;
        }
#else
        return 0;
#endif
    }

    /// <summary>
    /// 手牌里有多少张虚无牌（Ethereal），排除正在拖的那张。
    /// 这些牌会在**回合结束**被消耗，触发无惧疼痛——但那发生在奥里哈刚判断之后，
    /// 所以判断"回合结束时是否已有格挡"时不能算它们。
    /// </summary>
    private static int CountEtherealInHand(CardModel? card, Creature? creature)
    {
        var player = creature?.Player;

        if (player == null)
        {
            return 0;
        }

        int count = 0;

        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            if (handCard == card)
            {
                continue;
            }

            if (handCard.Keywords.Contains(CardKeyword.Ethereal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 无惧疼痛里**打这张牌立即就会触发**的那部分格挡。
    ///
    /// 用于奥里哈刚的判断：它只关心"回合结束那一刻 Block 是否 > 0"，
    /// 而手牌里的虚无牌是等到回合结束**之后**才消耗的，赶不上这次判断。
    ///
    /// 所以：
    ///   添柴 / 重振精神 / 恶魔之焰 / 破灭 / 痛殴 / 净化 → 收益全发生在打牌时，整个都算
    ///   其余（通用分支）                                → 扣掉「手牌虚无牌」那部分
    /// </summary>
    /// <param name="blockMultiplier">
    /// 传给 <paramref name="feelNoPainBlock"/> 时用的那个融合暗影倍率。
    /// 扣减虚无牌部分必须用同一个倍率，否则会扣少、留下虚假正数。
    /// </param>
    private static int GetFeelNoPainImmediateBlock(CardModel? card, Creature? creature,
        int feelNoPainBlock, int blockMultiplier)
    {
        if (feelNoPainBlock <= 0)
        {
            return 0;
        }

        // 这些牌的烧牌全部发生在打出时，没有"回合结束才消耗"的成分。
        // 怒放（Florescence）也归这一类：它在 OnPlay 里立刻烧光所有射击牌。
        // （严格说它算的 `etherealCount` 里还有非射击的虚无牌属于回合结束，
        //  这里按"全部立即"处理偏保守 —— 只有非射击虚无牌很多时才会影响奥里哈刚判断。）
#if STS2_FOOL
        if (card is Stoke or SecondWind or FiendFire or Havoc or Thrash or Purity or Florescence)
#else
        if (card is Stoke or SecondWind or FiendFire or Havoc or Thrash or Purity)
#endif
        {
            return feelNoPainBlock;
        }

        var power = creature?.GetPower<FeelNoPainPower>();

        if (power == null || power.Amount <= 0)
        {
            return feelNoPainBlock;
        }

        // feelNoPainBlock 传进来时**已经乘过融入暗影**（Shadowmeld），
        // 所以这里扣掉的「手牌虚无牌」部分必须用同一个倍率（由调用方传入，
        // 已包含"正在拖的这张牌会不会叠层"）。
        // 否则倍率 > 1 时只扣一倍，会留下一个虚假的正数，
        // 把本该触发的奥里哈刚错误地压掉。
        int etherealPart = CountEtherealInHand(card, creature) * power.Amount
                           * Math.Max(1, blockMultiplier);

        return Math.Max(0, feelNoPainBlock - etherealPart);
    }

    /// <summary>
    /// 重振精神的逐轮烧牌总数。
    /// 它烧完不会生成新牌，唯一能补回手牌的是黑暗之拥（每烧一张抽「层数」张）。
    /// 所以没有黑暗之拥时，重放轮能烧的就是 0。
    /// </summary>
    private static int CountSecondWindBurns(CardModel card, Creature? creature, int initialCount)
    {
        if (initialCount <= 0)
        {
            return 0;
        }

        int darkEmbrace = Math.Max(0, creature?.GetPowerAmount<DarkEmbracePower>() ?? 0);
        int plays = GetPlayCount(card, creature);

        int total = 0;
        int current = initialCount;

        for (int i = 0; i < plays; i++)
        {
            total += current;
            current = Math.Min(current * darkEmbrace, MaxHandSize);
        }

        return total;
    }

    /// <summary>
    /// 重振精神重放轮的期望收益（显示为 +N?）。
    ///
    /// 第一轮烧掉的是手牌里的非攻击牌，黑暗之拥把它们的位置换成抽回来的牌，
    /// 但抽到的可能是攻击牌（不会被烧），所以只能用期望：
    ///   期望烧掉张数 = 抽牌数 × (牌库非攻击牌 / 牌库总数)
    ///   期望格挡     = 期望烧掉张数 × (卡牌格挡 + 无惧疼痛层数)
    /// 多轮重放时逐轮累进，手牌上限按 10 处理。
    /// </summary>
    private static int GetSecondWindReplayBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (card is not SecondWind)
        {
            return 0;
        }

        var power = creature?.GetPower<FeelNoPainPower>();
        if (power == null || power.Amount <= 0 || creature == null)
        {
            return 0;
        }

        var player = creature.Player;
        if (player == null)
        {
            return 0;
        }

        int plays = GetPlayCount(card, creature);
        if (plays <= 1)
        {
            return 0;
        }

        int darkEmbrace = creature.GetPowerAmount<DarkEmbracePower>();
        if (darkEmbrace <= 0)
        {
            return 0;
        }

        // 第一次能烧掉的非攻击牌
        int firstBurned = 0;
        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            if (handCard != card && handCard.Type != CardType.Attack)
            {
                firstBurned++;
            }
        }

        if (firstBurned <= 0)
        {
            return 0;
        }

        // 抽牌是"先抽抽牌堆，抽空了才把弃牌堆洗进来"，所以要分开统计。
        // 走牌堆统计缓存：和逃脱计划 / 所向无敌共用同一次遍历结果。
        var drawStats = GetPileStats(PileType.Draw.GetPile(player));
        int drawCount = drawStats?.Total ?? 0;
        int drawNonAttack = drawStats?.NonAttacks ?? 0;

        var discardStats = GetPileStats(PileType.Discard.GetPile(player));
        int discardCount = discardStats?.Total ?? 0;
        int discardNonAttack = discardStats?.NonAttacks ?? 0;

        if (drawCount + discardCount <= 0)
        {
            return 0;
        }

        double drawRatio = drawCount > 0 ? (double)drawNonAttack / drawCount : 0;
        double discardRatio = discardCount > 0 ? (double)discardNonAttack / discardCount : 0;

        if (drawRatio <= 0 && discardRatio <= 0)
        {
            return 0;
        }

        // 每张非攻击牌给多少格挡（卡牌本身 + 无惧疼痛）
        int blockVar = 0;
        if (card.DynamicVars.TryGetValue(BlockVar.defaultName, out var bv) && bv is BlockVar block)
        {
            blockVar = (int)Math.Floor(block.PreviewValue);
        }

        // 卡面 PreviewValue 已含融入暗影的翻倍；无惧疼痛/余像这些层数是原始值，要单独乘
        int blockMultiplier = GetBlockGainMultiplier(creature);

        int perCard = blockVar + power.Amount * blockMultiplier;
        if (perCard <= 0)
        {
            return 0;
        }

        double expectedBurnedTotal = 0;
        double burned = firstBurned;

        // 牌堆剩余量：每轮抽走的牌会从对应堆里真正消失，
        // 所以库存和非攻击牌比例都得逐轮扣减，否则多轮重放会一路高估。
        double drawTotal = drawCount;
        double drawNonTotal = drawNonAttack;
        double discardTotal = discardCount;
        double discardNonTotal = discardNonAttack;

        // 每多打出一次重振精神，这些"打出牌时触发"的 power 也会各触发一次，
        // 和烧掉多少张无关，所以单独按轮数累加。
        int perPlayBonus = 0;
        perPlayBonus += GetAfterimageBlock(creature, out _);
        perPlayBonus += GetRageBlock(card, creature, out _);
        perPlayBonus += GetDanseMacabreBlock(card, creature, out _);
        perPlayBonus *= blockMultiplier;

        for (int i = 1; i < plays; i++)
        {
            double drawn = Math.Min(burned * darkEmbrace, MaxHandSize);
            double expectedBurned = 0;

            // 先从抽牌堆抽
            double fromDraw = Math.Min(drawn, drawTotal);

            if (fromDraw > 0 && drawTotal > 0)
            {
                double ratio = drawNonTotal / drawTotal;
                expectedBurned += fromDraw * ratio;
                drawNonTotal -= fromDraw * ratio;
                drawTotal -= fromDraw;
            }

            // 抽空了才把弃牌堆洗回来接着抽
            double fromDiscard = drawn - fromDraw;

            if (fromDiscard > 0 && discardTotal > 0)
            {
                double ratio = discardNonTotal / discardTotal;
                expectedBurned += fromDiscard * ratio;
                discardNonTotal -= fromDiscard * ratio;
                discardTotal -= fromDiscard;
            }

            expectedBurnedTotal += expectedBurned;
            burned = expectedBurned;
        }

        double result = expectedBurnedTotal * perCard + perPlayBonus * (plays - 1);

        if (result <= 0)
        {
            return 0;
        }

        icon = GetSwiftIcon();
        return (int)Math.Round(result);
    }

    /// <summary>
    /// 这张牌实际会被执行几次。
    ///
    /// 重放 / 华彩 / 附魔走 <see cref="CardModel.GetEnchantedReplayCount"/>；
    /// 其余"多打一次"的效果——复制药水、爆发、回声形态、遗物、mod 的 power——
    /// 全部通过 <see cref="Hook.ModifyCardPlayCount"/> 汇总。
    /// 这里直接调 Hook 而不是逐个 power 特判，以后新增的效果也能自动算对。
    ///
    /// 注意：只调 <c>ModifyCardPlayCount</c>（纯读，返回次数）；
    /// 会改状态的 <c>AfterModifyingCardPlayCount</c>（爆发/复制药水在那里扣层）
    /// 是真正打出时才跑的，预览里绝对不能调，否则会白扣层数。
    /// </summary>
    public static int GetPlayCount(CardModel card, Creature? creature)
    {
        return GetPlayCount(card, creature, out _);
    }

    public static int GetPlayCount(CardModel card, Creature? creature, out List<AbstractModel>? modifiers)
    {
        modifiers = null;

        int plays = 1 + Math.Max(0, card.GetEnchantedReplayCount());
        var combatState = card.CombatState;

        if (combatState == null)
        {
            return plays;
        }

        try
        {
            // ⚠️ 用**悬停目标**而不是 `card.CurrentTarget`：
            // 后者在拖牌阶段恒为 null（见 6.21），任何依赖目标的 `ModifyCardPlayCount`
            // 实现都会算错。Ki 诗句（KiVersePower）不依赖目标，但它证明了这条链路
            // **已经自动覆盖**各种「重放 +1」的效果（不需要单独写段）。
            plays = Hook.ModifyCardPlayCount(
                combatState, card, plays, GetHoverTarget(card) ?? card.CurrentTarget, out var models);
            modifiers = models;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 计算播放次数失败: {e.Message}");
        }

        return Math.Max(1, plays);
    }

    /// <summary>
    /// 额外播放（爆发 / 复制药水 / 回声形态 / 遗物 / mod power）那几次会额外给多少格挡。
    ///
    /// 第一次播放的收益已经由各段算过，这里只补剩下的 extraPlays 次，并且按结算顺序推进状态，
    /// 而不是把总量简单乘 2：
    ///   - 重振精神：重放轮的烧牌收益由 GetSecondWindReplayBlock 按期望单独算，这里不重复加手牌格挡
    ///   - 逃脱计划：第一次已经抽走一张牌，牌堆抽干后就抽不到、拿不到格挡
    ///   - 冰球激发：由 SimulateOrbPlays 按总播放次数推演，不在这里重复
    ///
    /// percent 只在逃脱计划这种带概率的段上有意义（取第二次的概率作提示）。
    /// </summary>
    private static void GetExtraPlayBlock(
        CardModel card,
        int extraPlays,
        List<AbstractModel>? modifiers,
        int handBlockSingle,
        int mirageBlockSingle,
        int danseBlockSingle,
        int rageBlockSingle,
        int afterimageBlockSingle,
        int escapePlanBlockSingle,
        int escapePlanPercent,
        int tenderStep,
        out int extra,
        out int percent,
        out Texture2D? icon)
    {
        extra = 0;
        percent = -1;
        icon = null;

        if (extraPlays <= 0)
        {
            return;
        }

        icon = GetExtraPlayIcon(modifiers);

        // 只有「手牌格挡」受敏捷影响（卡面 `PreviewValue` 含敏捷）；
        // 狂怒 / 余像 / 死亡之舞 / 蜃景 给的是 power 层数或毒层，柔嫩减少敏捷不影响它们。
        // 额外播放的**每一次**都会触发柔嫩（敏捷各 -1），所以手牌那部分要逐次衰减 ——
        // 之前统一 `perPlay * extraPlays` 会高估。
        int dexPerPlay = handBlockSingle;
        int flatPerPlay = mirageBlockSingle + danseBlockSingle
                          + rageBlockSingle + afterimageBlockSingle;

        if (card is SecondWind)
        {
            // 重放轮（含额外播放）由 GetSecondWindReplayBlock 按期望单独显示，
            // 这里再加手牌格挡就重复了。
            dexPerPlay = 0;
        }

        int step = Math.Max(0, tenderStep);

        for (int i = 0; i < extraPlays; i++)
        {
            extra += Math.Max(0, dexPerPlay - i * step) + flatPerPlay;
        }

        if (escapePlanBlockSingle > 0)
        {
            // 第一次已经抽走一张，之后逐次判断还能不能继续抽。
            int remaining = CountCardsInPiles(card.Owner) - 1;
            int escapeExtra = 0;

            for (int i = 0; i < extraPlays; i++)
            {
                if (remaining <= 0)
                {
                    break;
                }

                // 逃脱计划也是卡牌来源的格挡，同样逐次受柔嫩衰减
                escapeExtra += Math.Max(0, escapePlanBlockSingle - i * step);
                remaining--;
            }

            extra += escapeExtra;

            // 只有真的靠逃脱计划拿到了格挡，才带上它的概率提示；
            // 否则这一段可能只是余像之类的固定值，挂个百分比会误导。
            if (escapeExtra > 0)
            {
                percent = escapePlanPercent;
            }
        }

        if (extra <= 0)
        {
            icon = null;
        }
    }

    /// <summary>
    /// 额外播放那一段的图标：看是谁在加播放次数——爆发 / 复制药水 / 回声形态。
    /// 都没有（比如某个遗物或 mod power）就退回爆发图标。
    /// </summary>
    private static Texture2D? GetExtraPlayIcon(List<AbstractModel>? modifiers)
    {
        bool hasBurst = false;
        bool hasDuplication = false;
        bool hasEchoForm = false;

        if (modifiers != null)
        {
            foreach (var modifier in modifiers)
            {
                switch (modifier)
                {
                    case BurstPower:
                        hasBurst = true;
                        break;
                    case DuplicationPower:
                        hasDuplication = true;
                        break;
                    case EchoFormPower:
                        hasEchoForm = true;
                        break;
                }
            }
        }

        try
        {
            if (hasBurst)
            {
                return ModelDb.Power<BurstPower>().Icon;
            }

            if (hasDuplication)
            {
                return ModelDb.Power<DuplicationPower>().Icon;
            }

            if (hasEchoForm)
            {
                return ModelDb.Power<EchoFormPower>().Icon;
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取额外播放图标失败: {e.Message}");
        }

        return GetBurstIcon();
    }

    /// <summary>抽牌堆 + 弃牌堆的牌数（判断还能不能继续抽）。</summary>
    private static int CountCardsInPiles(Player? player)
    {
        if (player == null)
        {
            return 0;
        }

        int total = 0;
        CountCardsInPile(PileType.Draw.GetPile(player), ref total);
        CountCardsInPile(PileType.Discard.GetPile(player), ref total);
        return total;
    }

    private static void CountCardsInPile(CardPile? pile, ref int total)
    {
        if (pile != null)
        {
            total += pile.Cards.Count;
        }
    }

    /// <summary>
    /// 这张牌会不会额外消耗一张「别的」牌。
    /// 目前覆盖游戏里用 ExhaustSelectionPrompt 手动选牌消耗的几张：
    ///   坚毅 TrueGrit / 燃烧契约 BurningPact / 烙印 Brand / Cleanse / Scavenge
    /// （它们自身不一定带 Exhaust 关键词，所以不能只靠关键词判断）
    /// </summary>
    private static bool ExhaustsOneExtraCard(CardModel card)
    {
        return card is TrueGrit or BurningPact or Brand or Cleanse or Scavenge;
    }

    /// <summary>
    /// 恶魔之焰（FiendFire）：打出手牌中所有牌都被消耗，每张都触发一次无惧疼痛。
    ///
    /// 预览值 = 当前手牌数 × 无惧疼痛层数 × 播放次数。
    /// 播放次数 = 1 + GetEnchantedReplayCount()（重放/附魔的额外次数）。
    ///
    /// 注意这是估算：实际如果配合黑暗之拥之类的抽牌，每次烧完会补充手牌，
    /// 循环下去的总量会更多，无法在拖牌阶段精确预测。
    /// </summary>
    private static int GetFiendFireBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (card is not FiendFire)
        {
            return 0;
        }

        var power = creature?.GetPower<FeelNoPainPower>();
        if (power == null || power.Amount <= 0)
        {
            return 0;
        }

        var player = creature?.Player;
        if (player == null)
        {
            return 0;
        }

        int handCount = 0;
        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            if (handCard != card)
            {
                handCount++;
            }
        }

        if (handCount <= 0)
        {
            return 0;
        }

        icon = GetDemonFormIcon();

        // 这一段只算"第一次烧手牌"的收益，重放部分由 GetFiendFireReplayBlock 单独显示
        return handCount * power.Amount;
    }

    /// <summary>
    /// 恶魔之焰重放那部分的收益（+Y）。
    ///
    /// 第一次烧牌后，黑暗之拥会把牌抽回来（每烧一张抽 Amount 张），
    /// 迅速（Swift）附魔也会在这张牌打出时抽牌，于是重放时还能再烧一轮。
    ///
    /// 只有同时满足以下条件才显示：
    ///   - 这张牌有重放次数（华彩 Glam / BaseReplayCount）
    ///   - 玩家身上没有"本回合不能抽牌"（NoDrawPower）
    ///   - 存在抽牌来源（黑暗之拥 或 迅速附魔）
    /// </summary>
    private static int GetFiendFireReplayBlock(CardModel card, Creature? creature, out Texture2D? icon)
    {
        icon = null;

        if (card is not FiendFire)
        {
            return 0;
        }

        var power = creature?.GetPower<FeelNoPainPower>();
        if (power == null || power.Amount <= 0 || creature == null)
        {
            return 0;
        }

        // 实际播放次数（含重放 / 华彩 / 复制药水 / 爆发 / 回声形态 / 遗物等一切来源）
        int replayCount = Math.Max(0, GetPlayCount(card, creature) - 1);

        if (replayCount <= 0)
        {
            return 0;
        }

        // 本回合不能抽牌：重放时没有新牌可烧
        if (creature.HasPower<NoDrawPower>())
        {
            return 0;
        }

        var player = creature.Player;
        if (player == null)
        {
            return 0;
        }

        int handCount = 0;
        foreach (var handCard in PileType.Hand.GetPile(player).Cards)
        {
            if (handCard != card)
            {
                handCount++;
            }
        }

        if (handCount <= 0)
        {
            return 0;
        }

        // 黑暗之拥（DarkEmbracePower.AfterCardExhausted）：每消耗一张牌抽 Amount 张。
        int darkEmbrace = Math.Max(0, creature.GetPowerAmount<DarkEmbracePower>());

        // 迅速（Swift）附魔：OnPlay 里抽 Amount 张。
        // 关键点：Swift.AfterCardPlayed 会把自己的 Status 置成 Disabled，
        // 所以它**只在第一次打出时生效**，重放轮不会再抽牌。
        int swiftDraw = card.Enchantment is Swift swift ? swift.Amount : 0;

        // 抽牌来源必须真有牌可抽。
        // 黑暗之拥和迅速都是从「抽牌堆 → 抽空后洗弃牌堆」里抽，
        // 两个堆都空时一张都抽不回来，重放轮就没有牌可烧（这里出过一次 bug：
        // 抽牌堆/弃牌堆都 0 张却照样显示 +7?）。
        // 判据与重振精神的重放（GetSecondWindReplayBlock）保持一致。
        int pileTotal = CountCardsInPiles(player);

        if (pileTotal <= 0)
        {
            return 0;
        }

        // 执行顺序（CardModel 打出流程）：
        //   1) 先跑卡牌自身的 OnPlay —— 恶魔之焰把当前手牌连同"自己"一起消耗，
        //      每消耗一张触发一次黑暗之拥抽牌。此时 Swift 还没抽牌，所以第一轮
        //      消耗的就是「手牌数 + 自己 1 张」，不含 swiftDraw。
        //   2) 再跑 Swift.OnPlay —— 抽 swiftDraw 张。这批牌留在手上，
        //      正好成为重放轮要烧的牌。
        //
        // 抽回来的张数既受手牌上限约束，也受牌堆现有总数约束。
        int firstExhausted = handCount + 1;
        int drawnBack = Math.Min(firstExhausted * darkEmbrace, MaxHandSize);
        drawnBack = Math.Min(drawnBack, pileTotal);

        int pileRemaining = Math.Max(0, pileTotal - drawnBack);
        int swiftActual = Math.Min(Math.Max(0, swiftDraw), pileRemaining);

        int handBeforeReplay = Math.Min(drawnBack + swiftActual, MaxHandSize);

        if (handBeforeReplay <= 0)
        {
            return 0;
        }

        // 逐轮推演：每重放一次都烧光手牌，烧掉的每张又触发一次黑暗之拥抽牌，
        // 为下一轮补回牌。不能简单用"张数 × 重放次数"。
        // 牌堆抽空后就是一个固定的小循环，所以每轮也要按剩余牌量收敛。
        int totalBurned = 0;
        int current = handBeforeReplay;
        int remainingPile = pileRemaining;

        for (int i = 0; i < replayCount; i++)
        {
            totalBurned += current;

            int refill = Math.Min(current * darkEmbrace, MaxHandSize);
            refill = Math.Min(refill, remainingPile);
            remainingPile = Math.Max(0, remainingPile - refill);

            current = refill;
        }

        if (totalBurned <= 0)
        {
            return 0;
        }

        // 图标跟第一轮一致，都用恶魔形态，让两段一眼看出是同一个来源
        icon = GetDemonFormIcon();
        return totalBurned * power.Amount;
    }

    private static Texture2D? _swiftIcon;
    private static bool _swiftIconResolved;

    /// <summary>重放段图标：迅速（Swift）附魔的图标。</summary>
    private static Texture2D? GetSwiftIcon()
    {
        if (_swiftIconResolved)
        {
            return _swiftIcon;
        }

        _swiftIconResolved = true;

        try
        {
            _swiftIcon = ModelDb.Enchantment<Swift>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取迅速附魔图标失败: {e.Message}");
            _swiftIcon = null;
        }

        return _swiftIcon;
    }

    private static Texture2D? _demonFormIcon;
    private static bool _demonFormIconResolved;

    /// <summary>恶魔之焰那一段的图标：恶魔形态（Demon Form）的状态图标。</summary>
    private static Texture2D? GetDemonFormIcon()
    {
        if (_demonFormIconResolved)
        {
            return _demonFormIcon;
        }

        _demonFormIconResolved = true;

        try
        {
            _demonFormIcon = ModelDb.Power<DemonFormPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取恶魔形态图标失败: {e.Message}");
            _demonFormIcon = null;
        }

        return _demonFormIcon;
    }

    // ==================================================================
    //  充能球队列推演（2026-11-19 重构：统一「生成」与「激发」）
    //
    //  游戏里每颗球都是**逐颗** Channel 的（OrbCmd.Channel）：
    //      if (Orbs.Count >= Capacity) await EvokeNext(...);   // 队首被挤出去
    //      OrbQueue.TryEnqueue(orb);                           // 加到末尾
    //  所以这里用「队列模拟」而不是数学公式 —— 与游戏同构，
    //  混合球（Rainbow）/ 扩容（Capacitor）/ 新球自我挤压 全部天然正确。
    //
    //  推演只读真实队列做快照，绝不修改游戏数据。
    // ==================================================================

    /// <summary>`OrbQueue.AddSlots` 的硬上限（游戏里写死 10）。</summary>
    private const int MaxOrbCapacity = 10;

    private enum SimOrbKind
    {
        Frost,
        Lightning,
        Dark,
        Plasma,
        Glass,
        Other,

        /// <summary>
        /// 类型未知的随机球（TheMagician：5 种球等概率随机）。
        /// 它**占槽位**这件事是确定的，所以照样入队、照样能把队首冰球挤出去；
        /// 但它自己"是不是冰球"不可预知，因此不贡献任何格挡（宁可不显示，也不要多算）。
        /// </summary>
        Random,
    }

    /// <summary>推演用的轻量球快照，不持有真实 OrbModel。</summary>
    private readonly record struct SimOrb(SimOrbKind Kind, int EvokeVal, int PassiveVal, Texture2D? Icon)
    {
        public bool IsFrost => Kind == SimOrbKind.Frost;
    }

    /// <summary>这张牌会生成的一批球：类型 + 个数（按执行顺序）。</summary>
    private readonly record struct OrbChannelSpec(SimOrbKind Kind, int Count);

    /// <summary>
    /// Voltaic 的 CalculatedChannels：本场战斗里玩家已经生成过的**闪电球**总数。
    ///
    /// 游戏实现（sts2.decompiled.cs 163474）：
    ///   CalculatedChannels.WithMultiplier((card, _) =>
    ///       History.Entries.OfType&lt;OrbChanneledEntry&gt;()
    ///           .Count(e => e.Actor.Player == card.Owner &amp;&amp; e.Orb is LightningOrb))
    /// 战斗历史是实时可读的，所以 Voltaic 的生成量能**算准**，不是估算。
    /// </summary>
    private static int CountLightningChannels(CardModel card)
    {
        try
        {
            var owner = card.Owner;

            if (owner == null)
            {
                return 0;
            }

            var lightningState = card.CombatState;
            var lightningSnap = lightningState != null
                ? GetHistorySnapshot(lightningState, owner)
                : null;

            if (lightningSnap != null)
            {
                return lightningSnap.LightningChannelsEver;
            }

            return CombatManager.Instance.History.Entries
                .OfType<OrbChanneledEntry>()
                .Count(e => e.Actor.Player == owner && e.Orb is LightningOrb);
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计闪电球生成历史失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 这张牌按执行顺序会生成哪些球。规格来自逐牌核对游戏实现（27 张）。
    /// 返回空列表 = 不生成球，或确认对格挡无影响（Ignition）。
    /// </summary>
    private static List<OrbChannelSpec> GetOrbChannelSpecs(CardModel? card)
    {
        var list = new List<OrbChannelSpec>();

        switch (card)
        {
            // ---- 固定 1 颗 ----
            case BallLightning: list.Add(new(SimOrbKind.Lightning, 1)); break;
            case ColdSnap: list.Add(new(SimOrbKind.Frost, 1)); break;
            case Coolheaded: list.Add(new(SimOrbKind.Frost, 1)); break;
            case Darkness: list.Add(new(SimOrbKind.Dark, 1)); break;
            case Fusion: list.Add(new(SimOrbKind.Plasma, 1)); break;
            case Glasswork: list.Add(new(SimOrbKind.Glass, 1)); break;
            case Null: list.Add(new(SimOrbKind.Dark, 1)); break;
            case ShadowShield: list.Add(new(SimOrbKind.Dark, 1)); break;
            case Zap: list.Add(new(SimOrbKind.Lightning, 1)); break;

            // Rainbow：闪电 → 冰霜 → 黑暗，顺序固定不能颠倒
            case Rainbow:
                list.Add(new(SimOrbKind.Lightning, 1));
                list.Add(new(SimOrbKind.Frost, 1));
                list.Add(new(SimOrbKind.Dark, 1));
                break;

            // ---- 固定多颗 ----
            case Glacier: list.Add(new(SimOrbKind.Frost, 2)); break;
            case IceLance: list.Add(new(SimOrbKind.Frost, 3)); break;
            case MeteorStrike: list.Add(new(SimOrbKind.Plasma, 3)); break;
            case Refract: list.Add(new(SimOrbKind.Glass, 2)); break;

            // ---- 读卡面 Repeat 变量（升级会变） ----
            case ConsumingShadow: list.Add(new(SimOrbKind.Dark, ReadDynamicVar(card, "Repeat"))); break;
            case Hibernate: list.Add(new(SimOrbKind.Frost, ReadDynamicVar(card, "Repeat"))); break;

            // ---- 依赖局面 ----
            case Chill: list.Add(new(SimOrbKind.Frost, CountHittableEnemies(card))); break;
            case Tempest: list.Add(new(SimOrbKind.Lightning, EstimateXValue(card))); break;

            // Spinner：只有升级版才生成 1 颗玻璃球
            case Spinner:
                if (card.IsUpgraded)
                {
                    list.Add(new(SimOrbKind.Glass, 1));
                }

                break;

            // ---- 读战斗历史（可精确算，不是估算）----
            // Voltaic 的 CalculatedChannels = 本场战斗里玩家生成过的闪电球总数，
            // 而 OrbChanneledEntry 是实时可读的，所以数量能算准。
            case Voltaic:
                list.Add(new(SimOrbKind.Lightning, CountLightningChannels(card)));
                break;

            // ---- 随机类型：TheMagician ----
            // 游戏实现：OrbCmd.AddSlots(Repeat) 后再 Channel Repeat 次
            // OrbModel.GetRandomOrb(Rng.CombatOrbGeneration)，候选正好 5 种
            // （闪电 / 冰霜 / 黑暗 / 等离子 / 玻璃），等概率。
            // 类型算不出来，但"占几个槽"是确定的 → 按 Random 入队，
            // 这样它挤压队首冰球（溢出激发）的收益能算对。
#if STS2_FOOL
            case TheMagician:
                list.Add(new(SimOrbKind.Random, ReadDynamicVar(card, "Repeat")));
                break;
#endif

            // 不生成球 / 确认无格挡影响：
            //   Ignition → TargetType.AnyAlly，给**队友**生成 1 颗等离子球，
            //              既不进自己的队列，等离子球本身也没有格挡，
            //              所以对格挡预览没有任何影响，无需建模。
        }

        return list;
    }

    /// <summary>这张牌会给玩家增加几个充能球栏位。</summary>
    private static int GetOrbSlotAdd(CardModel? card)
    {
        return card switch
        {
            Capacitor => ReadDynamicVar(card, "Repeat"),
            Modded => 1,
#if STS2_FOOL
            TheMagician => ReadDynamicVar(card, "Repeat"),
#endif
            _ => 0,
        };
    }

    /// <summary>
    /// 打这张牌 totalPlays 次之后的球队列推演。
    ///
    ///   overflowBlock   = 生成新球时被挤出队首的冰球带来的格挡（**溢出激发**，独立成段）
    ///   evokeBlock      = 玩家主动激发的格挡（Front / All）
    ///   frostBlock      = 推演结束后仍存活的冰球，在回合结束时提供的被动格挡
    ///   goldCablesBlock = 镀金缆线（GoldPlatedCables）让队首球被动多触发一次带来的额外格挡
    /// </summary>
    /// <summary>球推演诊断日志的上一次内容（内容变了才打印，避免每帧刷屏）。</summary>
    private static string? _lastOrbSimDebug;

    private static void SimulateOrbs(
        Creature? creature,
        CardModel? card,
        int totalPlays,
        out int evokeBlock,
        out int overflowBlock,
        out int frostBlock,
        out int goldCablesBlock,
        out Texture2D? evokeIcon,
        out Texture2D? overflowIcon,
        out Texture2D? frostIcon,
        out Texture2D? goldCablesIcon)
    {
        evokeBlock = 0;
        overflowBlock = 0;
        frostBlock = 0;
        goldCablesBlock = 0;
        evokeIcon = null;
        overflowIcon = null;
        frostIcon = null;
        goldCablesIcon = null;

        if (totalPlays <= 0)
        {
            return;
        }

        var queue = creature?.Player?.PlayerCombatState?.OrbQueue;

        if (queue == null)
        {
            return;
        }

        var specs = GetOrbChannelSpecs(card);
        int slotAdd = GetOrbSlotAdd(card);

        // 雷暴（StormPower）：每打出一张**能力牌**，按层数各生成 1 个闪电球
        // （`StormPower.AfterCardPlayed` → `OrbCmd.Channel<LightningOrb>` × Amount）。
        // 拖能力牌时必须算进来，否则"满槽把队首冰球挤出去"的溢出激发会漏算（2026-11-22）。
        if (card != null && card.Type == CardType.Power && creature != null)
        {
            var storm = creature.GetPower<StormPower>();

            if (storm != null && storm.Amount > 0)
            {
                specs.Add(new OrbChannelSpec(SimOrbKind.Lightning, Math.Max(0, storm.Amount)));
            }
        }

        // 队列空 + 不生成球 + 不扩容 → 无事可做
        if (queue.Orbs.Count == 0 && specs.Count == 0 && slotAdd == 0)
        {
            return;
        }

        // 只读快照，不碰真实队列
        var orbs = new List<SimOrb>(queue.Orbs.Count);

        foreach (var orb in queue.Orbs)
        {
            orbs.Add(MakeSimSnapshot(orb));
        }

        int capacity = queue.Capacity;

        // 角色没有球槽时，OrbCmd.Channel 会先隐式 AddSlots(1)。
        // 但**卡牌自己的扩容先执行**（TheMagician / Capacitor 都是先 AddSlots 再生成球），
        // 到 Channel 那一刻已经有槽位了，就不会再补。所以只有不扩容的牌才补 1。
        if (capacity <= 0 && specs.Count > 0 && slotAdd <= 0)
        {
            capacity = 1;
        }

        // Voltaic 的生成量读的是「历史闪电球数」，而它生成出去的每颗球也会立刻写进历史，
        // 所以重放时第二轮必然比第一轮多（base → 2×base → 3×base…）。
        // 预览不能写游戏数据，这里在推演内部自己累计。
        bool isVoltaic = card is Voltaic;
        int voltaicChannelled = 0;

        var evokeType = GetEffectiveEvokeType(card);
        int evokeTimes = card != null ? GetEvokeTimes(card, creature) : 1;

        for (int play = 0; play < totalPlays; play++)
        {
            // 扩容：每次播放都会执行一次 AddSlots，且有 10 格上限
            if (slotAdd > 0)
            {
                capacity = Math.Min(MaxOrbCapacity, capacity + slotAdd);
            }

            // ---- A. 依次生成（Channel 是逐颗执行的）----
            foreach (var spec in specs)
            {
                int spawnCount = spec.Count;

                if (isVoltaic && spec.Kind == SimOrbKind.Lightning)
                {
                    spawnCount += voltaicChannelled;
                }

                for (int i = 0; i < spawnCount; i++)
                {
                    // 满了先把队首挤出去 —— 这就是「溢出激发」
                    if (capacity > 0 && orbs.Count >= capacity)
                    {
                        var pushed = orbs[0];
                        orbs.RemoveAt(0);

                        if (pushed.IsFrost && pushed.EvokeVal > 0)
                        {
                            overflowBlock += pushed.EvokeVal;

                            // 图标固定用引雷针（和主动激发同款）。
                            // 原来用被挤掉那颗球的 Icon，结果和「冰球被动」段一模一样，
                            // 玩家分不清哪一段是溢出激发、哪一段是剩余冰球的被动。
                            // 被挤掉的球已经 RemoveAt 了，它的被动不会进下面的 frostBlock。
                            overflowIcon ??= GetEvokeIcon();
                        }
                    }

                    orbs.Add(MakeNewSimOrb(spec.Kind, creature));
                }

                if (isVoltaic && spec.Kind == SimOrbKind.Lightning)
                {
                    voltaicChannelled += spawnCount;
                }
            }

            // ---- B. 玩家主动激发 ----
            if (evokeType == OrbEvokeType.None || orbs.Count == 0)
            {
                continue;
            }

            if (evokeType == OrbEvokeType.Front)
            {
                if (orbs[0].IsFrost && orbs[0].EvokeVal > 0)
                {
                    evokeBlock += orbs[0].EvokeVal * evokeTimes;
                    evokeIcon ??= GetEvokeIcon();
                }

                orbs.RemoveAt(0);
                continue;
            }

            // OrbEvokeType.All：每颗球都激发并消耗
            foreach (var orb in orbs)
            {
                if (orb.IsFrost && orb.EvokeVal > 0)
                {
                    evokeBlock += orb.EvokeVal * evokeTimes;
                    evokeIcon ??= GetEvokeIcon();
                }
            }

            orbs.Clear();
        }

        // ---- C. 吞噬暗影（ConsumingShadowPower）：回合结束时激发最后 N 颗球 ----
        //
        // 游戏里它在 `AfterSideTurnEnd` 里对 `Orbs.Last()` 反复 `EvokeLast`，Amount 层 = N 次。
        // 注意方向：这里的 `Orbs.Last()` 是**最后一个**球（最后生成的），
        // 和被挤出/主动激发时用的 `Orbs.First()` 正好相反。
        //
        // 影响：被激发的冰球**不再提供被动**（它已经被消耗了），但激发本身给格挡。
        // 这段格挡发生在 `AfterSideTurnEnd`，之后才轮到敌方回合，
        // 所以它确实能挡伤害，计入"结算后的总格挡"。
        // 黑暗球激发是打伤害、不产格挡，由 `IsFrost` 自然排除。
        var consumingShadow = creature?.GetPower<ConsumingShadowPower>();

        if (consumingShadow != null && consumingShadow.Amount > 0)
        {
            for (int i = 0; i < consumingShadow.Amount && orbs.Count > 0; i++)
            {
                var last = orbs[orbs.Count - 1];
                orbs.RemoveAt(orbs.Count - 1);

                // ⚠️ 被动必须先算：冰球的被动在 `BeforeTurnEndOrbTrigger` 触发，
                // **早于**吞噬暗影的 `AfterSideTurnEnd` 激发。
                // 这颗球虽然随后被激发消耗，它的被动仍然生效过 —— 漏掉会少算一层（2026-11-23 修正）。
                if (last.IsFrost && last.PassiveVal > 0)
                {
                    frostBlock += last.PassiveVal;
                    frostIcon ??= last.Icon;
                }

                if (last.IsFrost && last.EvokeVal > 0)
                {
                    evokeBlock += last.EvokeVal;

                    // 吞噬暗影的激发用它**自己的状态图标**；引雷针留给"主动激发"。
                    // 两者同时存在时保持引雷针（`??=` 不覆盖），因为主动激发更直观。
                    evokeIcon ??= GetConsumingShadowIcon() ?? GetEvokeIcon();
                }
            }
        }

        // ---- D. 剩余冰球在回合结束时的被动 ----
        foreach (var orb in orbs)
        {
            if (!orb.IsFrost || orb.PassiveVal <= 0)
            {
                continue;
            }

            frostBlock += orb.PassiveVal;
            frostIcon ??= orb.Icon;
        }

        // ---- E. 镀金缆线：队首（最右侧出口 Orbs[0]）的球被动多触发一次 ----
        //
        // 游戏里（GoldPlatedCables.ModifyOrbPassiveTriggerCounts）：
        //   if (orb != Owner.PlayerCombatState.OrbQueue.Orbs[0]) return triggerCount;
        //   return triggerCount + 1;
        // 即「只有队首那颗球」的被动触发次数 +1，它自己不给格挡。
        // 对冰球来说 = 额外多算一份 PassiveVal；队首不是冰球时对格挡零影响。
        //
        // ⚠️ 必须用**推演后**的队列：这张牌可能已经激发/挤出了原来的队首，
        //    结算被动时队首是谁取决于打完这张牌之后的状态。
        if (orbs.Count > 0 && orbs[0].IsFrost && orbs[0].PassiveVal > 0)
        {
            var cables = GetRelic<GoldPlatedCables>(creature?.Player);

            if (cables != null)
            {
                goldCablesBlock = orbs[0].PassiveVal;
                goldCablesIcon = cables.Icon;
            }
        }

        // 诊断：拖"会生成球"的牌时记录一次最终判定数据（内容变了才打，避免刷屏）。
        // 用来核对「容量 / 球数 / 队首是不是冰球 / 溢出值」到底对不对。
        // 日志：%APPDATA%\SlayTheSpire2\BlockPreviewLogs\Block Preview mod_Orb_*.log
        if (specs.Count > 0)
        {
            string dbg = $"card={card?.GetType().Name ?? "null"} plays={totalPlays} spec={specs.Count} "
                         + $"cap={capacity} orbs={orbs.Count} frost={orbs.Count(o => o.IsFrost)} "
                         + $"overflow={overflowBlock} evoke={evokeBlock} frostPassive={frostBlock} "
                         + $"consuming={consumingShadow?.Amount ?? 0} "
                         + $"showOverflow={BlockPreviewConfig.ShowOverflow} showFrost={BlockPreviewConfig.ShowFrost}";

            if (dbg != _lastOrbSimDebug)
            {
                _lastOrbSimDebug = dbg;
                BlockPreviewLog.Orb(dbg);
            }
        }
    }

    /// <summary>把真实球转成推演快照（只读）。</summary>
    private static SimOrb MakeSimSnapshot(OrbModel orb)
    {
        if (orb is FrostOrb frost)
        {
            // 冰球图标：正式版（v0.111.x）里 `orbs/frost.png` 有时取不到 → 面板会显示成问号。
            // 取不到就退回格挡图标，保证至少不是"丢失图标"（2026-11-23）。
            return new SimOrb(
                SimOrbKind.Frost,
                Math.Max(0, (int)Math.Floor(frost.EvokeVal)),
                Math.Max(0, (int)Math.Floor(frost.PassiveVal)),
                frost.Icon ?? TryLoadFrostOrbIconDirect() ?? FindBlockIcon());
        }

        var kind = orb switch
        {
            LightningOrb => SimOrbKind.Lightning,
            DarkOrb => SimOrbKind.Dark,
            PlasmaOrb => SimOrbKind.Plasma,
            GlassOrb => SimOrbKind.Glass,
            _ => SimOrbKind.Other,
        };

        return new SimOrb(kind, 0, 0, orb.Icon);
    }

    /// <summary>
    /// 模拟"新生成一颗球"。新球没有真实实例，数值按模型模板 + 当前 Focus 估算。
    /// 只有冰球需要数值（激发和被动都吃 Focus，且游戏里会被 clamp 到 ≥ 0）。
    /// </summary>
    private static SimOrb MakeNewSimOrb(SimOrbKind kind, Creature? creature)
    {
        if (kind != SimOrbKind.Frost)
        {
            return new SimOrb(kind, 0, 0, null);
        }

        int focus = creature?.GetPowerAmount<FocusPower>() ?? 0;

        // ⚠️ 基础值**必须写死**，不能读 `ModelDb.Orb<FrostOrb>()` 的 EvokeVal / PassiveVal：
        // 那是 Canonical 模板，而 `OrbModel.EvokeVal` / `PassiveVal` 内部走
        // `ModifyOrbValue(...)` → `Hook.ModifyOrbValue(Owner.Creature.CombatState, ...)`，
        // 对 Canonical 模型会抛 `CanonicalModelException: used in incorrect place`。
        // 一旦抛出，整个 SimulateOrbs / BuildValues 中断、面板停在上一次的旧值上，
        // 表现就是"拖生成冰球的牌没有反应"。
        // 数值直接来自 FrostOrb 源码：PassiveVal => ModifyOrbValue(2m)、EvokeVal => ModifyOrbValue(5m)。
        int evoke = Math.Max(0, 5 + focus);
        int passive = Math.Max(0, 2 + focus);

        return new SimOrb(SimOrbKind.Frost, evoke, passive, GetFrostOrbIcon(creature));
    }

    private static Texture2D? _frostOrbIconCache;
    private static bool _frostOrbIconLoaded;

    /// <summary>
    /// 兜底：按资源路径直接加载冰球图标（场上还没生成过冰球时用）。
    ///
    /// ⚠️ **必须先 `FileAccess.FileExists` 确认存在再加载**：
    /// 直接 `GetCompressedTexture2D` 一个不存在的路径会抛异常，
    /// 2026-11-23 就是这样把 `SimulateOrbs` 打断、面板变成 `=0` 的。
    /// 不同版本的图片命名可能不同，所以按候选名逐个试。
    /// </summary>
    private static Texture2D? TryLoadFrostOrbIconDirect()
    {
        if (_frostOrbIconLoaded)
        {
            return _frostOrbIconCache;
        }

        _frostOrbIconLoaded = true;

        // ① 先从**已注册的资源**里找（不依赖硬编码路径，最稳）：
        //    `AssetCache.GetCacheKeys()` 能列出游戏已加载的所有资源路径。
        try
        {
            foreach (string key in PreloadManager.Cache.GetCacheKeys())
            {
                string lower = key.ToLowerInvariant();

                if (lower.Contains("frost") && lower.Contains("orb")
                    && lower.EndsWith(".png", StringComparison.Ordinal))
                {
                    var icon = PreloadManager.Cache.GetCompressedTexture2D(key);

                    if (icon != null)
                    {
                        _frostOrbIconCache = icon;
                        BlockPreviewLog.Perf($"[诊断] 冰球图标加载成功: {key}");
                        return icon;
                    }
                }
            }
        }
        catch
        {
        }

        // ② 回退：按候选路径逐个试
        string[] candidates =
        {
            "orbs/frost.png",
            "orbs/frost_orb.png",
            "orbs/orb_frost.png",
        };

        foreach (string candidate in candidates)
        {
            try
            {
                string path = ImageHelper.GetImagePath(candidate);

                if (!Godot.FileAccess.FileExists(path))
                {
                    continue;
                }

                _frostOrbIconCache = PreloadManager.Cache.GetCompressedTexture2D(path);

                if (_frostOrbIconCache != null)
                {
                    BlockPreviewLog.Perf($"[诊断] 冰球图标加载成功: {path}");
                    return _frostOrbIconCache;
                }
            }
            catch
            {
                // 单个候选失败不影响其它候选
            }
        }

        BlockPreviewLog.Perf("[诊断] 冰球图标未找到，退回格挡图标");
        return _frostOrbIconCache;
    }

    /// <summary>
    /// 取冰球图标：**只用场上真实冰球的 Icon**，并缓存下来。
    ///
    /// ⚠️ 不能读 `ModelDb.Orb&lt;FrostOrb&gt;().Icon`（Canonical 模板会触发模型校验异常），
    /// 也不要按 `res://images/orbs/xxx.png` 直接加载 —— 2026-11-23 实测：路径加载会抛异常，
    /// 把整个 `SimulateOrbs` 打断，面板直接变成 `=0`。
    ///
    /// 所以策略是：场上出现过冰球 → 缓存它的图标；没出现过 → 退回格挡图标。
    /// </summary>
    private static Texture2D? GetFrostOrbIcon(Creature? creature)
    {
        try
        {
            var orbs = creature?.Player?.PlayerCombatState?.OrbQueue?.Orbs;

            if (orbs != null)
            {
                foreach (var orb in orbs)
                {
                    if (orb is FrostOrb frost)
                    {
                        _frostOrbIconCache = frost.Icon;

                        if (_frostOrbIconCache != null)
                        {
                            return _frostOrbIconCache;
                        }

                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            BlockPreviewLog.Perf($"[诊断] 取冰球图标失败: {e.Message}");
        }

        return TryLoadFrostOrbIconDirect() ?? _frostOrbIconCache ?? FindBlockIcon();
    }

    /// <summary>
    /// 卡牌实际会作用到哪些球。
    ///
    /// MultiCast 是个陷阱：游戏把它标成 <see cref="OrbEvokeType.All"/>（好让 UI 给所有球
    /// 显示激发值），但它循环里始终调用 EvokeNext 打队首那颗——只有最后一次才 dequeue，
    /// 所以实际是"同一颗球反复激发 X 次"，而不是每颗球各激发 X 次。
    /// </summary>
    private static OrbEvokeType GetEffectiveEvokeType(CardModel? card)
    {
        if (card == null)
        {
            return OrbEvokeType.None;
        }

        var type = card.OrbEvokeType;

        if (type == OrbEvokeType.All && card is MultiCast)
        {
            return OrbEvokeType.Front;
        }

        return type;
    }

    /// <summary>
    /// 这张牌会把球激发几次。
    /// 游戏只给了 OrbEvokeType（激发哪个球），没给次数，只能按牌区分：
    ///   Dualcast  激发两次（无 dequeue 一次 + 有 dequeue 一次）
    ///   Quadcast  次数 = DynamicVars.Repeat（基础 4）
    ///   MultiCast X 费，次数 = 当前能量（升级 +1）
    ///   Shatter   激发全部，每颗两次
    /// 其他（含 mod 加的新激发牌）默认 1 次。
    /// </summary>
    private static int GetEvokeTimes(CardModel card, Creature? creature)
    {
        switch (card)
        {
            case Dualcast:
                return 2;

            case Shatter:
                return 2;

            case Quadcast:
                return Math.Max(1, card.DynamicVars.Repeat.IntValue);

            case MultiCast:
            {
                int energy = creature?.Player?.PlayerCombatState?.Energy ?? 0;
                if (card.IsUpgraded)
                {
                    energy++;
                }

                return Math.Max(1, energy);
            }

            default:
                return 1;
        }
    }

    private static Texture2D? _evokeIcon;
    private static bool _evokeIconResolved;
    private static Texture2D? _songPathIcon;
    private static bool _songPathIconResolved;
    private static Texture2D? _lullabyIcon;
    private static bool _lullabyIconResolved;
    private static Texture2D? _allIsWellIcon;
    private static bool _allIsWellIconResolved;

    private static Texture2D? _strengthIcon;
    private static bool _strengthIconResolved;

    private static Texture2D? _poisonIcon;
    private static bool _poisonIconResolved;

    /// <summary>
    /// 伤害转化格挡那一段的图标：力量（Strength）的状态图标。
    /// </summary>
    private static Texture2D? GetStrengthIcon()
    {
        if (_strengthIconResolved)
        {
            return _strengthIcon;
        }

        _strengthIconResolved = true;

        try
        {
            _strengthIcon = ModelDb.Power<StrengthPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取力量图标失败: {e.Message}");
            _strengthIcon = null;
        }

        return _strengthIcon;
    }

    /// <summary>
    /// 蜃景那一段的图标：中毒（Poison）的状态图标。
    /// </summary>
    private static Texture2D? GetPoisonIcon()
    {
        if (_poisonIconResolved)
        {
            return _poisonIcon;
        }

        _poisonIconResolved = true;

        try
        {
            _poisonIcon = ModelDb.Power<PoisonPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取中毒图标失败: {e.Message}");
            _poisonIcon = null;
        }

        return _poisonIcon;
    }

    private static Texture2D? _burstIcon;
    private static bool _burstIconResolved;

    /// <summary>
    /// 爆发那一段的图标：爆发（Burst）的状态图标。
    /// </summary>
    private static Texture2D? GetBurstIcon()
    {
        if (_burstIconResolved)
        {
            return _burstIcon;
        }

        _burstIconResolved = true;

        try
        {
            _burstIcon = ModelDb.Power<BurstPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取爆发图标失败: {e.Message}");
            _burstIcon = null;
        }

        return _burstIcon;
    }

    /// <summary>
    /// 激发段的图标：借用引雷针（Lightning Rod）这张卡的状态图标。
    /// </summary>
    private static Texture2D? GetEvokeIcon()
    {
        if (_evokeIconResolved)
        {
            return _evokeIcon;
        }

        _evokeIconResolved = true;

        try
        {
            _evokeIcon = ModelDb.Power<LightningRodPower>().Icon;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 取引雷针图标失败: {e.Message}");
            _evokeIcon = null;
        }

        return _evokeIcon;
    }

    /// <summary>吞噬暗影（`ConsumingShadowPower`）的状态图标：它那段激发用这个。</summary>
    private static Texture2D? GetConsumingShadowIcon()
    {
        try
        {
            return ModelDb.Power<ConsumingShadowPower>().Icon;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取这张牌当前面板上显示的格挡值。
    /// 游戏在 NCard.UpdateVisuals 里已经用 Hook.ModifyBlock 算好了 PreviewValue
    /// （含敏捷等修正），这里直接读，保证与卡面数字一致。
    /// </summary>
    private static int ComputeHandBlock(CardModel card, out int damageBlock)
    {
        decimal gain = 0m;
        damageBlock = 0;
        bool hasBlockVar = false;
        var vars = card.DynamicVars;

        // 蜃景（Mirage）、逃脱计划（EscapePlan）的技能格挡单独成段显示
        // （+X [毒] 和 +X [概率]），这里直接排除，避免和手牌格挡重复计算。
        if (card is Mirage or EscapePlan)
        {
            return 0;
        }

        // 「托举」（Lift）/「恶魔之盾」（DemonicShield）这类**只给队友**格挡的牌：
        // `TargetType.AnyAlly` 的定义就是「任意队友（排除自己）」，
        // 它们 GainBlock 的是 `cardPlay.Target`，自己一点都拿不到，所以预览必须是 0。
        //
        // ⚠️ 不能按 `TargetType.AnyAlly` 一刀切：「拦截」（Intercept）也是 AnyAlly，
        // 但它 `GainBlock(base.Owner.Creature, ...)` 是**给自己**的，归零就错了。
        // 所以这里用显式名单，将来发现新的"给队友"牌再补。
        if (card is Lift or DemonicShield)
        {
            return 0;
        }

        // 重振精神（SecondWind）：消耗手牌里所有非攻击牌，每张获得 BlockVar 点格挡。
        // 卡面上的 BlockVar 是"每张"的值，需要乘上实际被烧掉的张数。
        // 它自己不生成新牌，所以第二轮能烧多少完全取决于黑暗之拥抽回多少。
        if (card is SecondWind)
        {
            int count = 0;
            var player = card.Owner;

            if (player != null)
            {
                foreach (var handCard in PileType.Hand.GetPile(player).Cards)
                {
                    if (handCard != card && handCard.Type != CardType.Attack)
                    {
                        count++;
                    }
                }
            }

            if (count > 0 && vars.TryGetValue(BlockVar.defaultName, out var swBlock) && swBlock is BlockVar swBlockVar)
            {
                // 这里只算第一轮；重放轮的期望值单独由 GetSecondWindReplayBlock 显示
                return (int)Math.Floor(swBlockVar.PreviewValue) * count;
            }

            return 0;
        }

        if (vars != null)
        {
            if (vars.TryGetValue(BlockVar.defaultName, out var bv) && bv is BlockVar block)
            {
                gain += block.PreviewValue;
                hasBlockVar = true;
            }

            if (vars.TryGetValue(CalculatedBlockVar.defaultName, out var cbv) && cbv is CalculatedBlockVar calcBlock)
            {
                gain += calcBlock.PreviewValue;
                hasBlockVar = true;
            }

            // 拳斗（Fisticuffs）这类牌：GainsBlock 为 true，但没有 Block 变量——
            // 它的格挡等于这张牌的面板伤害值。单独成段显示，图标用力量状态。
            //
            // 注意：即使伤害被敌人格挡完全吃掉，拳斗依然按**原始伤害**给格挡。
            // 游戏里取的是 TotalDamage + OverkillDamage，两者合计等于原始伤害，
            // 所以这里不能减敌人格挡。
            if (!hasBlockVar
                && card.GainsBlock
                && vars.TryGetValue(DamageVar.defaultName, out var dv)
                && dv is DamageVar damage)
            {
                // 拳斗的格挡是自己算的（伤害值），不经过 BlockVar.PreviewValue，
                // 所以要手动补「佩尔的士兵」的 ×2（清醒时卡牌来源的格挡翻倍），
                // 以及「不动」的 ×2（本回合前 N 次卡牌来源格挡翻倍）。两者会相乘。
                damageBlock = (int)Math.Floor(damage.PreviewValue)
                              * GetPaelsLegionMultiplier(card.Owner)
                              * GetUnmovableMultiplier(card.Owner?.Creature);
            }
        }

        // 愚者：有几张牌的格挡是**多次 GainBlock**，卡面 `BlockVar` 只显示单次值。
        //   ChoFirm  —— 手上有可用箭时 `blockGains = 1 + 1 = 2` 次（6 → 12，升 8 → 16）
        //   ChoLimbs —— 固定 GainBlock ×2（5 → 10，升 7 → 14）；
        //               旋律只影响它的**费用**，不影响格挡，所以无条件 ×2
#if STS2_FOOL
        if (card is ChoFirm)
        {
            if (HasAvailableArrow(card.Owner))
            {
                gain *= 2;
            }
        }
        else if (card is ChoLimbs)
        {
            gain *= 2;
        }

        // 啾 副歌（ChoChorus）：获得 BlockVar 点格挡后，把**手中所有「哇」「叽」牌逐张自动打出**，
        // 那些牌自己的格挡也会加进来（`WahCho` 3 / `WahChoKi` 9 / `WahTouch` 等）。
        // 只算「给自己」的格挡；自动打出连带的余像 / 无惧疼痛等暂不计（见 8.1）。
        if (card is ChoChorus)
        {
            gain += SumAutoPlayedMelodyBlock(card);
        }
#endif

        return (int)Math.Floor(gain);
    }

    /// <summary>玩家当前是否还有可用的箭（`ArrowSlot.IsAvailable`）。</summary>
    private static bool HasAvailableArrow(Player? player)
    {
#if STS2_FOOL
        if (player == null)
        {
            return false;
        }

        try
        {
            // 用 `AvailableArrowCount`：它直接遍历内部 `_arrows`，不分配 List；
            // 而 `ArrowCmd.PlayerHasAnyArrows` 走的是 `PlayerCombatState.Arrows`，
            // 那个属性每次都会 `ToList()` 复制一份（拖牌时每帧调用不值得为此分配）。
            return player.PlayerCombatState?.AvailableArrowCount > 0;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 读取箭矢失败: {e.Message}");
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>
    /// 啾 副歌（`ChoChorus`）自动打出手中「哇」「叽」牌时，那些牌**自己带来的格挡**。
    ///
    /// 游戏里 `ChoChorus.OnPlay` 会 `AutoPlay` 手里所有带 `CardTag.Wah` / `CardTag.Ki` 的牌，
    /// 所以 `WahCho`（3/5）、`WahChoKi`（9/12）、`WahTouch` 等的格挡都会加进来。
    /// 只取它们卡面的格挡值（已含敏捷 / 融入暗影修正）；
    /// 给队友的牌（托举 / 恶魔之盾）跳过；自动打出连带的余像 / 无惧疼痛等连锁暂不计。
    /// </summary>
    private static int SumAutoPlayedMelodyBlock(CardModel chorus)
    {
#if STS2_FOOL
        var player = chorus.Owner;

        if (player == null)
        {
            return 0;
        }

        try
        {
            int total = 0;

            foreach (var handCard in PileType.Hand.GetPile(player).Cards)
            {
                if (handCard == chorus)
                {
                    continue;
                }

                if (!handCard.Tags.Contains(CardTag.Wah) && !handCard.Tags.Contains(CardTag.Ki))
                {
                    continue;
                }

                // 只给队友的牌自己拿不到格挡（同 ComputeHandBlock 的名单）
                if (handCard is Lift or DemonicShield)
                {
                    continue;
                }

                var vars = handCard.DynamicVars;

                if (vars == null)
                {
                    continue;
                }

                if (vars.TryGetValue(BlockVar.defaultName, out var bv) && bv is BlockVar block)
                {
                    total += Math.Max(0, (int)Math.Floor(block.PreviewValue));
                }
                else if (vars.TryGetValue(CalculatedBlockVar.defaultName, out var cbv)
                         && cbv is CalculatedBlockVar calc)
                {
                    total += Math.Max(0, (int)Math.Floor(calc.PreviewValue));
                }
            }

            return total;
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 统计副歌连锁失败: {e.Message}");
            return 0;
        }
#else
        return 0;
#endif
    }

    /// <summary>
    /// `NHoverTipSet` 的私有静态字典（owner → 正在显示的提示）。
    ///
    /// 游戏的悬停提示都走这一套：遗物详情、卡牌、能量、牌堆、结束回合按钮……
    /// ⚠️ 不能「有提示就隐藏面板」——那样悬停任何东西面板都会消失（2026-11-21 踩过）。
    /// 这里只认 **`NRelic`**（遗物详情），局面其它小提示不抢位置。
    /// </summary>
    private static readonly FieldInfo? _activeHoverTipsField =
        typeof(NHoverTipSet).GetField("_activeHoverTips", BindingFlags.NonPublic | BindingFlags.Static);

    private static string _lastTipOwnerDesc = string.Empty;
    private static string _lastOrbOwnerDesc = string.Empty;
    private static string _lastLayoutMode = string.Empty;

    private static string _lastTipHitDesc = string.Empty;
    private static string _lastOrbDbg = string.Empty;
    private static string _lastPanelDbg = string.Empty;

    /// <summary>诊断：让位判据命中时打一行（只在变化时），确认面板到底有没有被判定为"该让位"。</summary>
    private static void LogTipHit(Node owner)
    {
        try
        {
            string desc = $"{owner.GetType().Name} ({owner.Name})";

            if (desc != _lastTipHitDesc)
            {
                _lastTipHitDesc = desc;
                BlockPreviewLog.Perf($"[诊断] 让位命中: {desc} | path={owner.GetPath()}");
            }
        }
        catch
        {
        }
    }

    private static bool IsBlockingHoverTipVisible()
    {
        try
        {
            if (_activeHoverTipsField?.GetValue(null) is System.Collections.IDictionary tips)
            {
                foreach (var owner in tips.Keys)
                {
                    // 诊断：把悬停提示的 owner 打到 mod 日志（只在新类型时打一行）。
                    // `Control` 这种匿名实例光看类型分不出来，所以把节点路径一起打出来。
                    if (owner is Node tipNode)
                    {
                        string desc = $"{tipNode.GetType().FullName} ({tipNode.Name})";

                        if (desc != _lastTipOwnerDesc)
                        {
                            _lastTipOwnerDesc = desc;
                            BlockPreviewLog.Perf($"[诊断] 悬停提示 owner: {desc} | path={tipNode.GetPath()}");
                        }

                        // 充能球专用：路径里带 Orb 就单独记一次（不管类型是否见过），
                        // 用来抓「悬停球被遮挡」时到底是谁在当 owner（2026-11-22）。
                        string tipPath = tipNode.GetPath().ToString();

                        if (tipPath.Contains("Orb", StringComparison.Ordinal))
                        {
                            string orbDesc = $"{tipNode.GetType().FullName} ({tipNode.Name})";

                            if (orbDesc != _lastOrbOwnerDesc)
                            {
                                _lastOrbOwnerDesc = orbDesc;
                                BlockPreviewLog.Perf($"[诊断] 球悬停 owner: {orbDesc} | path={tipPath}");
                            }
                        }
                    }

                    // 只让「体积大、确实会被压住」的提示：
                    //   · NInspectRelicScreen / NRelicInventoryHolder / NRelic —— 遗物详情
                    //   · NCreature / NPowerContainer / NPower             —— 生物状态栏 / 状态图标容器
                    // 卡牌 / 能量 / 牌堆按钮 / 结束回合这些小提示**不抢位置**，
                    // 否则鼠标扫过任何东西面板都会闪没（2026-11-21 踩过）。
                    if (owner is NInspectRelicScreen or NRelicInventoryHolder or NRelic
                        or NPowerContainer or NCreature or NPower or NOrb
                        && owner is Control control
                        && GodotObject.IsInstanceValid(control)
                        && control.IsVisibleInTree())
                    {
                        LogTipHit(control);
                        return true;
                    }

                    // 生物（人物 / 奥斯提 / 怪物）的悬停提示 owner 是 `NCreature` 里的
                    // 匿名 `Control`，节点名都是 `Hitbox`。但路径不一样（日志实测 2026-11-21）：
                    //   怪物：.../CombatSceneContainer/EnemyContainer/Creature/Hitbox
                    //   奥斯提：.../CombatSceneContainer/AllyContainer/@Control@2357/Hitbox   ← 中间多一层匿名节点
                    // 所以不能要求 `/Creature/`，改成认「Hitbox + 在战斗场景容器下」。
                    if (owner is Control hitbox
                        && GodotObject.IsInstanceValid(hitbox)
                        && hitbox.Name.ToString() == "Hitbox"
                        && hitbox.GetPath().ToString().Contains("CombatSceneContainer", StringComparison.Ordinal))
                    {
                        LogTipHit(hitbox);
                        return true;
                    }

                    // 充能球：悬停提示 owner 是球节点里的匿名 `Control`，名字 `Bounds`，
                    // 路径形如 .../Creature/OrbManager/Orbs/@Control@1400/Bounds（2026-11-22 实测）。
                    // 注意不是 `NOrb` 本身，所以只判断 NOrb 抓不到。
                    if (owner is Control orbBounds
                        && GodotObject.IsInstanceValid(orbBounds)
                        && orbBounds.Name.ToString() == "Bounds"
                        && orbBounds.GetPath().ToString().Contains("/Orbs/", StringComparison.Ordinal))
                    {
                        LogTipHit(orbBounds);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 读不到就当作没有，保证面板照常显示
        }

        return false;
    }

    // ---- 选牌界面检测（覆盖「选择悖论」这类不走 NPlayerHand 模式的界面）----
    //
    // 游戏所有选牌界面都实现 ICardSelectionScreen（奖励选牌 / 战斗牌堆选牌 /
    // 选择一张牌 / 净化…），用它在场景树里找「可见的那一个」比只看
    // NPlayerHand.CurrentMode 可靠。遍历整棵树偏贵，所以每 10 帧才查一次。
    private static long _overlayScanFrame = -1;
    private static bool _cardSelectVisible;
    private static bool _inspectRelicVisible;
    private static bool _fullScreenVisible;

    /// <summary>
    /// 每 10 帧扫一次场景树，识别两类覆盖层：
    ///   · `NInspectRelicScreen` —— **点开某个遗物**的查看界面（面板要整个隐藏）
    ///   · `ICardSelectionScreen` / `IOverlayScreen` —— 选牌 / 选遗物界面（面板挪到左下角）
    /// 遍历整棵树偏贵，所以按帧节流。
    /// </summary>
    private static void ScanOverlaysIfDue()
    {
        try
        {
            long frame = (long)Engine.GetProcessFrames();

            if (_overlayScanFrame >= 0 && frame - _overlayScanFrame < 10)
            {
                return;
            }

            _overlayScanFrame = frame;
            _cardSelectVisible = false;
            _inspectRelicVisible = false;
            _fullScreenVisible = false;

            var root = _playerBar?.GetTree()?.Root;

            if (root != null)
            {
                ScanOverlayNode(root);
            }
        }
        catch
        {
            // 扫描失败就当作「没有界面」，保证面板照常显示
        }
    }

    private static void ScanOverlayNode(Node node)
    {
        if (node is CanvasItem item && item.IsVisibleInTree())
        {
            if (node is NInspectRelicScreen)
            {
                // 「点开某个遗物」的详情界面：面板整个让位
                _inspectRelicVisible = true;
                LogOverlayFound(node);
            }
            else if (node is NDeckViewScreen or NInspectCardScreen or NMapScreen or NSettingsScreen
                     or NCardPileScreen or NCombatPileCardSelectScreen)
            {
                // 牌组查看 / 单卡查看 / 地图 / 设置 / 牌堆浏览 / 牌堆选牌：全屏界面，面板整个让位
                _fullScreenVisible = true;
                LogOverlayFound(node);
            }
            else if (node is NRunSubmenuStack submenu && submenu.SubmenusOpen)
            {
                // ESC 打开的运行时子菜单（暂停 / 设置 / 放弃…）：有子菜单压栈就让位
                _fullScreenVisible = true;
                LogOverlayFound(node);
            }
            else if (node is IOverlayScreen || HasCardSelectionInterface(node))
            {
                // 选牌 / 选遗物界面：面板保留，但挪到屏幕左下角
                _cardSelectVisible = true;
                LogOverlayFound(node);
            }
        }

        foreach (var child in node.GetChildren())
        {
            ScanOverlayNode(child);
        }
    }

    /// <summary>
    /// 覆盖层栈里有东西 → 打开了全屏界面（牌堆浏览、遗物查看、奖励界面…）。
    /// 这是游戏自己的权威判据，比逐个类型猜更可靠。
    /// </summary>
    private static bool IsOverlayStackBusy()
    {
        try
        {
            return NOverlayStack.Instance?.ScreenCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFullScreenOverlayVisible()
    {
        if (_fullScreenVisible)
        {
            return true;
        }

        try
        {
            var stack = NOverlayStack.Instance;

            if (stack == null || stack.ScreenCount <= 0)
            {
                return false;
            }

            // ⚠️ 选牌界面本身也是 IOverlayScreen，但它们的处理是「挪到左下角」而不是隐藏，
            // 所以栈顶是选牌界面时不算全屏遮挡（2026-11-21）。
            // ⚠️ 不能用 `is not ICardSelectionScreen`：正式版（0.111.x）没有这个接口，
            // 硬引用会编译失败。改为按接口名反射判断（2026-11-22）。
            return !HasCardSelectionInterface(stack.Peek() as Node);
        }
        catch
        {
            return false;
        }
    }

    private static string _lastOverlayDesc = string.Empty;

    /// <summary>诊断：覆盖层类型变化时打一行，确认净化 / 丢弃到底被谁命中。</summary>
    private static void LogOverlayFound(Node node)
    {
        string desc = node.GetType().Name;

        if (desc != _lastOverlayDesc)
        {
            _lastOverlayDesc = desc;
            BlockPreviewLog.Perf($"[诊断] 覆盖层: {desc} | path={node.GetPath()}");
        }
    }

    private static bool HasCardSelectionInterface(Node? node)
    {
        return HasCardSelectionInterface((object?)node);
    }

    /// <summary>
    /// 对象是否实现了 `ICardSelectionScreen`。
    ///
    /// 用反射按**接口名**判断，而不是 `is ICardSelectionScreen`：
    /// 正式版（v0.111.x）没有这个接口，硬引用会导致编译失败（2026-11-22）。
    /// </summary>
    private static bool HasCardSelectionInterface(object? obj)
    {
        if (obj == null)
        {
            return false;
        }

        try
        {
            foreach (var itf in obj.GetType().GetInterfaces())
            {
                if (itf.Name == "ICardSelectionScreen")
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsCardSelectScreenVisible()
    {
        ScanOverlaysIfDue();

        // 两种形态都算「选牌中」：
        //   · 独立选牌界面（净化 / 选择悖论…）→ 场景树里的 ICardSelectionScreen / IOverlayScreen
        //   · **手牌选择模式**（「选择 N 张牌来丢弃」这类）→ NPlayerHand.CurrentMode
        // 后者没有独立的 Screen 节点，只查场景树会漏掉（2026-11-21 实测）。
        return _cardSelectVisible || IsSelectionActive();
    }

    private static bool IsInspectRelicScreenVisible()
    {
        ScanOverlaysIfDue();
        return _inspectRelicVisible;
    }

    private static bool IsBarAlive()
    {
        return _playerBar != null
               && GodotObject.IsInstanceValid(_playerBar)
               // ⚠️ 地图 / 选牌 / 结算等界面里血条会被隐藏。
               // 面板挂在血条下面，此时也不该继续画——否则会浮在全屏 UI 之上
               //（配合 ZIndex 提高后尤其明显）。
               && _playerBar.IsVisibleInTree();
    }

    private static CanvasLayer? _screenLayer;

    private static void EnsureUi()
    {
        bool wantScreen = BlockPreviewConfig.PinToScreen;

        if (_panel != null && GodotObject.IsInstanceValid(_panel))
        {
            // 「跟随血条」和「强固定」的父节点类型不同（Control 父 / CanvasLayer 父），
            // 切换开关时需要重建面板，否则位置基准会错。
            if (wantScreen == (_panel.GetParent() is CanvasLayer))
            {
                return;
            }

            DestroyUi();
        }

        if (!IsBarAlive())
        {
            return;
        }

        var panel = new BlockPreviewPanel
        {
            Name = "BlockPreviewOverlay",
            IconTexture = FindBlockIcon(),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,

            // 面板默认挂在玩家血条下面，和怪物（NCreature）在同一个 CanvasLayer。
            // 怪物节点的绘制顺序比血条靠后，所以默认会被怪物盖住
            //（第二层 Boss 沙虫会把玩家拖到怪堆里，问题最明显）。
            // Godot 的排序是 (CanvasLayer.Layer, ZIndex, 树顺序)：
            //   · 100 足够压过怪物（它们都是默认 0）；
            //   · **不要用到 4096 上限**——那会连选牌 / 地图 / 结算这类全屏 UI 一起盖过去。
            // 非战斗界面的隐藏由 `IsBarAlive()` 里的 `IsVisibleInTree()` 负责。
            ZIndex = 100,
        };

        panel.OnDragMove = SyncFromPanel;
        panel.OnLock = LockAndSave;

        if (wantScreen)
        {
            EnsureScreenLayer().AddChild(panel);
        }
        else
        {
            _playerBar!.AddChild(panel);
        }

        panel.ApplyFonts(_playerBar!.GetNodeOrNull<Label>("%BlockLabel"));

        _panel = panel;
    }

    /// <summary>
    /// 「强固定 UI」用的独立层：面板位置就是**屏幕坐标**，不随血条移动。
    ///
    /// Layer 取 10：高于角色 / 怪物（默认 1），但低于牌堆浏览、结算、
    /// 玩家详情这类全屏弹窗——打开那些界面时它们会盖在面板上面，
    /// 不会被我们的 ZIndex 顶掉（这是 ZIndex=100 挂在血条层时做不到的）。
    /// </summary>
    private static CanvasLayer EnsureScreenLayer()
    {
        if (_screenLayer != null && GodotObject.IsInstanceValid(_screenLayer))
        {
            return _screenLayer;
        }

        _screenLayer = new CanvasLayer
        {
            Name = "BlockPreviewScreenLayer",
            Layer = 10,
        };

        _playerBar!.GetTree().Root.AddChild(_screenLayer);
        return _screenLayer;
    }

    private static Vector2 GetAnchor()
    {
        if (!IsBarAlive())
        {
            return Vector2.Zero;
        }

        var hp = _playerBar!.GetNodeOrNull<Control>("%HpBarContainer");
        return hp != null
            ? hp.Position + new Vector2(hp.Size.X, 0f)
            : new Vector2(_playerBar.Size.X, 0f);
    }

    private static void ApplyLayout()
    {
        if (_panel == null || !IsBarAlive())
        {
            return;
        }

        // 调整模式拖动中：不要每帧把面板位置拉回配置值，
        // 否则拖动会被"顶回去"、选牌界面里根本调不动（2026-11-21）。
        // 拖动结束后 SyncFromPanel 会把位置写回配置，下一帧自然对齐。
        if (_panel.IsDragging)
        {
            float dragScale = (float)BlockPreviewConfig.Scale;
            _panel.Scale = new Vector2(dragScale, dragScale);
            return;
        }

        bool pin = BlockPreviewConfig.PinToScreen;
        bool cardSelect = !pin && IsCardSelectScreenVisible();

        // 诊断：布局模式变化时打一行，确认选牌 / 选遗物界面到底有没有被识别到
        string mode = pin ? "pin" : (cardSelect ? "cardselect" : "normal");

        if (mode != _lastLayoutMode)
        {
            _lastLayoutMode = mode;
            BlockPreviewLog.Perf($"[诊断] 布局模式: {mode}");
        }

        // 强固定：位置就是屏幕坐标（面板挂在独立 CanvasLayer 上，原点是屏幕左上）
        if (pin)
        {
            var pos = new Vector2(
                (float)BlockPreviewConfig.OffsetX,
                (float)BlockPreviewConfig.OffsetY);

            // 兜底：旧值是「相对血条」的偏移（默认 88 / -146），直接当屏幕坐标会跑到屏幕外。
            // 越界时落到屏幕左下角，玩家在调整模式里拖一次就会覆盖成真实坐标。
            var viewport = _panel.GetViewportRect().Size;

            if (pos.X < 0f || pos.Y < 0f || pos.X > viewport.X - 40f || pos.Y > viewport.Y - 40f)
            {
                pos = new Vector2(40f, Math.Max(0f, viewport.Y - 220f));
            }

            _panel.Position = pos;
        }
        else if (IsCardSelectScreenVisible())
        {
            // 选牌 / 选遗物等二级界面：卡牌与遗物占据屏幕中央，
            // 而血条可能被 Boss 挪到中央——所以面板不再跟随血条，
            // 直接钉到「选牌界面位置」配置的屏幕坐标（可在调整模式里拖）。
            var viewport = _panel.GetViewportRect().Size;

            float x = Math.Clamp(
                (float)BlockPreviewConfig.SelectScreenX,
                0f,
                Math.Max(0f, viewport.X - 60f));
            float y = Math.Clamp(
                (float)BlockPreviewConfig.SelectScreenY,
                0f,
                Math.Max(0f, viewport.Y - 60f));

            _panel.GlobalPosition = new Vector2(x, y);
        }
        else
        {
            _panel.Position = GetAnchor() + GetEffectiveOffset();
        }

        float scale = (float)BlockPreviewConfig.Scale;
        _panel.Scale = new Vector2(scale, scale);
    }

    /// <summary>
    /// 当前角色的配置键（`CharacterModel` 子类名，如 `Necrobinder`）。
    /// 识别不到时返回空串，调用方一律退回主位置。
    /// </summary>
    private static string CurrentCharacterKey()
    {
        try
        {
            return _playerCreature?.Player?.Character?.GetType().Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>该角色是否「跟随主位置」（默认 true）。</summary>
    private static bool IsFollowing(string key)
    {
        return key switch
        {
            "Ironclad" => BlockPreviewConfig.FollowIronclad,
            "Silent" => BlockPreviewConfig.FollowSilent,
            "Defect" => BlockPreviewConfig.FollowDefect,
            "Regent" => BlockPreviewConfig.FollowRegent,
            "Necrobinder" => BlockPreviewConfig.FollowNecrobinder,
            "Fool" => BlockPreviewConfig.FollowFool,
            _ => true,
        };
    }

    /// <summary>
    /// 该角色实际生效的位置偏移：
    /// 跟随主位置（或识别不到角色）→ 全局 `OffsetX/OffsetY`；否则用角色自己的一套。
    /// </summary>
    private static Vector2 GetEffectiveOffset()
    {
        string key = CurrentCharacterKey();

        if (key.Length > 0 && !IsFollowing(key))
        {
            (double x, double y) offset = key switch
            {
                "Ironclad" => (BlockPreviewConfig.OffsetXIronclad, BlockPreviewConfig.OffsetYIronclad),
                "Silent" => (BlockPreviewConfig.OffsetXSilent, BlockPreviewConfig.OffsetYSilent),
                "Defect" => (BlockPreviewConfig.OffsetXDefect, BlockPreviewConfig.OffsetYDefect),
                "Regent" => (BlockPreviewConfig.OffsetXRegent, BlockPreviewConfig.OffsetYRegent),
                "Necrobinder" => (BlockPreviewConfig.OffsetXNecrobinder, BlockPreviewConfig.OffsetYNecrobinder),
                "Fool" => (BlockPreviewConfig.OffsetXFool, BlockPreviewConfig.OffsetYFool),
                _ => (BlockPreviewConfig.OffsetX, BlockPreviewConfig.OffsetY),
            };

            return new Vector2((float)offset.x, (float)offset.y);
        }

        return new Vector2(
            (float)BlockPreviewConfig.OffsetX,
            (float)BlockPreviewConfig.OffsetY);
    }

    private static Texture2D? FindBlockIcon()
    {
        if (!IsBarAlive())
        {
            return null;
        }

        var container = _playerBar!.GetNodeOrNull<Node>("%BlockContainer");
        return container == null ? null : FindFirstTexture(container);
    }

    private static Texture2D? FindFirstTexture(Node node)
    {
        if (node is TextureRect { Texture: not null } tr)
        {
            return tr.Texture;
        }

        foreach (var child in node.GetChildren())
        {
            if (FindFirstTexture(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void DestroyUi()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
        {
            _panel.QueueFree();
        }

        _panel = null;
        _draggedCard = null;

        // 强固定层随面板一起清掉（换战斗 / 切模式 / 关闭功能时）
        if (_screenLayer != null && GodotObject.IsInstanceValid(_screenLayer))
        {
            _screenLayer.QueueFree();
        }

        _screenLayer = null;
    }
}
