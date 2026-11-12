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

        services.AddSingleton(settings);
        services.AddSingleton<TiaokeParser>();
        services.AddSingleton<QqToastScanner>();
        services.AddSingleton<TempClassPlanPatcher>();
        services.AddNotificationProvider<TiaokeNotificationProvider>();
        services.AddSettingsPage<TiaokeSettingsPage>();
    }
}
