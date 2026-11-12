namespace QqClassSync.Models;

/// <summary>
/// 一条调课改动记录：记录「某天的第几节课，从哪个科目改成了哪个科目」。
/// 用于单节课精准撤销——只看临时层的话分不清「本来就是这个科目」和「被我改过的」。
/// </summary>
/// <param name="Date">日期（只保留日期部分）。</param>
/// <param name="Lesson">第几节课（1 起）。</param>
/// <param name="OriginalSubjectId">改动前的科目 GUID。</param>
/// <param name="OriginalSubjectName">改动前的科目名（仅用于提示，不用来恢复）。</param>
/// <param name="NewSubjectId">改动后的科目 GUID。</param>
/// <param name="NewSubjectName">改动后的科目名（仅用于提示）。</param>
/// <param name="CreatedAt">记录时间。</param>
public sealed record LessonPatchRecord(
    DateTime Date,
    int Lesson,
    Guid OriginalSubjectId,
    string OriginalSubjectName,
    Guid NewSubjectId,
    string NewSubjectName,
    DateTime CreatedAt)
{
    public override string ToString() =>
        $"{Date:yyyy-MM-dd} 第{Lesson}节 {OriginalSubjectName}→{NewSubjectName}";
}
