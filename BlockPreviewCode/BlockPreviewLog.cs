using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace BlockPreview.BlockPreviewCode;

/// <summary>三类诊断日志 + 崩溃日志。</summary>
public enum BlockPreviewLogKind
{
    /// <summary>格挡追踪：预览值 vs 实际获得值。</summary>
    Tracking,

    /// <summary>性能：refresh / persistent / 快照 / 牌堆重算。</summary>
    Perf,

    /// <summary>球推演：容量 / 球数 / 激发 / 溢出。</summary>
    Orb,

    /// <summary>崩溃：未处理异常、关键路径异常。</summary>
    Crash,
}

/// <summary>
/// Mod 自己的日志体系，**与游戏日志（godot.log）完全分开**。
///
/// 目录：<c>%APPDATA%\SlayTheSpire2\BlockPreviewLogs\</c>
/// 文件：<c>Block Preview mod_&lt;Type&gt;_&lt;时间戳&gt;.log</c>
///   - <c>Type</c> 是**全英文**的 <c>Tracking</c> / <c>Perf</c> / <c>Orb</c> / <c>Crash</c>，
///     避免中文文件名在某些环境下的路径问题（文件内容仍可以是中文）。
///
/// 关键约定：
///   - 功能日志（追踪 / 性能 / 球推演）**每场战斗换一组文件**，
///     每类只保留最近 <see cref="KeepPerKind"/> 场，更旧的自动删除。
///   - 崩溃日志每次启动一个文件，同样只保留最近 3 个，且**不受开关控制**。
///   - 每行即时 flush：游戏崩溃 / 被强杀时日志不会丢。
/// </summary>
public static class BlockPreviewLog
{
    /// <summary>文件名前缀（用户要求：Block Preview mod_日志名称类型）。</summary>
    private const string FilePrefix = "Block Preview mod_";

    /// <summary>每类日志保留的场次数。</summary>
    public const int KeepPerKind = 3;

    private static readonly object Gate = new();
    private static readonly Dictionary<BlockPreviewLogKind, StreamWriter?> Writers = new();

    /// <summary>每类日志当前正在写的文件路径（清理时排除它）。</summary>
    private static readonly Dictionary<BlockPreviewLogKind, string> Paths = new();

    private static string _dir = string.Empty;
    private static string _roundStamp = string.Empty;
    private static string _bootStamp = string.Empty;
    private static object? _lastCombat;
    private static bool _ready;

    /// <summary>功能日志的批量落盘间隔。崩溃日志不受它限制。</summary>
    private const int FlushIntervalMs = 500;

    private static long _lastFlushMs;

    // 预览去重：每帧都会进来，先用「对象引用 + 整数」挡住重复，避免每帧拼字符串
    private static CardModel? _lastPreviewCard;
    private static int _lastPreviewTotal;
    private static int _lastPreviewEstimated;
    private static int _lastPreviewCurrent;

    /// <summary>日志目录（供 UI / 文档提示）。</summary>
    public static string Folder => _dir;

    /// <summary>功能日志总闸（崩溃日志不走它）。</summary>
    private static bool Enabled
    {
        get
        {
            try
            {
                return BlockPreviewConfig.EnableLogs;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>在 MainFile.Initialize 里调用（配置加载之后）。</summary>
    public static void Initialize()
    {
        if (_ready)
        {
            return;
        }

        try
        {
            _dir = Path.Combine(OS.GetUserDataDir(), "BlockPreviewLogs");
            Directory.CreateDirectory(_dir);

            _bootStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            // 崩溃兜底：未捕获异常 / 后台 Task 异常都写进崩溃日志
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Crash("未处理异常", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Crash("未观察的 Task 异常", args.Exception);
                args.SetObserved();
            };

            _ready = true;

            // 崩溃日志一启动就建好，方便"游戏刚进就崩"也能抓到
            Write(BlockPreviewLogKind.Crash,
                $"=== 启动 === 版本 {MainFile.ModId}，用户数据目录 {OS.GetUserDataDir()}");
            CleanupOld(BlockPreviewLogKind.Crash);

            GD.Print($"[{MainFile.ModId}] 日志目录: {_dir}");
        }
        catch (Exception e)
        {
            GD.Print($"[{MainFile.ModId}] 日志初始化失败（不影响 mod）: {e.Message}");
        }
    }

    /// <summary>
    /// 由 <see cref="BlockPreviewDisplay.TickAlways"/> 每帧调用：
    /// 检测到**新的战斗状态对象**就轮转文件（每场一组）。
    /// </summary>
    public static void ObserveCombat(object? combatState)
    {
        if (!_ready)
        {
            return;
        }

        if (ReferenceEquals(combatState, _lastCombat))
        {
            return;
        }

        _lastCombat = combatState;

        if (combatState == null)
        {
            return;
        }

        StartRound();
    }

    /// <summary>开始新的一场：关掉旧 writer、换时间戳、清理旧文件。</summary>
    private static void StartRound()
    {
        lock (Gate)
        {
            try
            {
                CloseFunctionalWriters();
                _roundStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                _lastPreviewCard = null;

                // 每场开始先清理三类旧文件（各保留最近 3 个）
                CleanupOld(BlockPreviewLogKind.Tracking);
                CleanupOld(BlockPreviewLogKind.Perf);
                CleanupOld(BlockPreviewLogKind.Orb);

                // 只建"追踪"这一个，性能 / 球推演按需创建（没有输出就不落文件）
                if (Enabled)
                {
                    Write(BlockPreviewLogKind.Tracking,
                        $"=== 战斗开始 === 时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
            }
            catch (Exception e)
            {
                GD.Print($"[{MainFile.ModId}] 日志轮转失败: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    //  三类功能日志
    // ---------------------------------------------------------------

    /// <summary>格挡追踪：一次预览快照（同卡同数值只写一次）。</summary>
    public static void TrackPreview(CardModel? card, BlockPreviewValues values)
    {
        if (!Enabled || card == null)
        {
            return;
        }

        try
        {
            int current = card.Owner?.Creature?.Block ?? 0;

            // 每帧都会调到这里：先用对象引用 + 整数比较挡住重复，避免每帧拼字符串
            if (ReferenceEquals(card, _lastPreviewCard)
                && values.TotalBlock == _lastPreviewTotal
                && values.EstimatedTotalBlock == _lastPreviewEstimated
                && current == _lastPreviewCurrent)
            {
                return;
            }

            _lastPreviewCard = card;
            _lastPreviewTotal = values.TotalBlock;
            _lastPreviewEstimated = values.EstimatedTotalBlock;
            _lastPreviewCurrent = current;

            Write(BlockPreviewLogKind.Tracking,
                $"预览 {card.GetType().Name} | 本次盾 {values.HandBlock} | 确定 {values.TotalBlock} "
                + $"估算 {values.EstimatedTotalBlock} | 现有 {current} | 段: {Summarize(values)}");
        }
        catch
        {
            // 日志本身绝不影响游戏
        }
    }

    /// <summary>格挡追踪：一次实际获得格挡（来自 CombatHistory.BlockGained）。</summary>
    public static void TrackGain(int amount, ValueProp props, CardPlay? cardPlay, int blockAfter)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            string source = cardPlay?.Card?.GetType().Name ?? "非卡牌";

            Write(BlockPreviewLogKind.Tracking,
                $"实际 +{amount} 格挡 | 来源 {source} | props {props} | 现有 {blockAfter}");
        }
        catch
        {
        }
    }

    public static void Perf(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Write(BlockPreviewLogKind.Perf, message);
    }

    /// <summary>
    /// 记录一次**洗牌**（`CardPileCmd.Shuffle` 被调时）。
    ///
    /// 有什么用：
    ///   - 对照掉帧：洗牌瞬间性能日志里的 `牌堆重算` 与 `persistent` 峰值是不是同一条时间线；
    ///   - 对照算盘：算盘只在「必然洗牌」时给格挡，有这行就能核对判定时机。
    /// 行数很少（每次洗牌一行），不会刷屏。
    /// </summary>
    public static void TrackShuffle(Player player)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            int draw = PileType.Draw.GetPile(player).Cards.Count;
            int discard = PileType.Discard.GetPile(player).Cards.Count;

            Write(BlockPreviewLogKind.Tracking,
                $"洗牌 | 抽牌堆 {draw} 张 | 弃牌堆 {discard} 张");
        }
        catch
        {
        }
    }

    public static void Orb(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Write(BlockPreviewLogKind.Orb, message);
    }

    // ---------------------------------------------------------------
    //  崩溃
    // ---------------------------------------------------------------

    /// <summary>记录一次崩溃 / 关键异常。**不受功能日志开关控制**。</summary>
    public static void Crash(string context, Exception? exception)
    {
        try
        {
            if (!_ready)
            {
                GD.Print($"[{MainFile.ModId}] 崩溃({context}): {exception}");
                return;
            }

            lock (Gate)
            {
                var writer = EnsureWriter(BlockPreviewLogKind.Crash);
                writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] !!! {context}");
                writer?.WriteLine(exception?.ToString() ?? "（无异常对象）");
                writer?.Flush();
            }

            // 游戏日志里也留一行摘要，方便"日志目录都没找到"时兜底
            GD.Print($"[{MainFile.ModId}] 崩溃已写入日志({context}): {exception?.Message}");
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------
    //  内部实现
    // ---------------------------------------------------------------

    private static void Write(BlockPreviewLogKind kind, string message)
    {
        if (!_ready)
        {
            return;
        }

        if (kind != BlockPreviewLogKind.Crash && !Enabled)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var writer = EnsureWriter(kind);

                if (writer == null)
                {
                    return;
                }

                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

                // 崩溃日志立即落盘；功能日志攒到每 500ms 落一次 ——
                // 洗牌 / 连续拖牌时日志行会扎堆，每行一次刷盘会造成磁盘 IO 抖动。
                // 代价：极端崩溃时最多丢最后 0.5 秒的功能日志（崩溃日志本身不受影响）。
                if (kind == BlockPreviewLogKind.Crash
                    || System.Environment.TickCount64 - _lastFlushMs >= FlushIntervalMs)
                {
                    writer.Flush();
                    _lastFlushMs = System.Environment.TickCount64;
                }
            }
            catch
            {
            }
        }
    }

    private static StreamWriter? EnsureWriter(BlockPreviewLogKind kind)
    {
        if (Writers.TryGetValue(kind, out var existing) && existing != null)
        {
            return existing;
        }

        string stamp = kind == BlockPreviewLogKind.Crash ? _bootStamp : _roundStamp;

        if (string.IsNullOrEmpty(stamp))
        {
            return null;
        }

        string path = Path.Combine(_dir, $"{FilePrefix}{KindName(kind)}_{stamp}.log");
        // 带 BOM：Windows 记事本 / PowerShell 才能正确识别 UTF-8 中文
        // （不带 BOM 时 `Get-Content` 会按 GBK 读，日志就成乱码了）
        var writer = new StreamWriter(path, false, new UTF8Encoding(true))
        {
            AutoFlush = false,
        };

        writer.WriteLine($"# [{KindName(kind)}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {FilePrefix}{KindName(kind)}_{stamp}.log");

        Writers[kind] = writer;
        Paths[kind] = path;

        if (kind != BlockPreviewLogKind.Crash)
        {
            CleanupOld(kind);
        }

        return writer;
    }

    private static void CloseFunctionalWriters()
    {
        foreach (var kind in new[] { BlockPreviewLogKind.Tracking, BlockPreviewLogKind.Perf, BlockPreviewLogKind.Orb })
        {
            if (!Writers.TryGetValue(kind, out var writer) || writer == null)
            {
                continue;
            }

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch
            {
            }

            Writers[kind] = null;
            Paths.Remove(kind);
        }
    }

    /// <summary>删除同类旧文件，只保留最近 <see cref="KeepPerKind"/> 个（当前写入中的除外）。</summary>
    private static void CleanupOld(BlockPreviewLogKind kind)
    {
        try
        {
            string pattern = $"{FilePrefix}{KindName(kind)}_*.log";

            var files = Directory.GetFiles(_dir, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= KeepPerKind)
            {
                return;
            }

            Paths.TryGetValue(kind, out string? current);

            foreach (var fi in files.Skip(KeepPerKind))
            {
                if (current != null && string.Equals(fi.FullName, current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    fi.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>文件名的类型段：**全英文**，避免中文文件名在某些环境下的路径问题。</summary>
    private static string KindName(BlockPreviewLogKind kind)
    {
        return kind switch
        {
            BlockPreviewLogKind.Tracking => "Tracking",
            BlockPreviewLogKind.Perf => "Perf",
            BlockPreviewLogKind.Orb => "Orb",
            BlockPreviewLogKind.Crash => "Crash",
            _ => "Unknown",
        };
    }

    /// <summary>把非零的段拼成 `名=值` 列表，避免日志里全是 0。</summary>
    private static string Summarize(BlockPreviewValues values)
    {
        try
        {
            var parts = new List<string>();

            foreach (var prop in typeof(BlockPreviewValues).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(int))
                {
                    continue;
                }

                if (prop.Name is nameof(BlockPreviewValues.TotalBlock)
                    or nameof(BlockPreviewValues.EstimatedTotalBlock)
                    or nameof(BlockPreviewValues.RealBlock))
                {
                    continue;
                }

                // 百分比类字段（-1 表示"不适用"）不是格挡段，别混进日志
                if (prop.Name.EndsWith("Percent", StringComparison.Ordinal))
                {
                    continue;
                }

                if (prop.GetValue(values) is not int v || v == 0)
                {
                    continue;
                }

                string name = prop.Name.EndsWith("Block", StringComparison.Ordinal)
                    ? prop.Name[..^"Block".Length]
                    : prop.Name;

                parts.Add($"{name}={v}");
            }

            return parts.Count == 0 ? "（无）" : string.Join(" ", parts);
        }
        catch
        {
            return "（汇总失败）";
        }
    }
}
