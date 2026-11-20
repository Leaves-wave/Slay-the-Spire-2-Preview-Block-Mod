using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 给模组设置界面注入中英文本地化条目，让配置项跟随游戏语言切换。
///
/// BaseLib 生成标签时会查 <c>settings_ui</c> 表，key 的格式是：
///   ModPrefix + Slugify(属性名) + ".title"
/// 查不到才退回属性名本身（英文）。所以这里在 <see cref="LocManager"/> 加载表之后补进去。
/// </summary>
internal static class ConfigLocalization
{
    internal const string TableName = "settings_ui";

    /// <summary>BaseLib 的 GetType().GetPrefix() 对 BlockPreview.* 命名空间返回这个。</summary>
    private const string ModPrefix = "BLOCKPREVIEW-";

    private static readonly (string Name, string En, string Zh)[] Entries =
    {
        // 分组标题
        ("GeneralSettings", "General", "常规设置"),
        ("DisplayToggles", "Display toggles", "显示开关"),
        ("RelicToggles", "Relic toggles", "遗物开关"),

        // 常规设置
        ("Enabled", "Enable preview", "启用预览"),
        ("AlwaysVisible", "Always visible", "常驻显示"),
        ("AdjustMode", "Adjust position", "调整模式"),
        ("OffsetX", "Horizontal offset", "水平偏移"),
        ("OffsetY", "Vertical offset", "垂直偏移"),
        ("Scale", "UI scale", "界面缩放"),
        ("ShowEstimateLine", "Show estimate line", "显示估算行"),

        // 各段显示开关
        ("ShowHandBlock", "Show selected card block", "显示选中手牌格挡"),
        ("ShowDamageBlock", "Show damage-to-block", "显示伤害转格挡"),
        ("ShowMirage", "Show Mirage", "显示蜃景"),
        ("ShowEscapePlan", "Show Escape Plan", "显示逃脱计划"),
        ("ShowExtraPlay", "Show extra plays", "显示额外播放"),
        ("ShowStarChild", "Show Child of the Stars", "显示群星之子"),
        ("ShowStarPillar", "Show Pillar of Creation", "显示创世之柱"),
        ("ShowOstySummon", "Show Summon", "显示召唤"),
        ("ShowOstyHeal", "Show Osty recovery", "显示奥斯提恢复"),
        ("ShowOstyHp", "Show Osty life", "显示奥斯提生命"),
        ("ShowDeathDoor", "Show Death's Door", "显示死亡之门"),
        ("ShowDoomCloak", "Show Shroud", "显示灾厄之衣"),
        ("ShowInvincible", "Show I Am Invincible", "显示所向无敌"),
        ("ShowPlating", "Show Plating", "显示覆甲"),
        ("ShowFrost", "Show Frost passive", "显示冰球被动"),
        ("ShowEvoke", "Show Frost evoke", "显示冰球激发"),
        ("ShowOverflow", "Show orb overflow evoke", "显示溢出激发"),
        ("ShowPotion", "Show potion block", "显示药水格挡"),
        ("EnableLogs", "Enable mod logs", "开启 mod 日志"),

        // 按角色微调位置（6 组，默认折叠；关掉「跟随主位置」才露出两个滑块）
        ("CharIronclad", "Ironclad position", "铁甲战士 · 位置"),
        ("FollowIronclad", "Follow main position", "跟随主位置"),
        ("OffsetXIronclad", "X offset", "水平偏移"),
        ("OffsetYIronclad", "Y offset", "垂直偏移"),

        ("CharSilent", "Silent position", "静默猎手 · 位置"),
        ("FollowSilent", "Follow main position", "跟随主位置"),
        ("OffsetXSilent", "X offset", "水平偏移"),
        ("OffsetYSilent", "Y offset", "垂直偏移"),

        ("CharDefect", "Defect position", "故障机器人 · 位置"),
        ("FollowDefect", "Follow main position", "跟随主位置"),
        ("OffsetXDefect", "X offset", "水平偏移"),
        ("OffsetYDefect", "Y offset", "垂直偏移"),

        ("CharRegent", "Regent position", "储君 · 位置"),
        ("FollowRegent", "Follow main position", "跟随主位置"),
        ("OffsetXRegent", "X offset", "水平偏移"),
        ("OffsetYRegent", "Y offset", "垂直偏移"),

        ("CharNecrobinder", "Necrobinder position", "亡灵契约师 · 位置"),
        ("FollowNecrobinder", "Follow main position", "跟随主位置"),
        ("OffsetXNecrobinder", "X offset", "水平偏移"),
        ("OffsetYNecrobinder", "Y offset", "垂直偏移"),

        ("CharFool", "Fool position", "愚者 · 位置"),
        ("FollowFool", "Follow main position", "跟随主位置"),
        ("OffsetXFool", "X offset", "水平偏移"),
        ("OffsetYFool", "Y offset", "垂直偏移"),
        ("ShowDanse", "Show Danse Macabre", "显示死亡之舞"),
        ("ShowRage", "Show Rage", "显示狂怒"),
        ("ShowAfterimage", "Show Afterimage", "显示余像"),
        ("ShowFeelNoPain", "Show Feel No Pain", "显示无惧疼痛"),
        ("ShowSpiritOfAsh", "Show Spirit of Ash", "显示灰烬之灵"),
        ("ShowFasten", "Show Fasten", "显示勒紧"),
        ("ShowFiendFire", "Show Fiend Fire", "显示恶魔之焰"),
        ("ShowSecondWindReplay", "Show Second Wind", "显示重振精神"),
        ("ShowSongPath", "Show Song Path", "显示歌之路"),
        ("ShowLullaby", "Show Lullaby", "显示摇篮曲"),
        ("ShowAllIsWell", "Show All Is Well", "显示万事无忧"),

        // 遗物
        ("ShowBoneFlute", "Show Bone Flute", "显示骨笛"),
        ("ShowCloakClasp", "Show Cloak Clasp", "显示斗篷扣"),
        ("ShowWindDaughter", "Show Daughter of the Wind", "显示风的女儿"),
        ("ShowOrichalcum", "Show Orichalcum", "显示奥里哈刚"),
        ("ShowRippleBasin", "Show Ripple Basin", "显示波纹水盆"),
        ("ShowToughBandages", "Show Tough Bandages", "显示结实绷带"),
        ("ShowGalacticDust", "Show Galactic Dust", "显示星系尘埃"),
        ("ShowOrnamentalFan", "Show Ornamental Fan", "显示精致折扇"),
        ("ShowTuningFork", "Show Tuning Fork", "显示音叉"),
        ("ShowIntimidatingHelmet", "Show Intimidating Helmet", "显示骇人头盔"),
        ("ShowPermafrost", "Show Permafrost", "显示永冻冰晶"),
        ("ShowRegalite", "Show Regalite", "显示君王矿石"),
        ("ShowGoldPlatedCables", "Show Gold-Plated Cables", "显示镀金缆线"),
        ("ShowAbacus", "Show The Abacus", "显示算盘"),
        ("ShowTwine", "Show Twine", "显示合股线"),
    };

    /// <summary>
    /// 额外语言的文本（语言码 → 属性名 → 文本）。
    ///
    /// 游戏支持 17 种语言（`LocManager._supportedLanguages`：eng / ara / zhs / zht / deu /
    /// esp / fra / ind / ita / jpn / kor / pol / ptb / rus / spa / tha / tur）。
    /// 这里先补齐**繁中 / 日 / 韩**三套；其余语言**回退英文**，不会出现空白。
    ///
    /// 加一种语言只需在下面加一列，并在 `Translate` 里加一个 case —— 结构已经就绪。
    /// </summary>
    private static readonly (string Name, string Zht, string Jpn, string Kor)[] ExtraTexts =
    {
        ("GeneralSettings", "一般設定", "一般", "일반"),
        ("DisplayToggles", "顯示開關", "表示切替", "표시 토글"),
        ("RelicToggles", "遺物開關", "遺物切替", "유물 토글"),

        ("Enabled", "啟用預覽", "プレビューを有効化", "미리보기 사용"),
        ("AlwaysVisible", "常駐顯示", "常に表示", "항상 표시"),
        ("AdjustMode", "調整模式", "位置調整", "위치 조정"),
        ("OffsetX", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetY", "垂直偏移", "垂直オフセット", "세로 오프셋"),
        ("Scale", "介面縮放", "UI スケール", "UI 크기"),
        ("ShowEstimateLine", "顯示估算行", "推定行を表示", "추정 행 표시"),

        ("ShowHandBlock", "顯示選中手牌格擋", "選択中の手札のブロックを表示", "선택한 카드의 방어도 표시"),
        ("ShowDamageBlock", "顯示傷害轉格擋", "ダメージ→ブロックを表示", "피해→방어도 표시"),
        ("ShowMirage", "顯示蜃景", "蜃気楼を表示", "신기루 표시"),
        ("ShowEscapePlan", "顯示逃脫計劃", "脱出計画を表示", "탈출 계획 표시"),
        ("ShowExtraPlay", "顯示額外播放", "追加プレイを表示", "추가 플레이 표시"),
        ("ShowStarChild", "顯示群星之子", "星の子を表示", "별의 아이 표시"),
        ("ShowStarPillar", "顯示創世之柱", "創造の柱を表示", "창조의 기둥 표시"),
        ("ShowOstySummon", "顯示召喚", "召喚を表示", "소환 표시"),
        ("ShowOstyHeal", "顯示奧斯提恢復", "オスティ回復を表示", "오스티 회복 표시"),
        ("ShowOstyHp", "顯示奧斯提生命", "オスティの体力を表示", "오스티 생명 표시"),
        ("ShowDeathDoor", "顯示死亡之門", "死の扉を表示", "죽음의 문 표시"),
        ("ShowDoomCloak", "顯示災厄之衣", "災厄の衣を表示", "재앙의 옷 표시"),
        ("ShowInvincible", "顯示所向無敵", "無敵を表示", "무적 표시"),
        ("ShowPlating", "顯示覆甲", "装甲を表示", "장갑 표시"),
        ("ShowFrost", "顯示冰球被動", "フロストのパッシブを表示", "서리 구체 패시브 표시"),
        ("ShowEvoke", "顯示冰球激發", "フロストの発動を表示", "서리 구체 발동 표시"),
        ("ShowOverflow", "顯示溢出激發", "あふれ発動を表示", "넘침 발동 표시"),
        ("ShowPotion", "顯示藥水格擋", "ポーションのブロックを表示", "물약 방어도 표시"),
        ("EnableLogs", "開啟 mod 日誌", "mod ログを有効化", "mod 로그 사용"),

        ("CharIronclad", "鐵甲戰士 · 位置", "アイアンクラッド · 位置", "아이언클래드 · 위치"),
        ("FollowIronclad", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXIronclad", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYIronclad", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("CharSilent", "靜默獵手 · 位置", "サイレント · 位置", "사일런트 · 위치"),
        ("FollowSilent", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXSilent", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYSilent", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("CharDefect", "故障機器人 · 位置", "ディフェクト · 位置", "디펙트 · 위치"),
        ("FollowDefect", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXDefect", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYDefect", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("CharRegent", "儲君 · 位置", "リージェント · 位置", "리젠트 · 위치"),
        ("FollowRegent", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXRegent", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYRegent", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("CharNecrobinder", "亡靈契約師 · 位置", "ネクロバインダー · 位置", "네크로바인더 · 위치"),
        ("FollowNecrobinder", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXNecrobinder", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYNecrobinder", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("CharFool", "愚者 · 位置", "フール · 位置", "풀 · 위치"),
        ("FollowFool", "跟隨主位置", "メイン位置に追従", "메인 위치 따르기"),
        ("OffsetXFool", "水平偏移", "水平オフセット", "가로 오프셋"),
        ("OffsetYFool", "垂直偏移", "垂直オフセット", "세로 오프셋"),

        ("ShowDanse", "顯示死亡之舞", "死の舞踏を表示", "죽음의 춤 표시"),
        ("ShowRage", "顯示狂怒", "激怒を表示", "격노 표시"),
        ("ShowAfterimage", "顯示餘像", "残像を表示", "잔상 표시"),
        ("ShowFeelNoPain", "顯示無懼疼痛", "苦痛知らずを表示", "고통 모름 표시"),
        ("ShowSpiritOfAsh", "顯示灰燼之靈", "灰の精霊を表示", "재의 정령 표시"),
        ("ShowFasten", "顯示勒緊", "締め付けを表示", "조임 표시"),
        ("ShowFiendFire", "顯示惡魔之焰", "悪魔の炎を表示", "악마의 불꽃 표시"),
        ("ShowSecondWindReplay", "顯示重振精神", "再起を表示", "재기 표시"),
        ("ShowSongPath", "顯示歌之路", "歌の道を表示", "노래의 길 표시"),
        ("ShowLullaby", "顯示搖籃曲", "子守歌を表示", "자장가 표시"),
        ("ShowAllIsWell", "顯示萬事無憂", "万事順調を表示", "만사형통 표시"),

        ("ShowBoneFlute", "顯示骨笛", "骨の笛を表示", "뼈 피리 표시"),
        ("ShowCloakClasp", "顯示斗篷扣", "マントの留め具を表示", "망토 걸쇠 표시"),
        ("ShowWindDaughter", "顯示風的女兒", "風の娘を表示", "바람의 딸 표시"),
        ("ShowOrichalcum", "顯示奧里哈剛", "オリハルコンを表示", "오리하르콘 표시"),
        ("ShowRippleBasin", "顯示波紋水盆", "波紋の水盆を表示", "파문 물그릇 표시"),
        ("ShowToughBandages", "顯示結實繃帶", "頑丈な包帯を表示", "튼튼한 붕대 표시"),
        ("ShowGalacticDust", "顯示星系塵埃", "銀河の塵を表示", "은하의 먼지 표시"),
        ("ShowOrnamentalFan", "顯示精緻折扇", "装飾扇を表示", "장식 부채 표시"),
        ("ShowTuningFork", "顯示音叉", "音叉を表示", "소리굽쇠 표시"),
        ("ShowIntimidatingHelmet", "顯示駭人頭盔", "威圧の兜を表示", "위압 투구 표시"),
        ("ShowPermafrost", "顯示永凍冰晶", "永久凍土を表示", "영구 동토 표시"),
        ("ShowRegalite", "顯示君王礦石", "王者の鉱石を表示", "군왕 광석 표시"),
        ("ShowGoldPlatedCables", "顯示鍍金纜線", "金メッキケーブルを表示", "금도금 케이블 표시"),
        ("ShowAbacus", "顯示算盤", "そろばんを表示", "주판 표시"),
        ("ShowTwine", "顯示合股線", "より糸を表示", "합사 표시"),
    };

    /// <summary>按名字索引的额外语言表（构建一次）。</summary>
    private static readonly Dictionary<string, (string Zht, string Jpn, string Kor)> ExtraIndex = BuildExtraIndex();

    private static Dictionary<string, (string Zht, string Jpn, string Kor)> BuildExtraIndex()
    {
        var map = new Dictionary<string, (string, string, string)>(ExtraTexts.Length);

        foreach (var entry in ExtraTexts)
        {
            map[entry.Name] = (entry.Zht, entry.Jpn, entry.Kor);
        }

        return map;
    }

    private static FieldInfo? _translationsField;

    /// <summary>当前游戏语言码（如 <c>eng</c> / <c>zhs</c> / <c>jpn</c>）。读不到时按英文处理。</summary>
    private static string CurrentLanguageCode()
    {
        try
        {
            return (LocManager.Instance?.Language ?? "eng").ToLowerInvariant();
        }
        catch
        {
            return "eng";
        }
    }

    /// <summary>
    /// 从资源路径里取出这次加载的语言码（如 <c>res://localization/zhs/settings_ui.json</c> → <c>zhs</c>）。
    ///
    /// 必须用路径判断，不能读 <c>LocManager.Language</c>：
    /// <c>SetLanguage</c> 是「先 LoadTablesFromPath 加载表、后 SetLanguageInternal 赋 Language」，
    /// 所以在 LoadTable 的 postfix 里 Language 还是上一个语言，会正好判反。
    /// </summary>
    private static string? LanguageFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string[] parts = path.Replace('\\', '/').Split('/');

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals("localization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = parts[i + 1];

            // 形如 .../localization/zhs/settings_ui.json 才是原版语言目录；
            // mod 自己的表是 .../localization/settings_ui.json（这里取到的是文件名），跳过
            if (code.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return code.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>
    /// 取某个配置项在指定语言下的文本。
    /// 优先级：该语言的专表（繁中 / 日 / 韩）→ 中文（`zh*`）→ 英文。
    /// 其余语言（deu / fra / rus / …）**回退英文**，保证不出现空白。
    /// </summary>
    private static string Translate(string name, string lang)
    {
        if (ExtraIndex.TryGetValue(name, out var extra))
        {
            switch (lang)
            {
                case "zht":
                    return extra.Zht;
                case "jpn":
                    return extra.Jpn;
                case "kor":
                    return extra.Kor;
            }
        }

        foreach (var entry in Entries)
        {
            if (entry.Name != name)
            {
                continue;
            }

            return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? entry.Zh : entry.En;
        }

        return name;
    }

    public static void Apply(Dictionary<string, string>? table, string? path = null)
    {
        if (table == null)
        {
            return;
        }

        string lang = LanguageFromPath(path) ?? CurrentLanguageCode();

        foreach (var entry in Entries)
        {
            string key = ModPrefix + StringHelper.Slugify(entry.Name) + ".title";
            table[key] = Translate(entry.Name, lang);
        }
    }

    /// <summary>
    /// 兜底：如果 Harmony 注册时表已经加载过了，主动往当前表里补一次。
    /// </summary>
    public static void ApplyToCurrentTable()
    {
        try
        {
            var table = LocManager.Instance?.GetTable(TableName);

            if (table == null)
            {
                return;
            }

            _translationsField ??= AccessTools.Field(typeof(LocTable), "_translations");

            if (_translationsField?.GetValue(table) is Dictionary<string, string> dict)
            {
                Apply(dict);
            }
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 配置本地化注入失败: {e.Message}");
        }
    }
}

/// <summary>
/// 游戏每次加载本地化表都会走这里，语言切换后重新加载也能补上。
/// </summary>
[HarmonyPatch(typeof(LocManager), "LoadTable")]
internal static class ConfigLocalizationPatch
{
    [HarmonyPostfix]
    private static void Postfix(string path, Dictionary<string, string>? __result)
    {
        if (__result == null || !path.Contains(ConfigLocalization.TableName))
        {
            return;
        }

        ConfigLocalization.Apply(__result, path);
    }
}
