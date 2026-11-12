using System.Text.RegularExpressions;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 解析调课指令，支持一条消息里包含多条（批量）：
///   #调课 第3节 语文改数学，第5节 英语改物理
///   #调课 第1节和第2节都换成自习
///   #调课 第3节和第5节互换
///   #撤销调课 第3,5节
/// </summary>
public sealed class TiaokeParser
{
    private const int MaxCommandsPerMessage = 10;

    // 节次：阿拉伯/中文数字
    private const string Num = @"[0-9零〇一二两三四五六七八九十百]{1,3}";
    // 科目：不含空白与标点/数字，长度 1-8
    private const string Subject = @"[^\s，,。；;、:：!！?？0-9]{1,8}";

    private static readonly Regex SinglePattern = new(
        $@"第\s*(?<lesson>{Num})\s*节\s*(?<from>{Subject})?\s*(?:改成|换成|改为|换为|调成|改|换)\s*(?<to>{Subject})",
        RegexOptions.Compiled);

    private static readonly Regex PairSamePattern = new(
        $@"第\s*(?<a>{Num})\s*节\s*(?:和|与|跟|、)\s*第\s*(?<b>{Num})\s*节\s*(?:都|一起|全部|均)?\s*(?:改成|换成|改为|换为|调成|改|换)\s*(?<to>{Subject})",
        RegexOptions.Compiled);

    private static readonly Regex SwapPattern = new(
        $@"第\s*(?<a>{Num})\s*节\s*(?:和|与|跟)\s*第\s*(?<b>{Num})\s*节\s*(?:互换|交换|对调)",
        RegexOptions.Compiled);

    // 撤销后的节次列表：第3,5节 / 第3、5节 / 第3节和第5节
    private static readonly Regex UndoLessonsPattern = new(
        $@"第\s*(?<ns>{Num}(?:\s*[,，、和与]\s*{Num})*)\s*节",
        RegexOptions.Compiled);

    private static readonly Regex NumberSplitPattern = new(@"[,，、和与]", RegexOptions.Compiled);

    // 撤销指令的触发词。注意「#撤销调课」里并不包含「#调课」，所以要单独识别。
    private static readonly string[] UndoTokens = ["#撤销调课", "#取消调课", "撤销调课", "取消调课", "撤销换课"];

    // 待确认 AI 调课的操作词（只有存在待确认结果时才有意义）。
    private static readonly string[] ConfirmTokens = ["#确认", "#应用"];
    private static readonly string[] AcceptDropTokens = ["#放弃"];

    /// <summary>是否为「确认应用待处理调课」。</summary>
    public static bool IsConfirmText(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && ConfirmTokens.Any(token => text.Contains(token, StringComparison.Ordinal));

    /// <summary>是否为「放弃待处理调课」。</summary>
    public static bool IsDropText(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && AcceptDropTokens.Any(token => text.Contains(token, StringComparison.Ordinal));

    /// <summary>这条文本是否像是给我们插件的指令（决定要不要进解析流程）。</summary>
    public bool MentionsCommand(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.Ordinal))
        {
            return true;
        }

        if (IsConfirmText(text) || IsDropText(text))
        {
            return true;
        }

        return UndoTokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>解析调课 / 撤销指令，可能返回多条。</summary>
    public TiaokeParseResult Parse(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TiaokeParseResult.Fail("消息内容为空");
        }

        // 撤销优先：避免「#撤销调课」被当成普通调课处理。
        if (UndoTokens.FirstOrDefault(token => text.Contains(token, StringComparison.Ordinal)) is { } undoToken)
        {
            return ParseUndo(text, undoToken);
        }

        var index = text.IndexOf(keyword, StringComparison.Ordinal);
        if (index < 0)
        {
            return TiaokeParseResult.Fail($"未找到关键词 {keyword}");
        }

        var payload = text[(index + keyword.Length)..];
        var segments = payload.Split(['，', ',', '；', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        var commands = new List<TiaokeCommand>();
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            // 1) 两节一起换同一科目：第1节和第2节都换成自习
            var pairSame = PairSamePattern.Match(segment);
            if (pairSame.Success
                && ParseNumber(pairSame.Groups["a"].Value) is { } a1
                && ParseNumber(pairSame.Groups["b"].Value) is { } b1)
            {
                var to = pairSame.Groups["to"].Value.Trim();
                commands.Add(TiaokeCommand.Apply(a1, string.Empty, to, text.Trim()));
                commands.Add(TiaokeCommand.Apply(b1, string.Empty, to, text.Trim()));
                continue;
            }

            // 2) 两节互换：第3节和第5节互换
            var swap = SwapPattern.Match(segment);
            if (swap.Success
                && ParseNumber(swap.Groups["a"].Value) is { } a2
                && ParseNumber(swap.Groups["b"].Value) is { } b2)
            {
                commands.Add(TiaokeCommand.Swap(a2, b2, text.Trim()));
                continue;
            }

            // 3) 单节调课：第3节 语文改数学
            var single = SinglePattern.Match(segment);
            if (single.Success && ParseNumber(single.Groups["lesson"].Value) is { } lesson)
            {
                var from = single.Groups["from"].Value.Trim();
                var to = single.Groups["to"].Value.Trim();
                if (to.Length == 0)
                {
                    continue;
                }

                if (string.Equals(from, to, StringComparison.Ordinal))
                {
                    continue;
                }

                commands.Add(TiaokeCommand.Apply(lesson, from, to, text.Trim()));
            }
        }

        if (commands.Count == 0)
        {
            return TiaokeParseResult.Fail("格式不符，应为「#调课 第X节 原科目改新科目」，多条可用逗号分隔");
        }

        if (commands.Count > MaxCommandsPerMessage)
        {
            return TiaokeParseResult.Fail($"一条消息最多支持 {MaxCommandsPerMessage} 条调课指令");
        }

        return TiaokeParseResult.Ok(commands);
    }

    private static TiaokeParseResult ParseUndo(string text, string undoToken)
    {
        var tail = text[(text.IndexOf(undoToken, StringComparison.Ordinal) + undoToken.Length)..];
        var lessons = new List<int>();
        foreach (Match match in UndoLessonsPattern.Matches(tail))
        {
            foreach (var part in NumberSplitPattern.Split(match.Groups["ns"].Value))
            {
                if (ParseNumber(part) is { } lesson && lesson > 0)
                {
                    lessons.Add(lesson);
                }
            }
        }

        var distinct = lessons.Distinct().OrderBy(x => x).ToList();
        if (distinct.Count == 0)
        {
            return TiaokeParseResult.Ok(TiaokeCommand.Undo(0, text.Trim()));
        }

        return TiaokeParseResult.Ok(distinct.Select(l => TiaokeCommand.Undo(l, text.Trim())).ToList());
    }

    /// <summary>支持阿拉伯数字与中文数字（一、二、两、十、十二、二十等）。</summary>
    internal static int? ParseNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw, out var arabic))
        {
            return arabic;
        }

        var result = 0;
        var current = 0;
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '十':
                    result += (current == 0 ? 1 : current) * 10;
                    current = 0;
                    break;
                case '百':
                    result += (current == 0 ? 1 : current) * 100;
                    current = 0;
                    break;
                case '零' or '〇':
                    break;
                default:
                    var digit = ch switch
                    {
                        '一' => 1,
                        '二' or '两' => 2,
                        '三' => 3,
                        '四' => 4,
                        '五' => 5,
                        '六' => 6,
                        '七' => 7,
                        '八' => 8,
                        '九' => 9,
                        _ => -1
                    };
                    if (digit < 0)
                    {
                        return null;
                    }

                    current = digit;
                    break;
            }
        }

        return result + current;
    }
}
