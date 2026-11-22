using System;
using MegaCrit.Sts2.Core.Debug;

namespace BlockPreview.BlockPreviewCode;

/// <summary>
/// 游戏版本探测：用来判断玩家跑的是「测试版」还是更旧的**正式版**。
///
/// 背景：mod 的算法是按测试版（v0.113.x）校准的，正式版（v0.111.1）缺少部分测试版
/// 才有的卡牌 / Power 类型。允许在正式版运行，但设置界面要提醒玩家「可能算错」。
/// </summary>
internal static class GameVersionInfo
{
    /// <summary>当前游戏版本字符串（如 `v0.111.1`）。读不到时返回 `?`。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                return ReleaseInfoManager.Instance?.ReleaseInfo?.Version ?? "?";
            }
            catch
            {
                return "?";
            }
        }
    }

    /// <summary>mod 算法校准所用的版本线。低于它就提示玩家。</summary>
    private const string CalibratedVersion = "0.113.0";

    /// <summary>
    /// 当前版本是否**低于**算法校准版本。
    ///
    /// 只做最朴素的三段数字比较（形如 `v0.111.1` / `0.113.1-beta`）；
    /// 解析不出来时按 `true` 处理 —— 宁可多提示一次，也不要漏提示。
    /// </summary>
    public static bool IsBelowCalibrated
    {
        get
        {
            return CompareVersions(CurrentVersion, CalibratedVersion) < 0;
        }
    }

    private static int CompareVersions(string left, string right)
    {
        try
        {
            int[]? a = Parse(left);
            int[]? b = Parse(right);

            if (a == null || b == null)
            {
                return -1;
            }

            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] < b[i] ? -1 : 1;
                }
            }

            return 0;
        }
        catch
        {
            return -1;
        }
    }

    private static int[]? Parse(string version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        string text = version.Trim();

        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(1);
        }

        // 去掉 `-beta` / `+build` 之类的后缀，只留数字段
        int cut = text.IndexOfAny(new[] { '-', '+' });

        if (cut >= 0)
        {
            text = text.Substring(0, cut);
        }

        string[] parts = text.Split('.');

        if (parts.Length < 3)
        {
            return null;
        }

        var result = new int[3];

        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], out result[i]))
            {
                return null;
            }
        }

        return result;
    }
}
