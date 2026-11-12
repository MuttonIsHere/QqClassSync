using System.Text.Encodings.Web;
using System.Text.Json;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 调课改动日志（持久化到插件配置目录的 TempPatches.json）。
///
/// 单节课撤销需要知道「这一节原本是什么」，而临时层课表本身看不出哪些节被改过，
/// 所以每次改课都记一条。撤销时取该节最近一条记录，把原科目写回去，其余记录保留。
/// 日志按日期组织，只保留最近 7 天，避免无限增长。
/// </summary>
public sealed class LessonUndoJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const int MaxKeepDays = 7;

    private readonly List<LessonPatchRecord> _records;

    private LessonUndoJournal(string filePath, List<LessonPatchRecord> records)
    {
        FilePath = filePath;
        _records = records;
    }

    public string FilePath { get; }

    public static LessonUndoJournal Load(string filePath)
    {
        var records = new List<LessonPatchRecord>();
        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(filePath), JsonOptions);
                if (loaded?.Records is not null)
                {
                    records = loaded.Records;
                }
            }
        }
        catch
        {
            // 日志坏了就从空开始，不要让插件加载失败。
            records = new List<LessonPatchRecord>();
        }

        var journal = new LessonUndoJournal(filePath, records);
        journal.Prune(DateTime.Today);
        return journal;
    }

    /// <summary>追加一条改动记录。</summary>
    public void Add(LessonPatchRecord record) => _records.Add(record);

    /// <summary>某天某节的全部改动记录（按时间先后）。</summary>
    public IReadOnlyList<LessonPatchRecord> ForLesson(DateTime date, int lesson) =>
        _records.Where(r => r.Date.Date == date.Date && r.Lesson == lesson).ToList();

    /// <summary>某天的全部改动记录。</summary>
    public IReadOnlyList<LessonPatchRecord> ForDate(DateTime date) =>
        _records.Where(r => r.Date.Date == date.Date).ToList();

    /// <summary>
    /// 取出某天某节最近一条记录并移除（栈式撤销）。
    /// 没有记录时返回 null。
    /// </summary>
    public LessonPatchRecord? TakeLast(DateTime date, int lesson)
    {
        var index = -1;
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (_records[i].Date.Date == date.Date && _records[i].Lesson == lesson)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        var record = _records[index];
        _records.RemoveAt(index);
        return record;
    }

    /// <summary>清空某天的记录（整天撤销后调用）。</summary>
    public void Clear(DateTime date) => _records.RemoveAll(r => r.Date.Date == date.Date);

    /// <summary>清空全部记录。</summary>
    public void ClearAll() => _records.Clear();

    public void Prune(DateTime today)
    {
        var cutoff = today.Date.AddDays(-MaxKeepDays);
        _records.RemoveAll(r => r.Date.Date < cutoff);
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new JournalFile { Records = _records };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响内存中的行为；下次改课再写。
        }
    }

    private sealed class JournalFile
    {
        public List<LessonPatchRecord>? Records { get; set; }
    }
}
