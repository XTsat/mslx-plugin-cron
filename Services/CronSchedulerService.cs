using System.Diagnostics;
using System.Runtime.InteropServices;
using Cronos;
using MSLX.Plugin.Cron.Models;
using MSLX.SDK.Events;
using MSLX.SDK.IServices;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 插件自调度器：复刻宿主 TaskSchedulerService 的调度语义
/// （1s tick、UTC 基准、Cronos IncludeSeconds、GetNextOccurrence + TimeZoneInfo.Local），
/// 但按「服务器绑定」独立维护每台服务器的 LastRunTime，实现一个任务多服务器各自调度。
/// </summary>
public class CronSchedulerService : IDisposable
{
    private readonly CronTaskStore _store;
    private readonly IMCServerService _mc;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public CronSchedulerService(
        CronTaskStore store,
        IMCServerService mc)
    {
        _store = store;
        _mc = mc;
    }

    /// <summary>调度器是否运行中</summary>
    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception)
        {
            // 忽略停止期异常
        }

        _cts = null;
        _loop = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await ProcessAsync();
                }
                catch (Exception ex)
                {
                    MSLX.SDK.MSLX.Logger.Error("[mslx-plugin-cron] 调度器循环发生异常", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }

        MSLX.SDK.MSLX.Logger.Info("[mslx-plugin-cron] 调度器已停止");
    }

    private async Task ProcessAsync()
    {
        var nowUtc = DateTime.UtcNow;
        var tasks = _store.LoadTasks().Where(t => t.Enable).ToList();

        var due = new List<(CronTaskDefinition Task, uint InstanceId)>();
        var dirty = false;

        foreach (var task in tasks)
        {
            foreach (var binding in task.Bindings)
            {
                if (!ShouldRun(task.Cron, binding.LastRunTime, nowUtc))
                {
                    continue;
                }

                // 先更新 LastRunTime 并落盘，避免崩溃后重复触发
                binding.LastRunTime = nowUtc;
                due.Add((task, binding.InstanceId));
                dirty = true;
            }
        }

        if (dirty)
        {
            _store.SaveTasks(tasks);
        }

        foreach (var (task, instanceId) in due)
        {
            _ = Task.Run(() => ExecuteAsync(task, instanceId));
        }
    }

    /// <summary>
    /// 复刻宿主 ShouldRun：Cronos 解析 + GetNextOccurrence(TimeZoneInfo.Local)。
    /// </summary>
    private static bool ShouldRun(string cron, DateTime? lastRunTime, DateTime nowUtc)
    {
        try
        {
            var expression = CronExpression.Parse(cron, CronFormat.IncludeSeconds);
            var fromTimeUtc = lastRunTime.HasValue
                ? lastRunTime.Value.ToUniversalTime()
                : nowUtc.AddMinutes(-1);

            var nextRunUtc = expression.GetNextOccurrence(fromTimeUtc, TimeZoneInfo.Local);
            if (nextRunUtc.HasValue)
            {
                if (nextRunUtc.Value <= nowUtc && nextRunUtc.Value > fromTimeUtc)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Warn($"[mslx-plugin-cron] 任务 [{cron}] Cron 表达式解析失败: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 执行一次任务（对单台服务器），返回结果。调度器与「立即执行」共用。
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteAsync(CronTaskDefinition task, uint instanceId)
    {
        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 执行定时任务 [{task.Name}] ({task.Type}) → 实例 {instanceId}");

        var executingArgs = new TaskExecutingEventArgs
        {
            TaskId = task.Id,
            TaskName = task.Name,
            TaskType = task.Type,
            InstanceId = instanceId,
            Timestamp = DateTime.Now
        };
        MSLX.SDK.MSLX.Events.PublishTaskExecuting(executingArgs);
        if (executingArgs.Cancel)
        {
            MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 任务 [{task.Name}] 已被插件拦截取消执行。");
            return (false, "已被插件拦截取消");
        }

        try
        {
            var message = await DispatchAsync(task, instanceId);
            MSLX.SDK.MSLX.Events.PublishTaskExecuted(new TaskExecutedEventArgs
            {
                TaskId = task.Id,
                TaskName = task.Name,
                TaskType = task.Type,
                InstanceId = instanceId,
                Success = true,
                Timestamp = DateTime.Now
            });
            return (true, message);
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 任务 [{task.Name}] (实例 {instanceId}) 执行失败: {ex.Message}", ex);
            MSLX.SDK.MSLX.Events.PublishTaskExecuted(new TaskExecutedEventArgs
            {
                TaskId = task.Id,
                TaskName = task.Name,
                TaskType = task.Type,
                InstanceId = instanceId,
                Success = false,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.Now
            });
            return (false, ex.Message);
        }
    }

    private async Task<string> DispatchAsync(CronTaskDefinition task, uint instanceId)
    {
        switch (task.Type.ToLower())
        {
            case "command":
                var ok = _mc.SendCommand(instanceId, task.Payload);
                if (!ok)
                {
                    return $"命令发送失败 (实例 {instanceId} 可能未运行)";
                }

                return "命令已发送";

            case "start":
                var start = _mc.StartServer(instanceId);
                return start.success ? "启动指令已下发" : $"启动失败: {start.message}";

            case "stop":
                var stopped = _mc.StopServer(instanceId);
                return stopped ? "停止指令已下发" : "停止失败";

            case "restart":
                await HandleRestartAsync(instanceId, task.Payload);
                return "重启完成";

            case "backup":
                var backed = _mc.StartBackupServer(instanceId);
                return backed ? "备份已开始" : "备份启动失败";

            case "shell":
                return await HandleShellAsync(task, instanceId);

            default:
                throw new Exception($"未知的任务类型: {task.Type}");
        }
    }

    /// <summary>
    /// 复刻宿主 restart 流程：离线下直接启动；在线下广播倒计时 60s →
    /// 30/10/5s 阶段提示 → tellraw → 停止 → 等待退出(≤30s) → 缓冲2s → 启动。
    /// </summary>
    private async Task HandleRestartAsync(uint instanceId, string payload)
    {
        if (!_mc.IsServerRunning(instanceId))
        {
            _mc.StartServer(instanceId);
            MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 任务重启: 实例 {instanceId} 离线，直接启动");
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload))
        {
            _mc.SendCommand(instanceId, $"say [计划任务] {payload}");
        }

        _mc.SendCommand(instanceId, FormatRestartBroadcast(60));

        for (var i = 60; i > 0; i--)
        {
            if (!_mc.IsServerRunning(instanceId))
            {
                MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 实例 {instanceId} 在重启倒计时期间已停止，取消重启操作。");
                return;
            }

            if (i == 30 || i == 10 || i == 5)
            {
                _mc.SendCommand(instanceId, FormatRestartBroadcast(i));
            }

            await Task.Delay(1000);
        }

        _mc.SendCommand(instanceId, "tellraw @a [{\"text\":\"[\",\"color\":\"yellow\"},{\"text\":\"MSLX\",\"color\":\"green\"},{\"text\":\"]\",\"color\":\"yellow\"},{\"text\":\"服务器即将重启...\",\"color\":\"aqua\"}]");
        await Task.Delay(1000); // 确保发包给客户端

        var stopped = _mc.StopServer(instanceId);
        if (!stopped)
        {
            throw new Exception("停止实例失败");
        }

        var retry = 0;
        while (_mc.IsServerRunning(instanceId) && retry < 30)
        {
            await Task.Delay(1000);
            retry++;
        }

        await Task.Delay(2000);

        _mc.StartServer(instanceId);
        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 任务触发的重启已完成: 实例 {instanceId}");
    }

    private static string FormatRestartBroadcast(int seconds)
    {
        return $"tellraw @a [{{\"text\":\"[\",\"color\":\"yellow\"}},{{\"text\":\"MSLX\",\"color\":\"green\"}},{{\"text\":\"]\",\"color\":\"yellow\"}},{{\"text\":\"服务器将在 {seconds} 秒后重启\",\"color\":\"aqua\"}}]";
    }

    /// <summary>
    /// 复刻宿主 shell 分支：cmd/bash，WorkingDirectory=实例 Base，输出转日志。
    /// </summary>
    private async Task<string> HandleShellAsync(CronTaskDefinition task, uint instanceId)
    {
        if (!task.RunWhenOffline && !_mc.IsServerRunning(instanceId))
        {
            throw new Exception($"实例 {instanceId} 处于离线状态，且配置为未运行时不执行");
        }

        var serverInfo = MSLX.SDK.MSLX.Config.Servers.GetServer(instanceId);
        if (serverInfo == null || string.IsNullOrWhiteSpace(serverInfo.Base))
        {
            throw new Exception($"实例 ID {instanceId} 不存在或工作路径为空");
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c \"{task.Payload}\"" : $"-c \"{task.Payload.Replace("\"", "\\\"")}\"",
            WorkingDirectory = serverInfo.Base,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception("进程启动失败");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;
        if (!string.IsNullOrWhiteSpace(output))
        {
            MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] shell 输出 ({instanceId}): {output}");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            MSLX.SDK.MSLX.Logger.Warn($"[mslx-plugin-cron] shell 错误 ({instanceId}): {error}");
        }

        return $"已执行于 {serverInfo.Base} (exit {process.ExitCode})";
    }
}