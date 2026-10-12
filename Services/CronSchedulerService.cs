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

    private readonly object _pauseLock = new();
    private int _pauseDepth;

    private readonly object _runningLock = new();
    private readonly List<(Task Run, CancellationTokenSource Cts)> _running = new();

    public CronSchedulerService(
        CronTaskStore store,
        IMCServerService mc)
    {
        _store = store;
        _mc = mc;
    }

    /// <summary>
    /// 暂停调度（可重入）：迁移等需要短暂停摆的场景调用，防止插件侧提前执行造成双跑。
    /// 必须与 <see cref="Resume"/> 成对调用。
    /// </summary>
    public void Pause()
    {
        lock (_pauseLock)
        {
            _pauseDepth++;
        }
    }

    /// <summary>恢复调度，与 <see cref="Pause"/> 成对调用。</summary>
    public void Resume()
    {
        lock (_pauseLock)
        {
            if (_pauseDepth > 0)
            {
                _pauseDepth--;
            }
        }
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
        }
        catch (Exception)
        {
            // 忽略停止期异常
        }

        // 等待调度循环真正退出，避免 _loop 在 Dispose 后继续 tick
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 等待超时/异常不影响卸载流程
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;

        // 取消所有进行中的执行任务（重启倒计时、Shell 进程等），防止卸载后仍在后台执行
        List<CancellationTokenSource> running;
        lock (_runningLock)
        {
            running = _running.Select(r => r.Cts).ToList();
        }

        foreach (var cts in running)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // 忽略单任务取消异常
            }
        }
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
        bool paused;
        lock (_pauseLock)
        {
            paused = _pauseDepth > 0;
        }

        if (paused)
        {
            return; // 迁移等操作暂停调度：本期 tick 不执行任何任务
        }

        var nowUtc = DateTime.UtcNow;
        // 加载完整列表（含停用任务）：只对启用任务调度，但写回时不能把停用任务覆盖丢失
        var tasks = _store.LoadTasks();

        var due = new List<(CronTaskDefinition Task, uint InstanceId)>();
        var updates = new List<(string TaskId, uint InstanceId, DateTime LastRunTime)>();

        foreach (var task in tasks)
        {
            if (!task.Enable)
            {
                continue;
            }

            foreach (var binding in task.Bindings)
            {
                if (!ShouldRun(task.Cron, binding.LastRunTime, nowUtc))
                {
                    continue;
                }

                due.Add((task, binding.InstanceId));
                updates.Add((task.Id, binding.InstanceId, nowUtc));
            }
        }

        // 单事务写回到期绑定的 LastRunTime，避免崩溃后重复触发；任务列表整体不会被旧快照覆盖
        if (updates.Count > 0)
        {
            _store.UpdateBindingsLastRunTime(updates);
        }

        foreach (var (task, instanceId) in due)
        {
            // 跟踪执行任务：Stop 时取消，防止卸载后重启倒计时/Shell 仍在后台运行
            var cts = new CancellationTokenSource();
            var execTask = Task.Run(() => ExecuteAsync(task, instanceId, cts.Token));
            lock (_runningLock)
            {
                _running.Add((execTask, cts));
            }

            _ = execTask.ContinueWith(_ =>
            {
                lock (_runningLock)
                {
                    _running.RemoveAll(r => r.Run == execTask);
                }
            }, TaskScheduler.Default);
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
    public async Task<(bool Success, string Message)> ExecuteAsync(
        CronTaskDefinition task,
        uint instanceId,
        CancellationToken cancellationToken = default)
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
            var (success, message) = await DispatchAsync(task, instanceId, cancellationToken);
            MSLX.SDK.MSLX.Events.PublishTaskExecuted(new TaskExecutedEventArgs
            {
                TaskId = task.Id,
                TaskName = task.Name,
                TaskType = task.Type,
                InstanceId = instanceId,
                Success = success,
                ErrorMessage = success ? null : message,
                Timestamp = DateTime.Now
            });
            return (success, message);
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

    private async Task<(bool Success, string Message)> DispatchAsync(
        CronTaskDefinition task,
        uint instanceId,
        CancellationToken cancellationToken)
    {
        switch (task.Type.ToLower())
        {
            case "command":
                var ok = _mc.SendCommand(instanceId, task.Payload);
                return ok
                    ? (true, "命令已发送")
                    : (false, $"命令发送失败 (实例 {instanceId} 可能未运行)");

            case "start":
                var start = _mc.StartServer(instanceId);
                return start.success
                    ? (true, "启动指令已下发")
                    : (false, $"启动失败: {start.message}");

            case "stop":
                var stopped = _mc.StopServer(instanceId);
                return stopped ? (true, "停止指令已下发") : (false, "停止失败");

            case "restart":
                await HandleRestartAsync(instanceId, task.Payload, cancellationToken);
                return (true, "重启完成");

            case "backup":
                var backed = _mc.StartBackupServer(instanceId);
                return backed ? (true, "备份已开始") : (false, "备份启动失败");

            case "shell":
                return await HandleShellAsync(task, instanceId, cancellationToken);

            default:
                throw new Exception($"未知的任务类型: {task.Type}");
        }
    }

    /// <summary>
    /// 复刻宿主 restart 流程：离线下直接启动；在线下广播倒计时 60s →
    /// 30/10/5s 阶段提示 → tellraw → 停止 → 等待退出(≤30s) → 缓冲2s → 启动。
    /// </summary>
    private async Task HandleRestartAsync(uint instanceId, string payload, CancellationToken cancellationToken)
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

            await Task.Delay(1000, cancellationToken);
        }

        _mc.SendCommand(instanceId, "tellraw @a [{\"text\":\"[\",\"color\":\"yellow\"},{\"text\":\"MSLX\",\"color\":\"green\"},{\"text\":\"]\",\"color\":\"yellow\"},{\"text\":\"服务器即将重启...\",\"color\":\"aqua\"}]");
        await Task.Delay(1000, cancellationToken); // 确保发包给客户端

        var stopped = _mc.StopServer(instanceId);
        if (!stopped)
        {
            throw new Exception("停止实例失败");
        }

        var retry = 0;
        while (_mc.IsServerRunning(instanceId) && retry < 30)
        {
            await Task.Delay(1000, cancellationToken);
            retry++;
        }

        await Task.Delay(2000, cancellationToken);

        _mc.StartServer(instanceId);
        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 任务触发的重启已完成: 实例 {instanceId}");
    }

    private static string FormatRestartBroadcast(int seconds)
    {
        return $"tellraw @a [{{\"text\":\"[\",\"color\":\"yellow\"}},{{\"text\":\"MSLX\",\"color\":\"green\"}},{{\"text\":\"]\",\"color\":\"yellow\"}},{{\"text\":\"服务器将在 {seconds} 秒后重启\",\"color\":\"aqua\"}}]";
    }

    /// <summary>
    /// 复刻宿主 shell 分支：cmd/bash，WorkingDirectory=实例 Base，输出转日志。
    /// 非零退出码视为执行失败；卸载取消时杀掉仍在运行的进程。
    /// </summary>
    private async Task<(bool Success, string Message)> HandleShellAsync(
        CronTaskDefinition task,
        uint instanceId,
        CancellationToken cancellationToken)
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

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);

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

            var ok = process.ExitCode == 0;
            return ok
                ? (true, $"已执行于 {serverInfo.Base} (exit {process.ExitCode})")
                : (false, $"已执行于 {serverInfo.Base}，退出码 {process.ExitCode}（非零视为失败）");
        }
        catch (OperationCanceledException)
        {
            // 卸载/停止时取消：杀掉仍在运行的 shell 进程，防止其继续执行
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                // 进程可能已自行退出
            }

            throw;
        }
    }
}