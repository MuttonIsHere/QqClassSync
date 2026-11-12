using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QqClassSync.Services;
using QqClassSync.Views;

namespace QqClassSync;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 配置文件放在 ClassIsland 的插件配置目录，用户可直接编辑 Settings.json。
        var settings = TiaokeSettings.Load(PluginConfigFolder);

        // 调课改动日志：用于单节课精准撤销，随插件配置一起持久化。
        var journal = LessonUndoJournal.Load(Path.Combine(PluginConfigFolder, "TempPatches.json"));

        services.AddSingleton(settings);
        services.AddSingleton(journal);
        services.AddSingleton<TiaokeParser>();
        services.AddSingleton<AiTiaokeParser>();
        services.AddSingleton<QqToastScanner>();
        services.AddSingleton<TempClassPlanPatcher>();
        services.AddNotificationProvider<TiaokeNotificationProvider>();
        services.AddSettingsPage<TiaokeSettingsPage>();
    }
}
