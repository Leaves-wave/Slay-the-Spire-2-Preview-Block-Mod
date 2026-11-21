using BaseLib.Config;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 设置项。BaseLib 会为每个属性自动生成一行 UI（数值型自动变成滑块）。
/// 注意：BaseLib 只支持 static 属性。
/// </summary>
public class BlockPreviewConfig : SimpleModConfig
{
    /// <summary>总开关：关掉后拖牌不再显示预览。</summary>
    [ConfigSection("GeneralSettings")]
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 常驻显示：战斗中一直显示当前格挡（拖牌时切换成预览值）。
    /// </summary>
    public static bool AlwaysVisible { get; set; } = true;

    /// <summary>
    /// 调整模式：打开后预览条在战斗中常驻显示，可以鼠标拖动位置，
    /// 悬停时上方浮出"点击锁定"，点一下保存位置并关闭该模式。
    /// </summary>
    public static bool AdjustMode { get; set; } = false;

    /// <summary>水平偏移（正值向右）。</summary>
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetX { get; set; } = 88;

    /// <summary>垂直偏移（正值向下）。</summary>
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetY { get; set; } = -146;

    /// <summary>整体缩放。</summary>
    [ConfigSlider(0.5, 3.0, 0.05)]
    public static double Scale { get; set; } = 1.2;

    /// <summary>
    /// 强固定 UI：开启后面板固定在屏幕坐标（用上面的 水平/垂直偏移），
    /// **不再跟随玩家血条移动**——Boss 把角色拖到别处时位置也不变。
    /// 关掉时沿用原来的「血条右侧 + 偏移」。
    /// </summary>
    public static bool PinToScreen { get; set; } = false;

    /// <summary>
    /// 选牌 / 弃牌 / 替换 / 消耗等**二级界面**里，面板的屏幕 X 坐标。
    ///
    /// 这些界面会占据屏幕中央，所以面板不再跟随血条，而是钉在你设的位置；
    /// 在调整模式里直接拖面板即可覆盖这两个值。
    /// </summary>
    [ConfigSlider(0, 3840, 1)]
    public static int SelectScreenX { get; set; } = 40;

    /// <summary>同上，Y 坐标（默认靠近屏幕底部）。</summary>
    [ConfigSlider(0, 2160, 1)]
    public static int SelectScreenY { get; set; } = 860;



    /// <summary>
    /// 估算行：当这一手存在带问号的估算段时，额外显示第二行
    /// 「=确定值 +N? =含估算值?」。
    ///
    /// 关掉后界面上完全不出现估算——第一行照常显示确定值，
    /// 估算段既不计入总数、也不渲染。适合只想看准数的人。
    /// </summary>
    public static bool ShowEstimateLine { get; set; } = true;

    /// <summary>
    /// 药水行（**独立的第三行**）：手里能直接给格挡的药水，
    /// 格式 `{确定总格挡} [盾] +X [药水1] +N [药水2] … =喝药后总值 [盾]`。
    ///
    /// 它是第三行的总闸，和第二行总闸 <see cref="ShowEstimateLine"/> 并列，
    /// 所以和它放在同一个分组里。
    /// 关掉后第三行整条消失，药水既不渲染也不计入任何总格挡。
    /// 与 <see cref="ShowEstimateLine"/> **互不影响**（药水不跟估算行联动）。
    /// </summary>
    public static bool ShowPotion { get; set; } = true;

    /// <summary>
    /// Mod 自己的日志总闸（默认开）。三类日志写在
    /// <c>%APPDATA%\SlayTheSpire2\BlockPreviewLogs\</c>，**与游戏日志 godot.log 分开**：
    /// 「格挡追踪」（预览值 vs 实际获得）、「性能」、「球推演」，每类只保留最近 3 场战斗。
    /// 崩溃日志不受本开关控制，始终记录。
    /// </summary>
    public static bool EnableLogs { get; set; } = true;

    // ---------------- 各段的显示开关 ----------------
    // 全部默认开启；觉得某段吵眼可以单独关掉。

    /// <summary>手牌格挡（这张牌本身给的）。</summary>
    [ConfigSection("DisplayToggles", CollapsedByDefault = true)]
    public static bool ShowHandBlock { get; set; } = true;

    /// <summary>伤害转化格挡（拳斗这类）。</summary>
    public static bool ShowDamageBlock { get; set; } = true;

    /// <summary>蜃景（按敌人毒层）。</summary>
    public static bool ShowMirage { get; set; } = true;

    /// <summary>逃脱计划（满额 + 概率）。</summary>
    public static bool ShowEscapePlan { get; set; } = true;

    /// <summary>额外播放（爆发 / 复制药水 / 回声形态）。</summary>
    public static bool ShowExtraPlay { get; set; } = true;

    /// <summary>群星之子（按消耗星数）。</summary>
    public static bool ShowStarChild { get; set; } = true;

    /// <summary>创世之柱（按生成卡牌数）。</summary>
    public static bool ShowStarPillar { get; set; } = true;

    /// <summary>召唤量（为你而死）。</summary>
    public static bool ShowOstySummon { get; set; } = true;

    /// <summary>骨手有效回复。</summary>
    public static bool ShowOstyHeal { get; set; } = true;

    /// <summary>
    /// 奥斯提生命：它会替玩家挨伤害（"为你而死"），
    /// 所以当前生命值相当于一层自带壁垒的护盾。
    /// </summary>
    public static bool ShowOstyHp { get; set; } = true;

    /// <summary>死亡之门额外两次格挡。</summary>
    public static bool ShowDeathDoor { get; set; } = true;

    /// <summary>灾厄之衣（按给予灾厄次数）。</summary>
    public static bool ShowDoomCloak { get; set; } = true;

    /// <summary>所向无敌（抽牌堆里有时常驻显示）。</summary>
    public static bool ShowInvincible { get; set; } = true;

    /// <summary>覆甲。</summary>
    public static bool ShowPlating { get; set; } = true;

    /// <summary>冰球被动。</summary>
    public static bool ShowFrost { get; set; } = true;

    /// <summary>冰球激发。</summary>
    public static bool ShowEvoke { get; set; } = true;

    /// <summary>溢出激发：生成新球时把队首冰球挤出去带来的格挡。</summary>
    public static bool ShowOverflow { get; set; } = true;

    /// <summary>死亡之舞。</summary>
    public static bool ShowDanse { get; set; } = true;

    /// <summary>狂怒。</summary>
    public static bool ShowRage { get; set; } = true;

    /// <summary>余像。</summary>
    public static bool ShowAfterimage { get; set; } = true;

    /// <summary>无惧疼痛。</summary>
    public static bool ShowFeelNoPain { get; set; } = true;

    /// <summary>灰烬之灵：打出虚无牌时获得格挡。</summary>
    public static bool ShowSpiritOfAsh { get; set; } = true;

    /// <summary>勒紧：带 Defend 标签的牌额外获得格挡。</summary>
    public static bool ShowFasten { get; set; } = true;

    /// <summary>恶魔之焰（燃烧手牌收益）。</summary>
    public static bool ShowFiendFire { get; set; } = true;

    /// <summary>重振精神的重放轮期望。</summary>
    public static bool ShowSecondWindReplay { get; set; } = true;

    /// <summary>
    /// 歌之路（SongPathPower）：本回合**第一张**啾（Cho）旋律牌打出时获得格挡。
    /// 愚者专属。拖带 Cho 标签的牌、且本回合还没打过 Cho 牌时才显示。
    /// </summary>
    public static bool ShowSongPath { get; set; } = true;

    /// <summary>
    /// 摇篮曲（LullabyPower）：本回合集齐哇/啾/叽三种旋律时获得一次格挡。
    /// 愚者专属。拖的牌补齐最后一种旋律时才显示（本回合已集齐过则不再触发）。
    /// </summary>
    public static bool ShowLullaby { get; set; } = true;

    /// <summary>
    /// 万事无忧（`AllIsWellPower`）：**每生成一张「悲伤」（Sadness）**获得「层数」点格挡。
    /// 愚者专属。拖会生成悲伤的牌时显示：
    /// `BrightenUp` / `SorrowfulStrike` / `Jest` / `SelfDeprecate` / `Rummage` / `Vitriol`。
    /// </summary>
    public static bool ShowAllIsWell { get; set; } = true;

    // ---------------- 遗物带来的格挡 ----------------

    /// <summary>骨笛：奥斯提攻击时获得格挡。</summary>
    [ConfigSection("RelicToggles", CollapsedByDefault = true)]
    public static bool ShowBoneFlute { get; set; } = true;

    /// <summary>斗篷扣：回合结束按手牌数获得格挡。</summary>
    public static bool ShowCloakClasp { get; set; } = true;

    /// <summary>风的女儿：打出攻击牌时获得格挡。</summary>
    public static bool ShowWindDaughter { get; set; } = true;

    /// <summary>奥里哈刚：回合结束完全没有格挡时才获得。</summary>
    public static bool ShowOrichalcum { get; set; } = true;

    /// <summary>波纹水盆：本回合没打出过攻击牌时获得。</summary>
    public static bool ShowRippleBasin { get; set; } = true;

    /// <summary>结实绷带：每弃置一张牌获得格挡。</summary>
    public static bool ShowToughBandages { get; set; } = true;
    /// <summary>星系尘埃：每累计消耗 10 点星获得格挡。</summary>
    public static bool ShowGalacticDust { get; set; } = true;

    /// <summary>精致折扇：每回合每 3 张攻击牌获得格挡。</summary>
    public static bool ShowOrnamentalFan { get; set; } = true;

    /// <summary>音叉：每 10 张技能牌获得格挡。</summary>
    public static bool ShowTuningFork { get; set; } = true;

    /// <summary>骇人头盔：打出高耗能牌时获得格挡。</summary>
    public static bool ShowIntimidatingHelmet { get; set; } = true;

    /// <summary>永冻冰晶：每场战斗第一次打出能力牌时获得格挡。</summary>
    public static bool ShowPermafrost { get; set; } = true;

    /// <summary>君王矿石：每回合第一次生成牌时获得格挡。</summary>
    public static bool ShowRegalite { get; set; } = true;

    /// <summary>
    /// 镀金缆线：**队首（最右侧）充能球的被动多触发一次**。
    /// 它本身不给格挡，只是把队首那颗冰球的被动放大一份，
    /// 所以这一段的值 = 推演结束后队首冰球的 `PassiveVal`。
    /// </summary>
    public static bool ShowGoldPlatedCables { get; set; } = true;

    /// <summary>
    /// 算盘：抽牌堆不够抽、**必然要洗牌**时给格挡。
    /// 只做确定场景（要抽的张数 &gt; 抽牌堆张数，且弃牌堆非空），
    /// 手牌上限会先削掉一部分抽牌；依赖牌序的场景一律不显示。
    /// </summary>
    public static bool ShowAbacus { get; set; } = true;

    /// <summary>
    /// 合股线（Twine）：箭被**松脱**时获得格挡。
    /// 两条松脱路径都要算：①该敌人挨满 3 次强力攻击（`ImpaledPower.HitCounter` 归零）
    /// ②该敌人死亡时 `DislodgeAllArrows` 一次全松脱。
    /// 只在拖**攻击牌**时估算（常驻显示不显示：不打牌就不会松脱）。
    /// </summary>
    public static bool ShowTwine { get; set; } = true;
    // ---------------- 按角色微调位置 ----------------
    //
    // 默认 **跟随主位置**（`FollowXxx = true`）：所有角色共用上面的 OffsetX / OffsetY，
    // 行为和以前完全一样。
    //
    // 关掉跟随（取消勾选）后，这个角色才用自己的偏移 —— 主要用于**亡灵契约师**：
    // 奥斯提站在同一画面里，面板会挡住它，而换角色又不想每次回主界面调。
    //
    // 分组默认**折叠**（`CollapsedByDefault = true`）；只有关掉跟随，
    // 两个滑块才会露出来（`ConfigVisibleIf`）。
    //
    // 默认值与主位置一致（88 / -146），所以第一次改的只是"从这个值开始拖"。

    [ConfigSection("CharIronclad", CollapsedByDefault = true)]
    public static bool FollowIronclad { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowIronclad), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXIronclad { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowIronclad), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYIronclad { get; set; } = -146;

    [ConfigSection("CharSilent", CollapsedByDefault = true)]
    public static bool FollowSilent { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowSilent), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXSilent { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowSilent), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYSilent { get; set; } = -146;

    [ConfigSection("CharDefect", CollapsedByDefault = true)]
    public static bool FollowDefect { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowDefect), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXDefect { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowDefect), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYDefect { get; set; } = -146;

    [ConfigSection("CharRegent", CollapsedByDefault = true)]
    public static bool FollowRegent { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowRegent), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXRegent { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowRegent), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYRegent { get; set; } = -146;

    [ConfigSection("CharNecrobinder", CollapsedByDefault = true)]
    public static bool FollowNecrobinder { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowNecrobinder), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXNecrobinder { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowNecrobinder), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYNecrobinder { get; set; } = -146;

    [ConfigSection("CharFool", CollapsedByDefault = true)]
    public static bool FollowFool { get; set; } = true;

    [ConfigVisibleIf(nameof(FollowFool), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetXFool { get; set; } = 88;

    [ConfigVisibleIf(nameof(FollowFool), false)]
    [ConfigSlider(-400, 400, 1)]
    public static double OffsetYFool { get; set; } = -146;

}
