using HarmonyLib;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.ValueProps;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 记住"玩家自己"的血条节点。每个 NHealthBar 只会 SetCreature 一次。
/// </summary>
[HarmonyPatch(typeof(NHealthBar), nameof(NHealthBar.SetCreature))]
internal static class PlayerHealthBarPatch
{
    [HarmonyPostfix]
    private static void Postfix(NHealthBar __instance, Creature creature)
    {
        if (creature.IsPlayer)
        {
            BlockPreviewDisplay.AttachPlayerHealthBar(__instance, creature);
        }
    }
}

/// <summary>
/// 卡牌被拖出（开始选目标）时触发。
/// 游戏内部经由 NPlayerHand.StartCardPlay 调用 HoveredModelTracker.OnLocalCardSelected。
/// </summary>
[HarmonyPatch(typeof(HoveredModelTracker), nameof(HoveredModelTracker.OnLocalCardSelected))]
internal static class CardDragStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel cardModel)
    {
        BlockPreviewDisplay.Show(cardModel);
    }
}

/// <summary>
/// 拖拽结束（打出或松手取消）时隐藏。
/// </summary>
[HarmonyPatch(typeof(HoveredModelTracker), nameof(HoveredModelTracker.OnLocalCardDeselected))]
internal static class CardDragEndPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        BlockPreviewDisplay.Hide();
    }
}

/// <summary>
/// 记录"实际获得的格挡"，用于和预览值对照（格挡追踪日志）。
///
/// 为什么挂在 <see cref="CombatHistory.BlockGained"/> 而不是 `CreatureCmd.GainBlock`：
///   - 它是**最终值**的落点（`Hook.ModifyBlock` 算完之后才写进来），
///     所以敏捷 / 融入暗影 / 不可格挡等修正都已经包含；
///   - 它是同步方法，参数里有 `receiver / amount / props / cardPlay`，来源信息完整；
///   - `GainBlock` 是 `async Task`，Postfix 只能拿到未完成的 Task，拿不到实际数值。
/// </summary>
[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.BlockGained))]
internal static class BlockGainedLogPatch
{
    [HarmonyPostfix]
    private static void Postfix(Creature receiver, int amount, ValueProp props, CardPlay? cardPlay)
    {
        try
        {
            // 只记玩家自己获得的格挡（敌人格挡我们不关心，也会刷屏）
            if (!receiver.IsPlayer || amount <= 0)
            {
                return;
            }

            BlockPreviewLog.TrackGain(amount, props, cardPlay, receiver.Block);
        }
        catch
        {
            // 日志绝不影响游戏
        }
    }
}

/// <summary>
/// 记录**洗牌**事件，方便把日志里的掉帧/算盘判定对到同一条时间线。
///
/// 为什么挂 `CardPileCmd.Shuffle` 的 **Prefix**：
///   - 它是 `async Task`，Postfix 只能拿到未完成的 Task，拿不到完成时机；
///   - Prefix 在调用瞬间执行，参数里已经有 `player`，足够记录「什么时候洗的、两堆各多少张」。
/// 只写一行（每次洗牌一次），不刷屏；`EnableLogs` 关闭时 `TrackShuffle` 自己会跳过。
/// </summary>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Shuffle))]
internal static class ShuffleLogPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player)
    {
        try
        {
            BlockPreviewLog.TrackShuffle(player);
        }
        catch
        {
            // 日志绝不影响游戏
        }
    }
}
