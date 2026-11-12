using System.Text.RegularExpressions;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 解析「#调课 第X节 A改B」与「#撤销调课」这两类群聊/私聊指令。
/// </summary>
public sealed class TiaokeParser
{
    // A改B / A换B / A改成B / A换成B 都接受；科目名限制长度，避免把整句吞进去。
    // 注意多词关键词要排在前面（否则「改成」会被「改」抢先匹配成「成XX」）。
    private static readonly Regex CommandPattern = new(
        @"第\s*(?<lesson>[0-9零〇一二两三四五六七八九十百]{1,3})\s*节\s*
          (?<from>[^\s，,。；;、:：]{1,8}?)\s*
          (?:改成|换成|改为|换为|调成|改|换)\s*
          (?<to>[^\s，,。；;、!！?？]{1,8})",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex UndoPattern = new(
        @"第\s*(?<lesson>[0-9零〇一二两三四五六七八九十百]{1,3})\s*节",
        RegexOptions.Compiled);

    // 撤销指令的触发词。注意「#撤销调课」里并不包含「#调课」，所以要单独识别。
    private static readonly string[] UndoTokens = ["#撤销调课", "#取消调课", "撤销调课", "取消调课", "撤销换课"];

    /// <summary>
    /// 这条文本是否像是给我们插件的指令（决定要不要进解析流程）。
    /// </summary>
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

        return UndoTokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// 解析调课 / 撤销指令。
    /// </summary>
    /// <param name="text">Toast 合并后的文本。</param>
    /// <param name="keyword">触发关键词，默认 #调课。</param>
    public TiaokeParseResult Parse(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TiaokeParseResult.Fail("消息内容为空");
        }

        // 撤销优先：避免「#撤销调课」被当成普通调课处理。
        if (UndoTokens.FirstOrDefault(token => text.Contains(token, StringComparison.Ordinal)) is { } undoToken)
        {
            var lessonMatch = UndoPattern.Match(text[(text.IndexOf(undoToken, StringComparison.Ordinal) + undoToken.Length)..]);
            var lesson = lessonMatch.Success ? ParseNumber(lessonMatch.Groups["lesson"].Value) ?? 0 : 0;
            return TiaokeParseResult.Ok(TiaokeCommand.Undo(lesson, text.Trim()));
        }

        var index = text.IndexOf(keyword, StringComparison.Ordinal);
        if (index < 0)
        {
            return TiaokeParseResult.Fail($"未找到关键词 {keyword}");
        }

        var payload = text[(index + keyword.Length)..];
        var match = CommandPattern.Match(payload);
        if (!match.Success)
        {
            return TiaokeParseResult.Fail("格式不符，应为「#调课 第X节 原科目改新科目」");
        }

        var lessonNumber = ParseNumber(match.Groups["lesson"].Value);
        if (lessonNumber is null or <= 0 or > 20)
        {
            return TiaokeParseResult.Fail($"节次「{match.Groups["lesson"].Value}」无法识别");
        }

        var from = match.Groups["from"].Value.Trim();
        var to = match.Groups["to"].Value.Trim();
        if (from.Length == 0 || to.Length == 0)
        {
            return TiaokeParseResult.Fail("科目名称为空");
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return TiaokeParseResult.Fail($"{from} 改 {to} 没有实际变化");
        }

        return TiaokeParseResult.Ok(TiaokeCommand.Apply(lessonNumber.Value, from, to, text.Trim()));
    }

    /// <summary>
    /// 支持阿拉伯数字与中文数字（一、二、两、十、十二、二十等）。
    /// </summary>
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
