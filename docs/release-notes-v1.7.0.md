# v1.7.0 — 内置服务商预设 + 自定义大模型接入

这次把 AI 配置从「手填 DeepSeek」改成「内置通讯录 + 自定义」：普通用户下拉一键切换，折腾党仍然可以填任意 OpenAI 兼容地址。

## v1.7.0：服务商预设下拉

设置页 AI 区域新增「服务商预设」，内置：

| 预设 | Base URL | 推荐模型 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4` |
| Kimi (Moonshot) | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| 通义千问 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| 豆包（火山方舟） | `https://ark.cn-beijing.volces.com/api/v3` | 留空，保留接入点 ID（`ep-…`） |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| 硅基流动 SiliconFlow | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-7B-Instruct` |
| OpenRouter | `https://openrouter.ai/api/v1` | `openai/gpt-4o-mini` |
| 自定义 (Custom) | 清空 | 清空 |

- 选中预设自动填 Base URL 与推荐模型
- 当前已有手改内容且与预设不一致 → 弹窗确认「覆盖 / 保留当前」；保留则下拉退回，输入不动
- 选「自定义」清空两项，之后手动输入不会被回填
- 模型输入框始终可编辑；豆包预设只填地址，不覆盖接入点 ID
- 页面加载按 Base URL 识别预设；识别不到显示「自定义」，但不清空输入

## v1.6.0：取消强制兜底

- **Base URL 留空就保持为空**：不再自动补 `https://api.deepseek.com/v1`
- **新增模型名称 (Model)**：不再写死 `deepseek-chat`，可填 `glm-4`、豆包接入点 ID 等
- **四重配置校验**：开关 + Key + 有效 http(s) 地址 + 模型，缺任一项不发请求
- 未配置时日志：`[AI] 跳过：接口未配置`，静默回退正则
- Base URL 直接粘完整 `/chat/completions` 也不会重复拼接
- 请求失败按 401/403、404、400 分类记日志，不打印响应体
- 旧配置迁移：文件缺 `AiModel` 且地址是 DeepSeek → 自动补 `deepseek-chat`；其它厂商不猜

## 验证

- `ParserTests`：**182 项断言全部通过**
- 插件构建：**0 warning / 0 error**
- `.cipx` 内 DLL 与构建产物 SHA256 一致

## 升级

下载 `QqClassSync.cipx` 覆盖安装，重启 ClassIsland 一次。配置与调课日志（`TempPatches.json`）保持不变。
