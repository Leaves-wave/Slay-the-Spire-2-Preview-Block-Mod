using System.Reflection;
using BaseLib.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 格挡预览 Mod：拖出卡牌时，在玩家血条右侧显示 "+X = Y"。
/// X = 这张牌会提供的格挡，Y = 当前格挡 + X。
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "BlockPreview";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static BlockPreviewConfig Config { get; private set; } = null!;

    public static void Initialize()
    {
        // 本地化 patch 要尽早注册，否则配置界面读表时还没有我们的条目
        var harmony = new Harmony(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // 注册设置项，让 BaseLib 的 Mod 设置界面能看到开关/位置/大小
        Config = new BlockPreviewConfig();
        ModConfigRegistry.Register(ModId, Config);

        // 配置项曾经短暂改成过中文名，把那种存档里的键迁回英文
        MigrateLegacyConfig();

        try
        {
            Config.Load();
        }
        catch (System.Exception e)
        {
            GD.Print($"[{ModId}] 配置加载失败，使用默认值: {e.Message}");
        }

        // 兜底：如果 settings_ui 表在 patch 注册前就加载完了，主动补一次
        ConfigLocalization.ApplyToCurrentTable();

        // 独立日志体系（与游戏 godot.log 分开，每类只保留最近 3 场）
        BlockPreviewLog.Initialize();

        GD.Print($"[{ModId}] initialized");
    }

    /// <summary>
    /// 把"中文键名"的存档迁移回英文键名。
    /// 之前有一版把属性名直接写成了中文，BaseLib 用属性名当配置键，属性名改回来之后
    /// 那份存档会读不到，玩家调好的偏移/大小就白调了。
    /// </summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            string path = System.IO.Path.Combine(OS.GetUserDataDir(), "mod_configs", "BlockPreview.cfg");

            if (!System.IO.File.Exists(path))
            {
                return;
            }

            var keyMap = new System.Collections.Generic.Dictionary<string, string>
            {
                ["启用预览"] = "Enabled",
                ["常驻显示"] = "AlwaysVisible",
                ["调整模式"] = "AdjustMode",
                ["水平偏移"] = "OffsetX",
                ["垂直偏移"] = "OffsetY",
                ["界面缩放"] = "Scale",
            };

            string json = System.IO.File.ReadAllText(path);
            var data = System.Text.Json.JsonSerializer
                .Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);

            if (data == null)
            {
                return;
            }

            bool changed = false;

            foreach (var pair in keyMap)
            {
                if (!data.TryGetValue(pair.Key, out var value))
                {
                    continue;
                }

                data.Remove(pair.Key);
                data[pair.Value] = value;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, options));
            GD.Print($"[{ModId}] 已把旧版配置的英文键名迁移为中文");
        }
        catch (System.Exception e)
        {
            GD.Print($"[{ModId}] 配置键名迁移失败（不影响使用）: {e.Message}");
        }
    }
}
