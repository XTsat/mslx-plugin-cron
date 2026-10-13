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
    private readonly IInstanceLifecycleService _lifecycle;
    private readonly IInstanceConsoleService _console;
    private readonly IInstanceBackupService _backup;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly object _pauseLock = new();
    private int _pauseDepth;

    private readonly object _runningLock = new();
    private readonly List<(Task Run, CancellationTokenSource Cts)> _running = new();

    // 停止标记：Stop() 置位后拒绝新登记的执行任务（与 _runningLock 同锁读写），
    // 消除「快照后登记 → 既不取消也不等待」的竞态
    private bool _stopping;

    // 服务器删除后的绑定跟随清理：周期巡检而非每 tick 执行，避免无谓的清单比对与落盘
    private static readonly TimeSpan ServerCleanupInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastServerCleanupUtc = DateTime.MinValue;
    private bool _serverInventoryReady;

    public CronSchedulerService(
        CronTaskStore store,
        IInstanceLifecycleService lifecycle,
        IInstanceConsoleService console,
        IInstanceBackupService backup)
    {
        _store = store;
        _lifecycle = lifecycle;
        _console = console;
        _backup = backup;
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

        lock (_runningLock)
        {
            _stopping = false; // 停止后重启（插件热更新）时清除停止标记，恢复新任务的登记
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        // 先置停止标记：此后登记的执行任务（含手动立即执行）一律拒绝，
        // 消除「快照 _running 之后才登记的任务既不被取消也不被等待」的竞态
        lock (_runningLock)
        {
            _stopping = true;
        }

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

        // 取消并等待执行任务：循环复查 _running 至空（上限 5s），
        // 覆盖取消期间新登记的任务（ProcessAsync 已通过 _stopping 拒绝新登记，此处兜底复查）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Task[] runningTasks;
            CancellationTokenSource[] runningCts;
            lock (_runningLock)
            {
                runningTasks = _running.Select(r => r.Run).ToArray();
                runningCts = _running.Select(r => r.Cts).ToArray();
            }

            if (runningTasks.Length == 0)
            {
                return;
            }

            foreach (var cts in runningCts)
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

            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return; // 已超时：剩余任务交由进程销毁，避免 WaitAll 负超时抛异常
                }

                Task.WaitAll(runningTasks, remaining);
            }
            catch (Exception)
            {
                // 等待超时/任务异常不影响卸载流程
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

        CleanupDeletedServerBindings();

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
            // 登记与 _stopping 检查在同一把锁内：停止中的新任务直接跳过，不再启动
            Task execTask;
            CancellationTokenSource cts;
            lock (_runningLock)
            {
                if (_stopping)
                {
                    continue;
                }

                // 跟踪执行任务：Stop 时取消，防止卸载后重启倒计时/Shell 仍在后台运行
                cts = new CancellationTokenSource();
                execTask = Task.Run(() => ExecuteAsync(task, instanceId, cts.Token));
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
    /// 服务器在宿主侧被删除后，其任务绑定不再有意义，默认跟随自动清理（前端手动移除仅作兜底）。
    /// 周期巡检 + 落盘仅在确有删除时发生；宿主服务器清单未就绪前跳过，避免启动竞态把全部绑定误判为失效清空。
    /// </summary>
    private void CleanupDeletedServerBindings()
    {
        var now = DateTime.UtcNow;
        if (now - _lastServerCleanupUtc < ServerCleanupInterval)
        {
            return;
        }
        _lastServerCleanupUtc = now;

        var servers = MSLX.SDK.MSLX.Config.Servers.GetServerList();
        if (servers == null)
        {
            return;
        }
        if (servers.Count == 0 && !_serverInventoryReady)
        {
            return; // 清单尚未加载（插件启动早于宿主配置加载）：本次跳过，下一周期再试
        }
        _serverInventoryReady = true;

        var removedBindings = _store.PruneDeletedInstances(
            id => MSLX.SDK.MSLX.Config.Servers.GetServer(id) != null);
        if (removedBindings > 0)
        {
            MSLX.SDK.MSLX.Logger.Info(
                $"[mslx-plugin-cron] 检测到已删除的服务器，自动清理 {removedBindings} 个失效绑定（任务本身保留，绑定列表为空）");
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

        // 插件任务发布到宿主全局事件总线：TaskId 是插件自有任务 GUID（宿主任务表不存在该 ID），
        // 当前宿主与其它插件均无 OnTask* 订阅者；若未来有人按宿主 TaskId 关联状态，需知晓此跨插件契约
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

    /// <summary>
    /// 「立即执行」：与定时任务共用 <see cref="ExecuteAsync"/> 执行路径，并登记到 _running，
    /// 使 Stop()/卸载能够取消并等待手动执行的任务退出。
    /// </summary>
    public async Task<(bool Success, string Message)> RunNowAsync(
        CronTaskDefinition task,
        uint instanceId)
    {
        Task<(bool Success, string Message)> execTask;
        CancellationTokenSource cts;
        lock (_runningLock)
        {
            // 停止中直接拒绝：与 _stopping 检查同锁，登记后 Stop() 一定能取消并等待
            if (_stopping)
            {
                return (false, "调度器已停止，无法立即执行");
            }

            cts = new CancellationTokenSource();
            execTask = Task.Run(() => ExecuteAsync(task, instanceId, cts.Token));
            _running.Add((execTask, cts));
        }

        try
        {
            return await execTask;
        }
        finally
        {
            lock (_runningLock)
            {
                _running.RemoveAll(r => r.Run == execTask);
            }

            cts.Dispose();
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
                var ok = _console.SendCommand(instanceId, task.Payload);
                return ok
                    ? (true, "命令已发送")
                    : (false, $"命令发送失败 (实例 {instanceId} 可能未运行)");

            case "start":
                var start = _lifecycle.StartServer(instanceId);
                return start.success
                    ? (true, "启动指令已下发")
                    : (false, $"启动失败: {start.message}");

            case "stop":
                var stopped = _lifecycle.StopServer(instanceId);
                return stopped ? (true, "停止指令已下发") : (false, "停止失败");

            case "restart":
                await HandleRestartAsync(instanceId, task.Payload, cancellationToken);
                return (true, "重启完成");

            case "backup":
                var backed = _backup.StartBackupServer(instanceId);
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
        if (!_lifecycle.IsServerRunning(instanceId))
        {
            var offlineStart = _lifecycle.StartServer(instanceId);
            if (!offlineStart.success)
            {
                throw new Exception($"启动失败: {offlineStart.message}");
            }

            MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 任务重启: 实例 {instanceId} 离线，直接启动");
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload))
        {
            _console.SendCommand(instanceId, $"say [计划任务] {payload}");
        }

        _console.SendCommand(instanceId, FormatRestartBroadcast(60));

        for (var i = 60; i > 0; i--)
        {
            if (!_lifecycle.IsServerRunning(instanceId))
            {
                MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 实例 {instanceId} 在重启倒计时期间已停止，取消重启操作。");
                return;
            }

            if (i == 30 || i == 10 || i == 5)
            {
                _console.SendCommand(instanceId, FormatRestartBroadcast(i));
            }

            await Task.Delay(1000, cancellationToken);
        }

        _console.SendCommand(instanceId, "tellraw @a [{\"text\":\"[\",\"color\":\"yellow\"},{\"text\":\"MSLX\",\"color\":\"green\"},{\"text\":\"]\",\"color\":\"yellow\"},{\"text\":\"服务器即将重启...\",\"color\":\"aqua\"}]");
        await Task.Delay(1000, cancellationToken); // 确保发包给客户端

        var stopped = _lifecycle.StopServer(instanceId);
        if (!stopped)
        {
            throw new Exception("停止实例失败");
        }

        var retry = 0;
        while (_lifecycle.IsServerRunning(instanceId) && retry < 30)
        {
            await Task.Delay(1000, cancellationToken);
            retry++;
        }

        await Task.Delay(2000, cancellationToken);

        var start = _lifecycle.StartServer(instanceId);
        if (!start.success)
        {
            throw new Exception($"启动失败: {start.message}");
        }

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
        if (!task.RunWhenOffline && !_lifecycle.IsServerRunning(instanceId))
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