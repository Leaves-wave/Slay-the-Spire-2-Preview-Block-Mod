using System;
using Godot;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 第三行里的单瓶药水预览。
/// <paramref name="Uncertain"/> = true 时数值不是格挡、而是"由玩家决定的数量"（灰水），显示成 `+X?`。
/// </summary>
public readonly record struct PotionPreview(int Block, Texture2D? Icon, bool Uncertain = false);

/// <summary>
/// 预览条要显示的一组数值。
/// 布局语义：HandBlock + PlatingBlock + FrostBlock + EvokeBlock = TotalBlock
/// （手牌格挡 + 覆甲格挡 + 冰球被动 + 冰球激发 = 总护盾）
/// </summary>
public readonly record struct BlockPreviewValues
{
    public int HandBlock { get; init; }

    /// <summary>来自伤害的格挡（拳斗这类），图标用力量状态图标。</summary>
    public int DamageBlock { get; init; }

    /// <summary>蜃景（Mirage）按敌人中毒层数获得的格挡，图标用中毒状态图标。</summary>
    public int PoisonBlock { get; init; }

    /// <summary>逃脱计划（EscapePlan）的满额格挡：抽到技能牌就给这么多。</summary>
    public int EscapePlanBlock { get; init; }

    /// <summary>
    /// 逃脱计划的命中概率（0-100）。-1 表示不显示百分比
    /// （抽牌堆和弃牌堆都空，概率无意义）。只在 EscapePlanBlock &gt; 0 时才会被渲染。
    /// </summary>
    public int EscapePlanPercent { get; init; }

    /// <summary>
    /// 额外播放（爆发 / 复制药水 / 回声形态 / 遗物 / mod power）那几次带来的格挡，不含第一次。
    /// </summary>
    public int ExtraPlayBlock { get; init; }

    /// <summary>额外播放段的命中概率，只有逃脱计划这类概率牌才有；-1 表示不显示。</summary>
    public int ExtraPlayPercent { get; init; }

    public Texture2D? ExtraPlayIcon { get; init; }

    /// <summary>群星之子（ChildOfTheStarsPower）按消耗星数获得的格挡。</summary>
    public int StarChildBlock { get; init; }

    public Texture2D? StarChildIcon { get; init; }

    /// <summary>创世之柱（PillarOfCreationPower）按生成卡牌数获得的格挡。</summary>
    public int StarPillarBlock { get; init; }

    public Texture2D? StarPillarIcon { get; init; }

    /// <summary>召唤量（Bodyguard / PullAggro / DevourLife 等），显示成"为你而死"的护盾。</summary>
    public int OstySummonBlock { get; init; }

    public Texture2D? OstySummonIcon { get; init; }

    /// <summary>骨手的有效回复量（增生 Spur 这类牌）。</summary>
    public int OstyHealBlock { get; init; }

    public Texture2D? OstyHealIcon { get; init; }

    /// <summary>
    /// 奥斯提的当前生命值：它会替玩家挨伤害（"为你而死" + `ModifyUnblockedDamageTarget`），
    /// 所以这份生命相当于一层「自带壁垒的护盾」，计入总格挡。
    /// 图标用「为你而死」，和召唤段同源。
    /// </summary>
    public int OstyHpBlock { get; init; }

    public Texture2D? OstyHpIcon { get; init; }

    /// <summary>
    /// **真实格挡**：<see cref="TotalBlock"/> 里所有"真能挡伤害的格挡"之和。
    ///
    /// 排除奥斯提那三项（召唤量 / 骨手回复 / 奥斯提生命）——
    /// 它们提升的是「等效护盾」（骨手能承受多少），**但不是格挡**。
    /// 药水计算只能基于格挡（固化药水"格挡变三倍"就是典型），
    /// 拿含召唤的总值当基数会把它算成药水收益。
    /// </summary>
    public int RealBlock { get; init; }

    /// <summary>
    /// 灰烬之灵（SpiritOfAshPower）：每打出一张**虚无（Ethereal）**牌获得「层数」点格挡。
    /// 只在拖的牌本身带 `CardKeyword.Ethereal` 时才有值。
    /// </summary>
    public int SpiritOfAshBlock { get; init; }

    public Texture2D? SpiritOfAshIcon { get; init; }

    /// <summary>
    /// 勒紧（`FastenPower`）：给带 `CardTag.Defend` 标签的牌额外 +「层数」格挡。
    /// 卡面的 `PreviewValue` 本来已经含它，这里是为了单独展示而把它从手牌段里拆出来。
    /// </summary>
    public int FastenBlock { get; init; }

    public Texture2D? FastenIcon { get; init; }

    /// <summary>死亡之门（DeathsDoor）在本回合给过灾厄时额外两次的格挡。</summary>
    public int DeathDoorBlock { get; init; }

    public Texture2D? DeathDoorIcon { get; init; }

    /// <summary>灾厄之衣（ShroudPower）按给予灾厄次数获得的格挡。</summary>
    public int DoomCloakBlock { get; init; }

    public Texture2D? DoomCloakIcon { get; init; }

    /// <summary>所向无敌（IAmInvincible）在抽牌堆顶时自动打出的格挡。</summary>
    public int InvincibleBlock { get; init; }

    /// <summary>它在牌堆顶部的概率（0-100）；-1 表示不显示。</summary>
    public int InvinciblePercent { get; init; }

    public Texture2D? InvincibleIcon { get; init; }

    public int PlatingBlock { get; init; }

    public int FrostBlock { get; init; }

    public int EvokeBlock { get; init; }

    /// <summary>
    /// 溢出激发：生成新球时槽位已满，把队首（最右侧）的球挤出去所带来的格挡。
    /// 和主动激发（EvokeBlock）分开显示，方便分辨收益来源。
    /// </summary>
    public int OverflowBlock { get; init; }

    public Texture2D? OverflowIcon { get; init; }

    /// <summary>
    /// 药水：手里能直接给格挡的药水，**每瓶一项**（BlockPotion / ShipInABottle /
    /// Fortifier / Ashwater）。属于固定值，单独占第三行。
    /// </summary>
    public IReadOnlyList<PotionPreview>? Potions { get; init; }

    public int DanseBlock { get; init; }

    public int RageBlock { get; init; }

    public int AfterimageBlock { get; init; }

    public int FeelNoPainBlock { get; init; }

    public int FiendFireBlock { get; init; }

    public int FiendFireReplayBlock { get; init; }

    /// <summary>重振精神重放轮的期望格挡。</summary>
    public int SecondWindReplayBlock { get; init; }

    // ---------------- 遗物带来的格挡 ----------------

    /// <summary>骨笛（BoneFlute）：奥斯提攻击时获得格挡。</summary>
    public int BoneFluteBlock { get; init; }

    public Texture2D? BoneFluteIcon { get; init; }

    /// <summary>斗篷扣（CloakClasp）：回合结束时按手牌数获得格挡。</summary>
    public int CloakClaspBlock { get; init; }

    public Texture2D? CloakClaspIcon { get; init; }

    /// <summary>风的女儿（DaughterOfTheWind）：打出攻击牌时获得格挡。</summary>
    public int WindDaughterBlock { get; init; }

    public Texture2D? WindDaughterIcon { get; init; }

    /// <summary>奥里哈刚（Orichalcum）：回合结束时完全没有格挡才获得。</summary>
    public int OrichalcumBlock { get; init; }

    public Texture2D? OrichalcumIcon { get; init; }

    /// <summary>假奥里哈刚（FakeOrichalcum，假商人卖的赝品，3 点）。</summary>
    public int FakeOrichalcumBlock { get; init; }

    public Texture2D? FakeOrichalcumIcon { get; init; }

    /// <summary>波纹水盆（RippleBasin）：本回合没打出过攻击牌时获得。</summary>
    public int RippleBasinBlock { get; init; }

    public Texture2D? RippleBasinIcon { get; init; }

    /// <summary>结实绷带（ToughBandages）：每弃置一张牌获得格挡。</summary>
    public int ToughBandagesBlock { get; init; }

    /// <summary>星系尘埃：每累计 10 点星获得 10 格挡（StarsSpent 可读）。</summary>
    public int GalacticDustBlock { get; init; }

    public Texture2D? GalacticDustIcon { get; init; }

    /// <summary>精致折扇：每回合每 3 张攻击牌 +4 格挡。</summary>
    public int OrnamentalFanBlock { get; init; }

    public Texture2D? OrnamentalFanIcon { get; init; }

    /// <summary>音叉：每 10 张技能牌 +7 格挡（SkillsPlayed 可读，跨回合累计）。</summary>
    public int TuningForkBlock { get; init; }

    public Texture2D? TuningForkIcon { get; init; }

    /// <summary>骇人头盔：打出耗能 ≥ 2 的牌 +4 格挡。</summary>
    public int IntimidatingHelmetBlock { get; init; }

    public Texture2D? IntimidatingHelmetIcon { get; init; }

    /// <summary>永冻冰晶：每场战斗第一次打出能力牌 +7 格挡。</summary>
    public int PermafrostBlock { get; init; }

    public Texture2D? PermafrostIcon { get; init; }

    /// <summary>君王矿石：每回合第一次生成牌 +4 格挡。</summary>
    public int RegaliteBlock { get; init; }

    public Texture2D? RegaliteIcon { get; init; }

    /// <summary>镀金缆线：队首（最右侧）充能球的被动多触发一次带来的额外格挡。</summary>
    public int GoldCablesBlock { get; init; }

    public Texture2D? GoldCablesIcon { get; init; }

    /// <summary>算盘：抽牌堆不够抽、必然洗牌时给的格挡。</summary>
    public int AbacusBlock { get; init; }

    public Texture2D? AbacusIcon { get; init; }

    /// <summary>合股线（愚者）：箭被松脱（挨满 3 次强力攻击 / 目标死亡）时给的格挡。</summary>
    public int TwineBlock { get; init; }

    public Texture2D? TwineIcon { get; init; }

    /// <summary>歌之路（愚者）：本回合第一张啾（Cho）旋律牌给的格挡。</summary>
    public int SongPathBlock { get; init; }

    public Texture2D? SongPathIcon { get; init; }

    /// <summary>摇篮曲（愚者）：本回合集齐哇/啾/叽三种旋律时给的格挡。</summary>
    public int LullabyBlock { get; init; }

    public Texture2D? LullabyIcon { get; init; }

    /// <summary>万事无忧（愚者）：每生成一张「悲伤」给的格挡。</summary>
    public int AllIsWellBlock { get; init; }

    public Texture2D? AllIsWellIcon { get; init; }

    public Texture2D? ToughBandagesIcon { get; init; }

    /// <summary>
    /// 确定值总计：**不含**带问号的估算段
    /// （`FiendFireReplayBlock` / `SecondWindReplayBlock`）。
    /// 这是第一行 `=X` 显示的数字。
    /// </summary>
    public int TotalBlock { get; init; }

    /// <summary>
    /// 含估算的总计 = <see cref="TotalBlock"/> + 那两个问号段。
    /// 等于 <see cref="TotalBlock"/> 时说明这一手没有估算项，第二行不显示。
    /// </summary>
    public int EstimatedTotalBlock { get; init; }

    /// <summary>
    /// 本回合**即将受到**的攻击伤害（已减去 <see cref="TotalBlock"/>）。
    /// 显示在第一行末尾：`-X [剑]`（红色），致死时显示 `-NAN [灾厄]`。
    /// </summary>
    public int IncomingDamage { get; init; }

    /// <summary>这一击是否致死（受无实体 / 难杀 / 缓冲 / 孤注一掷影响）。</summary>
    public bool IncomingFatal { get; init; }

    /// <summary>无视格挡的生命流失（毒）：单独显示 `-Y [毒]`。</summary>
    public int IncomingUnblockable { get; init; }

    public Texture2D? PlatingIcon { get; init; }

    public Texture2D? FrostIcon { get; init; }

    public Texture2D? EvokeIcon { get; init; }

    public Texture2D? DanseIcon { get; init; }

    public Texture2D? RageIcon { get; init; }

    public Texture2D? AfterimageIcon { get; init; }

    public Texture2D? FeelNoPainIcon { get; init; }

    public Texture2D? FiendFireIcon { get; init; }

    public Texture2D? FiendFireReplayIcon { get; init; }

    public Texture2D? SecondWindReplayIcon { get; init; }

    public Texture2D? DamageIcon { get; init; }

    public Texture2D? PoisonIcon { get; init; }
}

/// <summary>
/// 预览条本体：+手牌 [盾] +覆甲 [覆甲] +冰球 [冰球] +激发 [引雷针] =总护盾 [盾]
/// 每段数值为 0 时整段隐藏。
/// 调整模式下可以用鼠标拖动，悬停时上方浮出半透明的"点击锁定"，
/// 点一下保存位置并退出调整模式。
/// </summary>
public partial class BlockPreviewPanel : Control
{
    /// <summary>一个"加项"：一个 +N 文本配一个图标，可整体隐藏。</summary>
    /// <summary>
    /// 一行 "图标 + 数值 (+ 可选百分比)"。
    /// PercentLabel 非空时渲染成 `+N [图标] A%` —— 图标紧跟在**数值**后面，
    /// 百分比再跟在图标后面（2026-11-20 调整：原来图标在最后，读起来像"概率的图标"）。
    /// </summary>
    private sealed record BonusRow(
        HBoxContainer Box,
        Label Label,
        TextureRect Icon,
        string Prefix,
        Label? PercentLabel = null);

    private VBoxContainer? _root;
    private HBoxContainer? _content;
    private Label? _totalLabel;
    private Label? _incomingLabel;
    private TextureRect? _incomingIcon;
    private Label? _unblockableLabel;
    private TextureRect? _unblockableIcon;

    /// <summary>「即将受到伤害」两段的红色（套完字体后要重新覆盖，因为 ApplyFontTo 会强制白色）。</summary>
    private static readonly Color DamageRed = new(1.0f, 0.35f, 0.3f);

    /// <summary>
    /// 第二行（估算行）：`=确定值 [盾] +不确定项… =含估算值? [盾]`。
    /// 只有存在不确定项时才显示。
    /// </summary>
    private HBoxContainer? _estimateRow;

    /// <summary>第二行开头的确定值（`=X`）。</summary>
    private Label? _estimateBaseLabel;

    private Label? _estimatedTotalLabel;

    private Label? _hint;

    // ---------------- 第一行：只放「确定」的段 ----------------

    private BonusRow? _handRow;
    private BonusRow? _damageRow;
    private BonusRow? _poisonRow;

    /// <summary>逃脱计划在命中率 100% 时算确定收益，显示在第一行（不带 %）。</summary>
    private BonusRow? _escapePlanRow;
    private BonusRow? _invincibleRow;

    private BonusRow? _platingRow;
    private BonusRow? _frostRow;
    private BonusRow? _evokeRow;
    private BonusRow? _overflowRow;
    private BonusRow? _danseRow;
    private BonusRow? _rageRow;
    private BonusRow? _afterimageRow;
    private BonusRow? _feelNoPainRow;
    private BonusRow? _spiritOfAshRow;
    private BonusRow? _fastenRow;
    private BonusRow? _fiendFireRow;
    private BonusRow? _starChildRow;
    private BonusRow? _starPillarRow;
    private BonusRow? _ostySummonRow;
    private BonusRow? _ostyHealRow;
    private BonusRow? _ostyHpRow;
    private BonusRow? _deathDoorRow;
    private BonusRow? _doomCloakRow;

    // ---------------- 第一行：遗物 ----------------

    private BonusRow? _boneFluteRow;
    private BonusRow? _cloakClaspRow;
    private BonusRow? _windDaughterRow;
    private BonusRow? _orichalcumRow;
    private BonusRow? _fakeOrichalcumRow;
    private BonusRow? _rippleBasinRow;
    private BonusRow? _toughBandagesRow;

    // 4 个"计数器型"遗物（2026-11-20）
    private BonusRow? _galacticDustRow;
    private BonusRow? _ornamentalFanRow;
    private BonusRow? _tuningForkRow;
    private BonusRow? _intimidatingHelmetRow;
    private BonusRow? _permafrostRow;
    private BonusRow? _regaliteRow;
    private BonusRow? _goldCablesRow;
    private BonusRow? _abacusRow;
    private BonusRow? _twineRow;
    private BonusRow? _songPathRow;
    private BonusRow? _lullabyRow;
    private BonusRow? _allIsWellRow;

    // ---------------- 第二行：所有「不确定」的项 ----------------
    // 带 ? 的估算值（FiendFireReplay / SecondWindReplay）
    // 带 % 的概率值（EscapePlan / ExtraPlay / Invincible）

    private BonusRow? _escapePlanEstimateRow;
    private BonusRow? _extraPlayEstimateRow;
    private BonusRow? _invincibleEstimateRow;
    private BonusRow? _fiendFireReplayEstimateRow;
    private BonusRow? _secondWindReplayEstimateRow;

    /// <summary>
    /// 第三行：药水。药水大多是**固定值**，而第二行展示的是"打这张牌算出来的期望"，
    /// 性质不同，所以单独占一行。
    /// 布局：`+X [药水1] +N [药水2] … =总计 [盾]`
    /// </summary>
    private HBoxContainer? _potionLine;

    /// <summary>装各瓶药水的容器（总计 label 在它外面，避免动态加行时被挤到前面）。</summary>
    private HBoxContainer? _potionItems;

    private readonly List<BonusRow> _potionBonusRows = new();

    /// <summary>
    /// 药水行末尾的「+召唤 / +骨手回复 / +奥斯提生命」。
    /// 这三项都不是格挡（所以不进基数、不吃药水倍率），
    /// 但它们是**等效护盾**的一部分，所以要作为加项补回总计。
    /// 只在**药水行本身可见**（手里至少有一瓶格挡药水）时才显示，
    /// 否则会和第一行重复。
    /// </summary>
    private BonusRow? _potionOstySummonRow;
    private BonusRow? _potionOstyHealRow;
    private BonusRow? _potionOstyHpRow;

    /// <summary>
    /// 第三行开头的确定总格挡（**不带 + 也不带 =**，和第二行开头的写法同款）。
    /// 药水是固定值，加在它上面得到"喝药之后的总格挡"。
    /// </summary>
    private Label? _potionBaseLabel;

    private Label? _potionTotalLabel;

    private Tween? _hintTween;

    private string _lastTotal = "";
    private string _lastEstimateBase = "";
    private string _lastEstimatedTotal = "";
    private string _lastPotionBase = "";
    private int _lastPotionOstySummon = -1;
    private int _lastPotionOstyHeal = -1;
    private int _lastPotionOstyHp = -1;
    private int _lastHand = -1;
    private int _lastDamage = -1;
    private int _lastPoison = -1;
    private int _lastEscapePlan = -1;
    private int _lastInvincibleRow = -1;
    private int _lastEscapePlanEstimate = -1;
    private int _lastEscapePlanPercent = -2;
    private int _lastEscapePlanEstimatePercent = -2;
    private int _lastPlating = -1;
    private int _lastFrost = -1;
    private int _lastEvoke = -1;
    private int _lastOverflow = -1;
    private int _lastDanse = -1;
    private int _lastRage = -1;
    private int _lastAfterimage = -1;
    private int _lastFeelNoPain = -1;
    private int _lastSpiritOfAsh = -1;
    private int _lastFasten = -1;
    private int _lastFiendFire = -1;
    private int _lastFiendFireReplay = -1;
    private int _lastSecondWindReplay = -1;
    private int _lastPotion = -1;
    private int _lastExtraPlay = -1;
    private int _lastExtraPlayPercent = -2;
    private int _lastStarChild = -1;
    private int _lastStarPillar = -1;
    private int _lastOstySummon = -1;
    private int _lastOstyHeal = -1;
    private int _lastOstyHp = -1;
    private int _lastDeathDoor = -1;
    private int _lastDoomCloak = -1;
    private int _lastInvincible = -1;
    private int _lastInvinciblePercent = -2;
    private int _lastBoneFlute = -1;
    private int _lastCloakClasp = -1;
    private int _lastWindDaughter = -1;
    private int _lastOrichalcum = -1;
    private int _lastFakeOrichalcum = -1;
    private int _lastRippleBasin = -1;
    private int _lastToughBandages = -1;
    private int _lastGalacticDust = -1;
    private int _lastOrnamentalFan = -1;
    private int _lastTuningFork = -1;
    private int _lastIntimidatingHelmet = -1;
    private int _lastPermafrost = -1;
    private int _lastRegalite = -1;
    private int _lastGoldCables = -1;
    private int _lastAbacus = -1;
    private int _lastTwine = -1;
    private int _lastSongPath = -1;
    private int _lastLullaby = -1;
    private int _lastAllIsWell = -1;

    private bool _pressed;
    private bool _moved;
    private Vector2 _dragStartLocal;
    private Vector2 _dragStartGlobal;

    public Texture2D? IconTexture { get; set; }

    public bool IsDragging => _pressed;

    /// <summary>拖动过程中回调，参数是面板当前的局部位置。</summary>
    public Action<Vector2>? OnDragMove { get; set; }

    /// <summary>单击（不是拖拽）时回调，用于锁定并保存。</summary>
    public Action? OnLock { get; set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = false;

        // 外层竖排容器：第一行是各段明细 + 确定总额，第二行是"含估算"总额
        _root = new VBoxContainer
        {
            Name = "Root",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _root.AddThemeConstantOverride("separation", 0);
        AddChild(_root);

        _content = new HBoxContainer
        {
            Name = "Content",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _content.AddThemeConstantOverride("separation", 2);
        _root.AddChild(_content);

        // +手牌格挡 [盾]
        _handRow = CreateBonusRow("HandBlock", IconTexture, "+");
        _content.AddChild(_handRow.Box);

        // +伤害转化格挡 [力量图标]（拳斗这类）
        _damageRow = CreateBonusRow("DamageBlock", null, " +");
        _content.AddChild(_damageRow.Box);

        // +蜃景按毒层获得的格挡 [中毒图标]
        _poisonRow = CreateBonusRow("PoisonBonus", null, " +");
        _content.AddChild(_poisonRow.Box);

        // +逃脱计划：命中率 100% 时是确定收益（不带 %），只在这时才出现在第一行
        _escapePlanRow = CreateBonusRow("EscapePlanBonus", IconTexture, " +");
        _content.AddChild(_escapePlanRow.Box);

        // +所向无敌：只有概率 100%（抽牌堆里全是它）时才是确定收益，这时才进第一行。
        // 其余情况一律在第二行按 "满额 + 概率" 显示。
        _invincibleRow = CreateBonusRow("InvincibleBonus", null, " +");
        _content.AddChild(_invincibleRow.Box);

        // +覆甲格挡 [覆甲图标]
        _platingRow = CreateBonusRow("PlatingBonus", null, " +");
        _content.AddChild(_platingRow.Box);

        // +冰球被动格挡 [冰球图标]
        _frostRow = CreateBonusRow("FrostBonus", null, " +");
        _content.AddChild(_frostRow.Box);

        // +冰球激发格挡 [引雷针图标]
        _evokeRow = CreateBonusRow("EvokeBonus", null, " +");
        _content.AddChild(_evokeRow.Box);

        // +溢出激发格挡（生成新球把队首冰球挤出去）[冰球图标]
        _overflowRow = CreateBonusRow("OverflowBonus", null, " +");
        _content.AddChild(_overflowRow.Box);

        // +死亡之舞格挡 [死亡之舞图标]
        _danseRow = CreateBonusRow("DanseBonus", null, " +");
        _content.AddChild(_danseRow.Box);

        // +狂怒格挡 [狂怒图标]
        _rageRow = CreateBonusRow("RageBonus", null, " +");
        _content.AddChild(_rageRow.Box);

        // +余像格挡 [余像图标]
        _afterimageRow = CreateBonusRow("AfterimageBonus", null, " +");
        _content.AddChild(_afterimageRow.Box);

        // +无惧疼痛格挡 [无惧疼痛图标]（手牌中虚无牌在回合结束消耗的收益）
        _feelNoPainRow = CreateBonusRow("FeelNoPainBonus", null, " +");
        _content.AddChild(_feelNoPainRow.Box);

        // +灰烬之灵格挡 [灰烬之灵图标]（打出的牌是虚无牌时，每次获得「层数」格挡）
        _spiritOfAshRow = CreateBonusRow("SpiritOfAshBonus", null, " +");
        _content.AddChild(_spiritOfAshRow.Box);

        // +勒紧格挡 [勒紧图标]（带 Defend 标签的牌额外获得「层数」格挡）
        _fastenRow = CreateBonusRow("FastenBonus", null, " +");
        _content.AddChild(_fastenRow.Box);

        // +恶魔之焰烧手牌的收益 [恶魔形态图标]
        _fiendFireRow = CreateBonusRow("FiendFireBonus", null, " +");
        _content.AddChild(_fiendFireRow.Box);

        // 注：恶魔之焰重放、重振精神重放期望这两段是估算值，
        // 已经移到第二行（=确定 +N? =含估算?）显示，不再占用第一行的明细。

        // +群星之子按消耗星数获得的格挡 [群星之子图标]
        _starChildRow = CreateBonusRow("StarChildBonus", null, " +");
        _content.AddChild(_starChildRow.Box);

        // +创世之柱按生成卡牌数获得的格挡 [创世之柱图标]
        _starPillarRow = CreateBonusRow("StarPillarBonus", null, " +");
        _content.AddChild(_starPillarRow.Box);

        // +召唤量 [为你而死图标]
        _ostySummonRow = CreateBonusRow("OstySummonBonus", null, " +");
        _content.AddChild(_ostySummonRow.Box);

        // +骨手有效回复 [生命图标]
        _ostyHealRow = CreateBonusRow("OstyHealBonus", null, " +");
        _content.AddChild(_ostyHealRow.Box);

        // +奥斯提生命 [为你而死图标]：它替玩家挨伤害，生命等于自带壁垒的护盾
        _ostyHpRow = CreateBonusRow("OstyHpBonus", null, " +");
        _content.AddChild(_ostyHpRow.Box);

        // +死亡之门额外两次格挡 [死亡之门图标]
        _deathDoorRow = CreateBonusRow("DeathDoorBonus", null, " +");
        _content.AddChild(_deathDoorRow.Box);

        // +灾厄之衣按灾厄次数获得的格挡 [灾厄之衣图标]
        _doomCloakRow = CreateBonusRow("DoomCloakBonus", null, " +");
        _content.AddChild(_doomCloakRow.Box);

        // 注：所向无敌（带在牌堆顶的概率）属于"不确定项"，放在第二行显示。

        // ---------------- 遗物 ----------------
        // 图标统一用遗物自己的图标（relic.Icon）

        // +骨笛（奥斯提攻击时）
        _boneFluteRow = CreateBonusRow("BoneFluteBonus", null, " +");
        _content.AddChild(_boneFluteRow.Box);

        // +斗篷扣（回合结束按手牌数）
        _cloakClaspRow = CreateBonusRow("CloakClaspBonus", null, " +");
        _content.AddChild(_cloakClaspRow.Box);

        // +风的女儿（打出攻击牌时）
        _windDaughterRow = CreateBonusRow("WindDaughterBonus", null, " +");
        _content.AddChild(_windDaughterRow.Box);

        // +奥里哈刚（回合结束无格挡时）
        _orichalcumRow = CreateBonusRow("OrichalcumBonus", null, " +");
        _content.AddChild(_orichalcumRow.Box);

        // +假奥里哈刚（假商人的赝品，3 点，逻辑相同）
        _fakeOrichalcumRow = CreateBonusRow("FakeOrichalcumBonus", null, " +");
        _content.AddChild(_fakeOrichalcumRow.Box);

        // +波纹水盆（本回合没出过攻击牌时）
        _rippleBasinRow = CreateBonusRow("RippleBasinBonus", null, " +");
        _content.AddChild(_rippleBasinRow.Box);

        // +结实绷带（每弃一张牌）
        _toughBandagesRow = CreateBonusRow("ToughBandagesBonus", null, " +");
        _content.AddChild(_toughBandagesRow.Box);

        // 4 个"计数器型"遗物
        _galacticDustRow = CreateBonusRow("GalacticDustBonus", null, " +");
        _content.AddChild(_galacticDustRow.Box);

        _ornamentalFanRow = CreateBonusRow("OrnamentalFanBonus", null, " +");
        _content.AddChild(_ornamentalFanRow.Box);

        _tuningForkRow = CreateBonusRow("TuningForkBonus", null, " +");
        _content.AddChild(_tuningForkRow.Box);

        _intimidatingHelmetRow = CreateBonusRow("IntimidatingHelmetBonus", null, " +");
        _content.AddChild(_intimidatingHelmetRow.Box);

        _permafrostRow = CreateBonusRow("PermafrostBonus", null, " +");
        _content.AddChild(_permafrostRow.Box);

        _regaliteRow = CreateBonusRow("RegaliteBonus", null, " +");
        _content.AddChild(_regaliteRow.Box);

        // +镀金缆线（队首球的被动多触发一次）
        _goldCablesRow = CreateBonusRow("GoldCablesBonus", null, " +");
        _content.AddChild(_goldCablesRow.Box);

        // +算盘（抽牌堆不够抽、必然洗牌）
        _abacusRow = CreateBonusRow("AbacusBonus", null, " +");
        _content.AddChild(_abacusRow.Box);

        // +合股线（愚者：箭被松脱）
        _twineRow = CreateBonusRow("TwineBonus", null, " +");
        _content.AddChild(_twineRow.Box);

        // +歌之路（愚者：本回合首张啾牌）
        _songPathRow = CreateBonusRow("SongPathBonus", null, " +");
        _content.AddChild(_songPathRow.Box);

        // +摇篮曲（愚者：集齐三种旋律）
        _lullabyRow = CreateBonusRow("LullabyBonus", null, " +");
        _content.AddChild(_lullabyRow.Box);

        // +万事无忧（愚者：每生成一张悲伤）
        _allIsWellRow = CreateBonusRow("AllIsWellBonus", null, " +");
        _content.AddChild(_allIsWellRow.Box);

        // 第一行末尾：=确定总护盾 [盾]
        _totalLabel = MakeLabel("TotalBlock");
        _content.AddChild(_totalLabel);
        _content.AddChild(MakeIcon(IconTexture));

        // 第一行末尾：本回合**即将受到**的伤害（红色，所有来源合并成一个数；不带图标）
        _incomingLabel = MakeLabel("IncomingDamage");
        _incomingLabel.AddThemeColorOverride("font_color", DamageRed);
        _incomingLabel.Visible = false;
        _content.AddChild(_incomingLabel);

        // 第二行（估算行）：
        //   =确定值 [盾]  +N A% [概率项…]  +N? [估算项…]  =含估算值? [盾]
        // 所有"不确定"的项都集中在这里；没有不确定项时整行隐藏。
        // 右对齐，让两行的盾图标纵向对齐。
        _estimateRow = new HBoxContainer
        {
            Name = "EstimateLine",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        _estimateRow.AddThemeConstantOverride("separation", 2);

        // 开头的确定值
        _estimateBaseLabel = MakeLabel("EstimateBaseLabel");
        _estimateRow.AddChild(_estimateBaseLabel);
        _estimateRow.AddChild(MakeIcon(IconTexture));

        // 概率项：逃脱计划（数值是"抽到技能牌时的满额"，带命中概率）
        _escapePlanEstimateRow = CreateBonusRow("EscapePlanEstimate", IconTexture, " +", withPercent: true);
        _estimateRow.AddChild(_escapePlanEstimateRow.Box);

        // 额外播放（爆发 / 复制药水 / 回声形态）；若来自逃脱计划会带概率
        _extraPlayEstimateRow = CreateBonusRow("ExtraPlayEstimate", null, " +", withPercent: true);
        _estimateRow.AddChild(_extraPlayEstimateRow.Box);

        // 所向无敌（带"在牌堆顶"的概率）
        _invincibleEstimateRow = CreateBonusRow("InvincibleEstimate", null, " +", withPercent: true);
        _estimateRow.AddChild(_invincibleEstimateRow.Box);

        // 恶魔之焰重放（估算，图标用恶魔形态，和第一轮同源）
        _fiendFireReplayEstimateRow = CreateBonusRow("FiendFireReplayEstimate", null, " +");
        _estimateRow.AddChild(_fiendFireReplayEstimateRow.Box);

        // 重振精神重放期望（估算，图标用迅速）
        _secondWindReplayEstimateRow = CreateBonusRow("SecondWindReplayEstimate", null, " +");
        _estimateRow.AddChild(_secondWindReplayEstimateRow.Box);

        // 结尾的"含估算总计"
        _estimatedTotalLabel = MakeLabel("EstimatedTotalLabel");
        _estimateRow.AddChild(_estimatedTotalLabel);
        _estimateRow.AddChild(MakeIcon(IconTexture));

        _root.AddChild(_estimateRow);

        // 第三行：药水（固定值，和"打牌的期望"分开）。
        // 第二行隐藏时它会自然上移到第二行的位置（VBox 不占位）。
        _potionLine = new HBoxContainer
        {
            Name = "PotionLine",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        _potionLine.AddThemeConstantOverride("separation", 2);

        // 开头的确定总格挡：`14 [盾] +12 [药水] … =26 [盾]`。
        // **不带 + 也不带 =**，与第二行开头（EstimateBaseLabel）的写法保持一致。
        _potionBaseLabel = MakeLabel("PotionBaseLabel");
        _potionLine.AddChild(_potionBaseLabel);
        _potionLine.AddChild(MakeIcon(IconTexture));

        _potionItems = new HBoxContainer
        {
            Name = "PotionItems",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _potionItems.AddThemeConstantOverride("separation", 2);
        _potionLine.AddChild(_potionItems);

        // 奥斯提三项（召唤 / 骨手回复 / 生命）：都不是格挡，所以不进基数、
        // 也不吃药水倍率；但它们是等效护盾的一部分，作为加项补回总计。
        // 只在药水行可见时出现，避免和第一行重复。
        _potionOstySummonRow = CreateBonusRow("PotionOstySummonBonus", null, " +");
        _potionLine.AddChild(_potionOstySummonRow.Box);

        _potionOstyHealRow = CreateBonusRow("PotionOstyHealBonus", null, " +");
        _potionLine.AddChild(_potionOstyHealRow.Box);

        _potionOstyHpRow = CreateBonusRow("PotionOstyHpBonus", null, " +");
        _potionLine.AddChild(_potionOstyHpRow.Box);

        // 末尾的总计
        _potionTotalLabel = MakeLabel("PotionTotalLabel");
        _potionLine.AddChild(_potionTotalLabel);
        _potionLine.AddChild(MakeIcon(IconTexture));

        _root.AddChild(_potionLine);

        _hint = new Label
        {
            Name = "LockHint",
            Text = "点击锁定",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(0, -34),
            Modulate = new Color(1f, 1f, 1f, 0f),
        };
        _hint.AddThemeFontSizeOverride("font_size", 16);
        _hint.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        _hint.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        _hint.AddThemeConstantOverride("outline_size", 5);
        AddChild(_hint);

        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;

        ApplyFonts();
    }

    public override void _Process(double delta)
    {
        if (!IsDragging)
        {
            BlockPreviewDisplay.TickAlways();
        }
    }

    /// <summary>更新显示的整组数值（内部带缓存，值没变就不重排）。</summary>
    public void SetValues(BlockPreviewValues values)
    {
        bool changed = false;

        // 这一手有没有"不确定项"（估算 ? / 概率 %）。
        // 没有就整条第二行隐藏，面板退回单行高度。
        int uncertainTotal = Math.Max(0, values.EstimatedTotalBlock - values.TotalBlock);
        bool showEstimateLine = uncertainTotal > 0;

        // ---- 第一行：只放确定的段 ----
        changed |= ApplyBonusRow(_handRow, values.HandBlock, null, ref _lastHand);
        changed |= ApplyBonusRow(_damageRow, values.DamageBlock, values.DamageIcon, ref _lastDamage);
        changed |= ApplyBonusRow(_poisonRow, values.PoisonBlock, values.PoisonIcon, ref _lastPoison);

        // 逃脱计划：命中率 100% 时这一抽必定是技能牌，属于确定收益，
        // 显示成普通加项（不带 %），也计入第一行的 =X。
        int escapePlanCertain = values.EscapePlanPercent >= 100 ? values.EscapePlanBlock : 0;
        changed |= ApplyBonusRow(_escapePlanRow, escapePlanCertain, IconTexture, ref _lastEscapePlan);

        // 所向无敌：和逃脱计划同一套规则 —— 概率 100% 才算确定收益、进第一行（不带 %）。
        // 小于 100% 时它只存在于第二行的估算行里。
        int invincibleCertain = values.InvinciblePercent >= 100 ? values.InvincibleBlock : 0;
        changed |= ApplyBonusRow(_invincibleRow, invincibleCertain, values.InvincibleIcon, ref _lastInvincibleRow);

        changed |= ApplyBonusRow(_platingRow, values.PlatingBlock, values.PlatingIcon, ref _lastPlating);
        changed |= ApplyBonusRow(_frostRow, values.FrostBlock, values.FrostIcon, ref _lastFrost);
        changed |= ApplyBonusRow(_evokeRow, values.EvokeBlock, values.EvokeIcon, ref _lastEvoke);
        changed |= ApplyBonusRow(_overflowRow, values.OverflowBlock, values.OverflowIcon, ref _lastOverflow);
        changed |= ApplyBonusRow(_danseRow, values.DanseBlock, values.DanseIcon, ref _lastDanse);
        changed |= ApplyBonusRow(_rageRow, values.RageBlock, values.RageIcon, ref _lastRage);
        changed |= ApplyBonusRow(_afterimageRow, values.AfterimageBlock, values.AfterimageIcon, ref _lastAfterimage);
        changed |= ApplyBonusRow(_feelNoPainRow, values.FeelNoPainBlock, values.FeelNoPainIcon, ref _lastFeelNoPain);
        changed |= ApplyBonusRow(_spiritOfAshRow, values.SpiritOfAshBlock, values.SpiritOfAshIcon, ref _lastSpiritOfAsh);
        changed |= ApplyBonusRow(_fastenRow, values.FastenBlock, values.FastenIcon, ref _lastFasten);
        changed |= ApplyBonusRow(_fiendFireRow, values.FiendFireBlock, values.FiendFireIcon, ref _lastFiendFire);
        changed |= ApplyBonusRow(_starChildRow, values.StarChildBlock, values.StarChildIcon, ref _lastStarChild);
        changed |= ApplyBonusRow(_starPillarRow, values.StarPillarBlock, values.StarPillarIcon, ref _lastStarPillar);
        changed |= ApplyBonusRow(_ostySummonRow, values.OstySummonBlock, values.OstySummonIcon, ref _lastOstySummon);
        changed |= ApplyBonusRow(_ostyHealRow, values.OstyHealBlock, values.OstyHealIcon, ref _lastOstyHeal);
        changed |= ApplyBonusRow(_ostyHpRow, values.OstyHpBlock, values.OstyHpIcon, ref _lastOstyHp);
        changed |= ApplyBonusRow(_deathDoorRow, values.DeathDoorBlock, values.DeathDoorIcon, ref _lastDeathDoor);
        changed |= ApplyBonusRow(_doomCloakRow, values.DoomCloakBlock, values.DoomCloakIcon, ref _lastDoomCloak);

        // 遗物段
        changed |= ApplyBonusRow(_boneFluteRow, values.BoneFluteBlock, values.BoneFluteIcon, ref _lastBoneFlute);
        changed |= ApplyBonusRow(_cloakClaspRow, values.CloakClaspBlock, values.CloakClaspIcon, ref _lastCloakClasp);
        changed |= ApplyBonusRow(_windDaughterRow, values.WindDaughterBlock, values.WindDaughterIcon, ref _lastWindDaughter);
        changed |= ApplyBonusRow(_orichalcumRow, values.OrichalcumBlock, values.OrichalcumIcon, ref _lastOrichalcum);
        changed |= ApplyBonusRow(_fakeOrichalcumRow, values.FakeOrichalcumBlock, values.FakeOrichalcumIcon, ref _lastFakeOrichalcum);
        changed |= ApplyBonusRow(_rippleBasinRow, values.RippleBasinBlock, values.RippleBasinIcon, ref _lastRippleBasin);
        changed |= ApplyBonusRow(_toughBandagesRow, values.ToughBandagesBlock, values.ToughBandagesIcon, ref _lastToughBandages);
        changed |= ApplyBonusRow(_galacticDustRow, values.GalacticDustBlock, values.GalacticDustIcon, ref _lastGalacticDust);
        changed |= ApplyBonusRow(_ornamentalFanRow, values.OrnamentalFanBlock, values.OrnamentalFanIcon, ref _lastOrnamentalFan);
        changed |= ApplyBonusRow(_tuningForkRow, values.TuningForkBlock, values.TuningForkIcon, ref _lastTuningFork);
        changed |= ApplyBonusRow(_intimidatingHelmetRow, values.IntimidatingHelmetBlock, values.IntimidatingHelmetIcon, ref _lastIntimidatingHelmet);
        changed |= ApplyBonusRow(_permafrostRow, values.PermafrostBlock, values.PermafrostIcon, ref _lastPermafrost);
        changed |= ApplyBonusRow(_regaliteRow, values.RegaliteBlock, values.RegaliteIcon, ref _lastRegalite);
        changed |= ApplyBonusRow(_goldCablesRow, values.GoldCablesBlock, values.GoldCablesIcon, ref _lastGoldCables);
        changed |= ApplyBonusRow(_abacusRow, values.AbacusBlock, values.AbacusIcon, ref _lastAbacus);
        changed |= ApplyBonusRow(_twineRow, values.TwineBlock, values.TwineIcon, ref _lastTwine);
        changed |= ApplyBonusRow(_songPathRow, values.SongPathBlock, values.SongPathIcon, ref _lastSongPath);
        changed |= ApplyBonusRow(_lullabyRow, values.LullabyBlock, values.LullabyIcon, ref _lastLullaby);
        changed |= ApplyBonusRow(_allIsWellRow, values.AllIsWellBlock, values.AllIsWellIcon, ref _lastAllIsWell);

        // 第一行末尾的确定总额
        string totalText = $"={values.TotalBlock}";

        if (totalText != _lastTotal)
        {
            _lastTotal = totalText;
            if (_totalLabel != null)
            {
                _totalLabel.Text = totalText;
            }

            changed = true;
        }

        // 第一行末尾：即将受到的攻击伤害（红色 -X [剑]，致死 -NAN [灾厄]）
        UpdateIncoming(values);

        // ---- 第二行：所有不确定项 ----
        // 布局：=确定值 [盾] +N A% [概率项] +N? [估算项] =含估算值? [盾]
        // 第二行隐藏时，各子项一并传 0，保证不会留下孤立的数字。
        // 逃脱计划只有「命中率 < 100%」时才算不确定项；
        // 100% 已经当成确定值放到第一行了，这里要排除掉，免得两行都显示。
        int escapePlanUncertain = 0;

        if (showEstimateLine && values.EscapePlanPercent < 100)
        {
            escapePlanUncertain = values.EscapePlanBlock;
        }

        int extraPlayBlock = showEstimateLine ? values.ExtraPlayBlock : 0;

        // 所向无敌：100% 的那份已经在第一行当确定值显示，这里要排除，免得两行重复。
        // 关掉估算行总闸时，<100% 的这段整个消失（和逃脱计划一致）。
        int invincibleBlock = showEstimateLine && values.InvinciblePercent < 100
            ? values.InvincibleBlock
            : 0;
        int fiendFireReplayBlock = showEstimateLine ? values.FiendFireReplayBlock : 0;
        int secondWindReplayBlock = showEstimateLine ? values.SecondWindReplayBlock : 0;

        changed |= ApplyChanceRow(_escapePlanEstimateRow, escapePlanUncertain,
            values.EscapePlanPercent, IconTexture,
            ref _lastEscapePlanEstimate, ref _lastEscapePlanEstimatePercent);

        changed |= ApplyChanceRow(_extraPlayEstimateRow, extraPlayBlock,
            values.ExtraPlayPercent, values.ExtraPlayIcon,
            ref _lastExtraPlay, ref _lastExtraPlayPercent);

        changed |= ApplyChanceRow(_invincibleEstimateRow, invincibleBlock,
            values.InvinciblePercent, values.InvincibleIcon,
            ref _lastInvincible, ref _lastInvinciblePercent);

        changed |= ApplyBonusRow(_fiendFireReplayEstimateRow, fiendFireReplayBlock,
            values.FiendFireReplayIcon, ref _lastFiendFireReplay, "?");

        changed |= ApplyBonusRow(_secondWindReplayEstimateRow, secondWindReplayBlock,
            values.SecondWindReplayIcon, ref _lastSecondWindReplay, "?");

        // 药水：固定值，单独第三行。只受 ShowPotion 控制（Display 里已过滤），
        // 不跟估算行总闸联动。
        // 格式：`{真实格挡} [盾] +X [药水1] +N [药水2] … =喝药后格挡 [盾]`
        //   - 开头的基数**不带 + 也不带 =**（与第二行开头同款）
        //   - 基数是**真实格挡**，排除奥斯提生命（生命不参与药水效果）
        //   - 结尾总值 = 真实格挡 + 各瓶药水
        var potions = values.Potions;
        int potionCount = potions?.Count ?? 0;

        // 按需补足 row（药水槽数量由遗物决定，可能超过 3）
        while (_potionBonusRows.Count < potionCount)
        {
            var row = CreateBonusRow($"PotionBonus{_potionBonusRows.Count}", null, " +");

            if (_potionItems != null)
            {
                _potionItems.AddChild(row.Box);
            }

            _potionBonusRows.Add(row);
        }

        int potionSum = 0;
        bool potionUncertain = false;

        for (int i = 0; i < _potionBonusRows.Count; i++)
        {
            var row = _potionBonusRows[i];
            bool used = i < potionCount;

            row.Box.Visible = used;

            if (!used)
            {
                row.Label.Text = "";
                continue;
            }

            var preview = potions![i];
            potionSum += preview.Block;

            if (preview.Uncertain)
            {
                potionUncertain = true;
            }

            row.Label.Text = " +" + preview.Block + (preview.Uncertain ? "?" : "");

            if (preview.Icon != null)
            {
                row.Icon.Texture = preview.Icon;
            }
        }

        // 开头的基数是**真实格挡**（`RealBlock`），不含奥斯提的三项非格挡收益：
        //   - 召唤量（提升骨手上限，不是护盾）
        //   - 骨手回复（治疗，不是护盾）
        //   - 奥斯提生命（承伤能力，会作为 +X [为你而死] 单独列在后面）
        // 药水（尤其固化药水"格挡变三倍"）只作用于格挡，
        // 混进这些会让玩家以为它们也被药水放大了。
        int potionBase = Math.Max(0, values.RealBlock);
        string potionBaseText = potionCount > 0 ? $"{potionBase}" : "";

        if (potionBaseText != _lastPotionBase)
        {
            _lastPotionBase = potionBaseText;

            if (_potionBaseLabel != null)
            {
                _potionBaseLabel.Text = potionBaseText;
            }

            changed = true;
        }

        // 奥斯提三项（召唤 / 骨手回复 / 生命）：都不是格挡（所以基数里没有），
        // 但它们是等效护盾的一部分，作为独立加项接在各瓶药水后面，
        // 让结尾总计重新回到"等效总护盾 + 药水"。
        // **只在药水行可见时**才列出来 —— 没有药水时它们不该出现，
        // 否则会和第一行重复（第一行已经有这三项了）。
        int potionOstySummon = potionCount > 0 ? Math.Max(0, values.OstySummonBlock) : 0;
        int potionOstyHeal = potionCount > 0 ? Math.Max(0, values.OstyHealBlock) : 0;
        int potionOstyHp = potionCount > 0 ? Math.Max(0, values.OstyHpBlock) : 0;

        changed |= ApplyBonusRow(_potionOstySummonRow, potionOstySummon,
            values.OstySummonIcon, ref _lastPotionOstySummon);

        changed |= ApplyBonusRow(_potionOstyHealRow, potionOstyHeal,
            values.OstyHealIcon, ref _lastPotionOstyHeal);

        changed |= ApplyBonusRow(_potionOstyHpRow, potionOstyHp,
            values.OstyHpIcon, ref _lastPotionOstyHp);

        // 结尾的总值 = 真实格挡 + 各瓶药水 + 奥斯提三项（等效总护盾）
        // 灰水那种"由玩家决定数量"的项会让总计也带上 ?
        int potionTotal = potionBase + potionSum
                          + potionOstySummon + potionOstyHeal + potionOstyHp;
        string potionTotalText = potionCount > 0
            ? $"={potionTotal}{(potionUncertain ? "?" : "")}"
            : "";

        if (_potionTotalLabel != null && _potionTotalLabel.Text != potionTotalText)
        {
            _potionTotalLabel.Text = potionTotalText;
        }

        bool showPotionLine = potionCount > 0;

        if (_potionLine != null && _potionLine.Visible != showPotionLine)
        {
            _potionLine.Visible = showPotionLine;
            changed = true;
        }

        // 第二行的开头（确定值，不带等号）与结尾（含估算值）
        string estimateBaseText = showEstimateLine ? $"{values.TotalBlock}" : "";
        string estimatedTotalText = showEstimateLine ? $"={values.EstimatedTotalBlock}?" : "";

        if (estimateBaseText != _lastEstimateBase)
        {
            _lastEstimateBase = estimateBaseText;

            if (_estimateBaseLabel != null)
            {
                _estimateBaseLabel.Text = estimateBaseText;
            }

            changed = true;
        }

        if (estimatedTotalText != _lastEstimatedTotal)
        {
            _lastEstimatedTotal = estimatedTotalText;

            if (_estimatedTotalLabel != null)
            {
                _estimatedTotalLabel.Text = estimatedTotalText;
            }

            changed = true;
        }

        if (_estimateRow != null && _estimateRow.Visible != showEstimateLine)
        {
            _estimateRow.Visible = showEstimateLine;
            changed = true;
        }

        if (changed || CustomMinimumSize == Vector2.Zero)
        {
            ResizeToContent();
        }
    }

    private static bool ApplyBonusRow(BonusRow? row, int amount, Texture2D? icon, ref int last, string suffix = "")
    {
        if (row == null)
        {
            return false;
        }

        bool visible = amount > 0;

        if (amount == last)
        {
            // 数值没变时，图标若换了也要跟着换（换球/换 buff 的情况）
            if (icon != null && row.Icon.Texture != icon)
            {
                row.Icon.Texture = icon;
            }

            return false;
        }

        last = amount;
        row.Box.Visible = visible;
        row.Label.Text = visible ? row.Prefix + amount + suffix : "";

        // 传 null 表示"保持原图标"（手牌段固定用盾图标）
        if (icon != null)
        {
            row.Icon.Texture = icon;
        }

        return true;
    }

    /// <summary>
    /// 一格"满额数值 + 概率"的行：显示成 `+3 50%`（percent &lt; 0 时只显示 `+3`）。
    /// 和 ApplyBonusRow 分开是因为这里的缓存要同时比较数值和百分比——
    /// 只看数值的话，"50% → 100%" 这种变化会被漏掉。
    /// </summary>
    private static bool ApplyChanceRow(BonusRow? row, int amount, int percent, Texture2D? icon, ref int lastAmount, ref int lastPercent)
    {
        if (row == null)
        {
            return false;
        }

        bool visible = amount > 0;

        if (amount == lastAmount && percent == lastPercent)
        {
            if (icon != null && row.Icon.Texture != icon)
            {
                row.Icon.Texture = icon;
            }

            return false;
        }

        lastAmount = amount;
        lastPercent = percent;
        row.Box.Visible = visible;

        if (icon != null)
        {
            row.Icon.Texture = icon;
        }

        if (!visible)
        {
            row.Label.Text = "";

            if (row.PercentLabel != null)
            {
                row.PercentLabel.Text = "";
            }
        }
        else
        {
            // 数值和百分比分成两个 Label，中间夹着图标：
            //   +N  [图标]  A%
            // 这样图标语义上属于"这个效果的数值"，而不是"概率"。
            row.Label.Text = $"{row.Prefix}{amount}";

            if (row.PercentLabel != null)
            {
                row.PercentLabel.Text = percent >= 0 ? $"{percent}%" : "";
            }
        }

        return true;
    }

    private void ResizeToContent()
    {
        if (_root == null)
        {
            return;
        }

        _root.ResetSize();
        var size = _root.GetCombinedMinimumSize();
        CustomMinimumSize = size;
        Size = size;
    }

    private BonusRow CreateBonusRow(string name, Texture2D? initialIcon, string prefix, bool withPercent = false)
    {
        var box = new HBoxContainer
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        box.AddThemeConstantOverride("separation", 2);

        var label = MakeLabel(name + "Label");
        box.AddChild(label);

        var icon = MakeIcon(initialIcon);
        box.AddChild(icon);

        Label? percentLabel = null;

        if (withPercent)
        {
            percentLabel = MakeLabel(name + "Percent");
            box.AddChild(percentLabel);
        }

        return new BonusRow(box, label, icon, prefix, percentLabel);
    }

    private static Label MakeLabel(string name)
    {
        return new Label
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>
    /// 更新第一行末尾的「即将受到伤害」：把所有来源合并成 `-X`（红色），致死显示 `-NAN`。
    /// 图标已按玩家反馈去掉（分开的图标不直观）。
    /// </summary>
    private void UpdateIncoming(BlockPreviewValues values)
    {
        int total = values.IncomingDamage + values.IncomingUnblockable;
        bool show = total > 0 || values.IncomingFatal;

        if (_incomingLabel != null)
        {
            _incomingLabel.Visible = show;

            if (show)
            {
                // 致死时在数值后面追加 " NAN"（玩家反馈纯 -NAN 看不出伤害量，不直观）
                _incomingLabel.Text = values.IncomingFatal ? $" -{total} NAN" : $" -{total}";
            }
        }

        // 图标 / 第二段一律不显示
        if (_incomingIcon != null)
        {
            _incomingIcon.Visible = false;
        }

        if (_unblockableLabel != null)
        {
            _unblockableLabel.Visible = false;
        }

        if (_unblockableIcon != null)
        {
            _unblockableIcon.Visible = false;
        }
    }

    private static TextureRect MakeIcon(Texture2D? texture)
    {
        return new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(28, 28),
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    /// <summary>套用游戏血条数字的字体，保证风格统一。</summary>
    public void ApplyFonts(Label? template = null)
    {
        // 第一行
        ApplyFontTo(_handRow?.Label, template);
        ApplyFontTo(_damageRow?.Label, template);
        ApplyFontTo(_poisonRow?.Label, template);
        ApplyFontTo(_escapePlanRow?.Label, template);
        ApplyFontTo(_invincibleRow?.Label, template);
        ApplyFontTo(_platingRow?.Label, template);
        ApplyFontTo(_frostRow?.Label, template);
        ApplyFontTo(_evokeRow?.Label, template);
        ApplyFontTo(_overflowRow?.Label, template);
        ApplyFontTo(_danseRow?.Label, template);
        ApplyFontTo(_rageRow?.Label, template);
        ApplyFontTo(_afterimageRow?.Label, template);
        ApplyFontTo(_feelNoPainRow?.Label, template);
        ApplyFontTo(_spiritOfAshRow?.Label, template);
        ApplyFontTo(_fastenRow?.Label, template);
        ApplyFontTo(_fiendFireRow?.Label, template);
        ApplyFontTo(_starChildRow?.Label, template);
        ApplyFontTo(_starPillarRow?.Label, template);
        ApplyFontTo(_ostySummonRow?.Label, template);
        ApplyFontTo(_ostyHealRow?.Label, template);
        ApplyFontTo(_ostyHpRow?.Label, template);
        ApplyFontTo(_deathDoorRow?.Label, template);
        ApplyFontTo(_doomCloakRow?.Label, template);
        ApplyFontTo(_boneFluteRow?.Label, template);
        ApplyFontTo(_cloakClaspRow?.Label, template);
        ApplyFontTo(_windDaughterRow?.Label, template);
        ApplyFontTo(_orichalcumRow?.Label, template);
        ApplyFontTo(_fakeOrichalcumRow?.Label, template);
        ApplyFontTo(_rippleBasinRow?.Label, template);
        ApplyFontTo(_toughBandagesRow?.Label, template);
        ApplyFontTo(_galacticDustRow?.Label, template);
        ApplyFontTo(_ornamentalFanRow?.Label, template);
        ApplyFontTo(_tuningForkRow?.Label, template);
        ApplyFontTo(_intimidatingHelmetRow?.Label, template);
        ApplyFontTo(_permafrostRow?.Label, template);
        ApplyFontTo(_regaliteRow?.Label, template);
        ApplyFontTo(_goldCablesRow?.Label, template);
        ApplyFontTo(_abacusRow?.Label, template);
        ApplyFontTo(_twineRow?.Label, template);
        ApplyFontTo(_songPathRow?.Label, template);
        ApplyFontTo(_lullabyRow?.Label, template);
        ApplyFontTo(_allIsWellRow?.Label, template);
        ApplyFontTo(_totalLabel, template);
        ApplyFontTo(_incomingLabel, template);
        ApplyFontTo(_unblockableLabel, template);

        // `ApplyFontTo` 会把 font_color 强制成白色（其它段都是白的），
        // 但这两段是"受伤提示"，必须保持红色 —— 所以套完字体后再覆盖一次。
        if (_incomingLabel != null)
        {
            _incomingLabel.AddThemeColorOverride("font_color", DamageRed);
        }

        if (_unblockableLabel != null)
        {
            _unblockableLabel.AddThemeColorOverride("font_color", DamageRed);
        }

        // 第二行（估算行）
        ApplyFontTo(_estimateBaseLabel, template);
        ApplyFontTo(_escapePlanEstimateRow?.Label, template);
        ApplyFontTo(_escapePlanEstimateRow?.PercentLabel, template);
        ApplyFontTo(_extraPlayEstimateRow?.Label, template);
        ApplyFontTo(_extraPlayEstimateRow?.PercentLabel, template);
        ApplyFontTo(_invincibleEstimateRow?.Label, template);
        ApplyFontTo(_invincibleEstimateRow?.PercentLabel, template);
        ApplyFontTo(_fiendFireReplayEstimateRow?.Label, template);
        ApplyFontTo(_secondWindReplayEstimateRow?.Label, template);
        foreach (var potionRow in _potionBonusRows)
        {
            ApplyFontTo(potionRow.Label, template);
        }

        ApplyFontTo(_potionOstySummonRow?.Label, template);
        ApplyFontTo(_potionOstyHealRow?.Label, template);
        ApplyFontTo(_potionOstyHpRow?.Label, template);
        ApplyFontTo(_potionTotalLabel, template);
        ApplyFontTo(_estimatedTotalLabel, template);
    }

    private static void ApplyFontTo(Label? label, Label? template)
    {
        if (label == null)
        {
            return;
        }

        if (template != null)
        {
            label.AddThemeFontOverride("font", template.GetThemeFont("font"));
            label.AddThemeFontSizeOverride("font_size", template.GetThemeFontSize("font_size"));
        }
        else
        {
            label.AddThemeFontSizeOverride("font_size", 22);
        }

        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        label.AddThemeConstantOverride("outline_size", 6);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (!BlockPreviewConfig.AdjustMode)
        {
            return;
        }

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _pressed = true;
                _moved = false;
                _dragStartLocal = Position;
                _dragStartGlobal = GetGlobalMousePosition();
                AcceptEvent();
            }
            else if (_pressed)
            {
                _pressed = false;
                if (!_moved)
                {
                    FadeHint(0f);
                    OnLock?.Invoke();
                }
                AcceptEvent();
            }

            return;
        }

        if (@event is InputEventMouseMotion && _pressed)
        {
            var now = GetGlobalMousePosition();
            if (now.DistanceTo(_dragStartGlobal) > 3f)
            {
                _moved = true;
            }

            Position = _dragStartLocal + (now - _dragStartGlobal);
            OnDragMove?.Invoke(Position);
            AcceptEvent();
        }
    }

    private void OnMouseEntered()
    {
        if (BlockPreviewConfig.AdjustMode)
        {
            FadeHint(1f);
        }
    }

    private void OnMouseExited()
    {
        if (!_pressed)
        {
            FadeHint(0f);
        }
    }

    private void FadeHint(float target)
    {
        if (_hint == null)
        {
            return;
        }

        _hintTween?.Kill();
        _hintTween = CreateTween();
        _hintTween.TweenProperty(_hint, "modulate:a", target, 0.15);
    }
}
