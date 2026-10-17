using Microsoft.AspNetCore.Mvc.ApplicationParts;
using MSLX.SDK;
using MSLX.Plugin.Cron.Services;

[assembly: ApplicationPart("MSLX.Plugin.Cron")]

namespace MSLX.Plugin.Cron;

/// <summary>
/// 多服定时任务插件：任务绑定多服务器。
/// 创建任务并选择多台服务器启用；支持从宿主「服务器绑定任务」迁移（需用户确认，
/// 迁移后宿主旧任务被禁用、由插件自调度器按每台服务器的 LastRunTime 独立调度）；
/// 卸载时不自动还原宿主任务。
/// </summary>
public class PluginEntry : IPlugin
{
    public static PluginEntry Instance { get; private set; } = null!;

    public string Id => "mslx-plugin-cron";
    public string Name => "定时任务绑定";
    public string Description => "任务绑定多服务器：同一任务可启用至多台服务器统一执行；支持在服务器设置页以服务器视角添加/取消任务绑定。";
    public string Version => "1.1.0";
    public string MinSDKVersion => "1.7.0";
    public string Developer => "XTsat";
    public string AuthorUrl => "https://github.com/XTsat";
    public string PluginUrl => "https://github.com/XTsat/mslx-plugin-cron";

    private IServiceProvider _services = null!;
    private CronSchedulerService? _scheduler;
    private CronMigrationService? _migration;

    public void OnPluginInitialize(IServiceProvider serviceProvider)
    {
        Instance = this;
        _services = serviceProvider;
    }

    public void OnRegisterServices(IServiceCollection services)
    {
        // 三个单例：存储、迁移、调度器；调度器由 OnLoad 启动、OnUnload 停止
        services.AddSingleton<CronTaskStore>();
        services.AddSingleton<CronMigrationService>();
        services.AddSingleton<CronSchedulerService>();
    }

    public void OnLoad()
    {
        try
        {
            SDK.MSLX.Logger.Info("[mslx-plugin-cron] 插件开始加载...");

            _migration = _services.GetService<CronMigrationService>();
            _scheduler = _services.GetService<CronSchedulerService>();

            // 不自动迁移：迁移由用户在页面上确认后触发（可能导致宿主旧任务失效），
            // 这里只记录当前迁移状态供页面提示。
            if (_migration != null)
            {
                var status = _migration.GetStatus();
                if (status.Migrated)
                {
                    SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 已迁移 {status.TaskCount} 个多服任务定义（{status.MigratedAt?.ToString("O")}）");
                }
                else
                {
                    SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 尚未迁移，等待用户在迁移面板确认后导入（原因: {status.Reason}）");
                }
            }

            _scheduler?.Start();
            SDK.MSLX.Logger.Info("[mslx-plugin-cron] 多服定时任务调度器已启动 (1s tick)");
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Error("[mslx-plugin-cron] 插件加载失败", ex);
        }
    }

    public void OnUnload()
    {
        try
        {
            _scheduler?.Stop();
            // 不再还原宿主任务：宿主进程每次停止（重启容器/更新 daemon）都会触发 OnUnload，
            // 若自动还原会把已接管的宿主任务重新启用，导致宿主调度器与插件调度器双重执行。
            // 被接管的宿主任务保持禁用，由插件持续接管；如需还原请使用「宿主任务迁移」面板左下角「还原任务」按钮（仅管理员）。
            SDK.MSLX.Logger.Info("[mslx-plugin-cron] 插件已卸载，调度已停止；宿主任务保持插件接管状态（不自动还原）");
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Error("[mslx-plugin-cron] 插件卸载异常", ex);
        }
    }

    public void OnRegisterEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Controller (ApplicationPart) 已覆盖 REST API
    }
}
