## 背景

v1.0 的撤销只能**整天清空**（`ClearTempClassPlan()`），一节课撤错就把当天所有调课一起清掉。

这个 PR 实现**单节课精准撤销**：老师发 `#撤销调课 第3节`，只把第 3 节恢复成原科目，其他调过的节次保留。

## 实现思路

临时层课表本身看不出「哪些节被改过」，所以插件自己维护一份**调课改动日志**：

- 每次调课记录一条 `{日期, 节次, 原科目 GUID/名称, 新科目 GUID/名称, 时间}`，持久化到
  `<插件配置目录>/TempPatches.json`（保留最近 7 天，避免无限增长）。
- 撤销某节时取该节**最近一条**记录（栈式：同一节改两次会先撤最近一次），把 `SubjectId` 写回原值，`IsChangedClass = false`，`SaveProfile()`。
- 其他节次的记录与课表改动**原样保留**。
- 如果撤销后所有节次都和源课表一致，顺手调用 `ClearTempClassPlan()`，避免课表名一直挂着「（临时层）」。
- 日志缺失时（插件重装 / 日志被清）退回到「临时层的源课表」（`OverlaySourceId`）读取原科目，仍能撤销。

## 改动内容

| 文件 | 说明 |
|---|---|
| `Models/LessonPatchRecord.cs` | 改动记录的数据模型 |
| `Services/LessonUndoJournal.cs` | 日志的加载/保存/栈式取出/按天清理 |
| `Services/TempClassPlanPatcher.cs` | `Apply()` 记录改动；新增 `UndoLesson(lesson)`；整天 `Undo()` 同时清日志 |
| `Services/TiaokeNotificationProvider.cs` | `#撤销调课 第N节` 走单节撤销，`#撤销调课` 走整天撤销 |
| `Views/TiaokeSettingsPage.axaml(.cs)` | 新增「撤销单节课」行（节次输入框 + 按钮） |
| `tests/ParserTests/JournalTests.cs` | 日志逻辑单元测试 |
| `README.md` | 新功能说明与限制 |

## 验证

- `dotnet run --project tests/ParserTests` → **21/21 通过**（含新增的 8 条日志用例：持久化、栈式撤销、按天清理、7 天过期）
- `dotnet build -c Release` → 0 warning / 0 error，`.cipx` 正常产出
- 本机部署 v1.1.0 到 ClassIsland 2.1.0.1：插件正常启动、权限 `Allowed`、设置页打开无绑定错误、新按钮正常渲染（截图见下）

![设置页](https://raw.githubusercontent.com/MuttonIsHere/QqClassSync/feature/per-lesson-undo/docs/images/settings-page-actions.png)

## 待真机验证（下周教室实测）

- 真实 QQ 消息 `#调课 第3节 语文改数学` → `#调课 第5节 英语改物理` → `#撤销调课 第3节`，确认只回滚第 3 节、第 5 节保留
- 撤销到「全部恢复」时确认临时层被自动清除

## 说明

「模糊指令识别（自然语言 → 大模型 → JSON）」没有放进这个 PR：它需要 DeepSeek API Key、失败降级策略和隐私开关（消息内容会离开本机），适合单独立项。
