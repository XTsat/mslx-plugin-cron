using MSLX.Plugin.Cron.Models;
using MSLX.SDK.Models.Instance;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 宿主旧任务 → 多服任务 迁移服务。
/// 归并键：(Type 小写, Cron 去空白, Payload, RunWhenOffline, 启停状态)。
/// 迁移流程（迁移期间暂停插件调度，避免双跑）：
/// 归并写入插件存储（新建任务保留宿主原启停状态）→ 宿主任务逐个禁用，
/// 检查 UpdateTask 返回值，仅在成功后登记迁移记录（失败/中断可安全重试）→ 写迁移记录。
/// 卸载时按迁移记录还原宿主任务。
/// </summary>
public class CronMigrationService
{
    private readonly CronTaskStore _store;
    private readonly CronSchedulerService _scheduler;

    public CronMigrationService(CronTaskStore store, CronSchedulerService scheduler)
    {
        _store = store;
        _scheduler = scheduler;
    }

    /// <summary>
    /// 导入宿主任务（幂等增量，可反复调用）：
    /// - 迁移记录中已导入过的宿主任务 → 跳过；
    /// - 新任务按 (Type, Cron, Payload, RunWhenOffline, 启停状态) 归并：与已有插件任务同键
    ///   且启停状态一致则把实例并入 Bindings，否则新建插件任务（保留宿主原启停状态）；
    /// - 新导入的宿主任务被禁用（保留原状态），卸载时按迁移记录还原。
    /// 迁移期间暂停插件调度；每条宿主任务仅在禁用成功后登记迁移记录，失败/中断可安全重试。
    /// </summary>
    public MigrationResult Migrate()
    {
        var record = _store.LoadMigration();
        var hostTasks = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().ToList();
        if (hostTasks.Count == 0)
        {
            return new MigrationResult { Skipped = true, Reason = "宿主没有定时任务可导入" };
        }

        var importedIds = record?.HostTasks.Select(r => r.TaskId).ToHashSet() ?? new HashSet<string>();
        var fresh = hostTasks.Where(t => !importedIds.Contains(t.ID)).ToList();
        if (fresh.Count == 0)
        {
            return new MigrationResult { Skipped = true, Reason = "没有新的宿主任务需要导入（已全部接管）" };
        }

        // 迁移期间暂停插件调度：避免插件任务先写入并执行、宿主任务尚未禁用造成双跑
        _scheduler.Pause();
        try
        {
            var existing = _store.LoadTasks();
            var byKey = new Dictionary<string, CronTaskDefinition>();
            foreach (var def in existing)
            {
                var key = MergeKeyOf(def.Type, def.Cron, def.Payload, def.RunWhenOffline, def.Enable);
                if (!byKey.ContainsKey(key))
                {
                    byKey[key] = def;
                }
            }

            var tasks = existing.ToList();
            var items = new List<MigrationItem>();
            foreach (var t in fresh)
            {
                var key = MergeKeyOf(t.Type, t.Cron, t.Payload, t.RunWhenOffline, t.Enable);
                if (byKey.TryGetValue(key, out var def))
                {
                    // 同规则且启停状态一致的任务：仅把新实例并入绑定，避免重复任务
                    if (def.Bindings.All(b => b.InstanceId != t.InstanceId))
                    {
                        def.Bindings.Add(new TaskBinding { InstanceId = t.InstanceId, LastRunTime = t.LastRunTime });
                        items.Add(new MigrationItem { Name = def.Name, InstanceId = t.InstanceId, Merged = true });
                    }
                }
                else
                {
                    // 新建任务保留宿主原启停状态：停用的宿主任务导入后仍为停用
                    def = new CronTaskDefinition
                    {
                        Name = t.Name,
                        Cron = t.Cron,
                        Type = t.Type,
                        Payload = t.Payload,
                        RunWhenOffline = t.RunWhenOffline,
                        Enable = t.Enable,
                        Bindings = new List<TaskBinding>
                        {
                            new TaskBinding { InstanceId = t.InstanceId, LastRunTime = t.LastRunTime }
                        }
                    };
                    tasks.Add(def);
                    byKey[key] = def;
                    items.Add(new MigrationItem { Name = def.Name, InstanceId = t.InstanceId, Merged = false });
                }
            }

            _store.SaveTasks(tasks);

            record ??= new MigrationRecord { MigratedAt = DateTime.Now };
            var failedCount = 0;
            foreach (var t in fresh)
            {
                var originalEnable = t.Enable; // 先取原状态再禁用宿主任务
                t.Enable = false;
                if (!MSLX.SDK.MSLX.Config.Tasks.UpdateTask(t))
                {
                    failedCount++;
                    MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 禁用宿主任务 {t.ID} 失败，未登记该任务；下次迁移将重试");
                    continue;
                }

                // 仅登记成功禁用的宿主任务：记录始终等于「已接管」集合，中断后不会半完成态重复导入
                record.HostTasks.Add(new HostTaskRecord
                {
                    TaskId = t.ID,
                    InstanceId = t.InstanceId,
                    OriginalEnable = originalEnable
                });
                _store.SaveMigration(record);
            }

            return new MigrationResult
            {
                MigratedCount = fresh.Count - failedCount,
                FailedCount = failedCount,
                TaskCount = tasks.Count,
                Items = items
            };
        }
        finally
        {
            _scheduler.Resume();
        }
    }

    private static string KeyOf(string type, string cron, string? payload, bool runWhenOffline)
    {
        return $"{type.ToLower().Trim()}|{cron.Trim()}|{payload ?? ""}|{(runWhenOffline ? 1 : 0)}";
    }

    private static string MergeKeyOf(string type, string cron, string? payload, bool runWhenOffline, bool enable)
    {
        return $"{KeyOf(type, cron, payload, runWhenOffline)}|{(enable ? 1 : 0)}";
    }

    /// <summary>
    /// 导入预览（干跑）：与 Migrate 相同的判重/归并逻辑，但不写任何数据，
    /// 返回「点击导入后会发生什么」的清单，供前端先展示、用户确认后再真正导入。
    /// </summary>
    public MigrationPreview Preview()
    {
        var record = _store.LoadMigration();
        var hostTasks = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().ToList();
        if (hostTasks.Count == 0)
        {
            return new MigrationPreview { Skipped = true, Reason = "宿主没有定时任务可导入" };
        }

        var importedIds = record?.HostTasks.Select(r => r.TaskId).ToHashSet() ?? new HashSet<string>();
        var fresh = hostTasks.Where(t => !importedIds.Contains(t.ID)).ToList();
        if (fresh.Count == 0)
        {
            return new MigrationPreview { Skipped = true, Reason = "没有新的宿主任务需要导入（已全部接管）" };
        }

        var byKey = new Dictionary<string, CronTaskDefinition>();
        foreach (var def in _store.LoadTasks())
        {
            var key = MergeKeyOf(def.Type, def.Cron, def.Payload, def.RunWhenOffline, def.Enable);
            if (!byKey.ContainsKey(key))
            {
                byKey[key] = def;
            }
        }

        var items = new List<MigrationPreviewItem>();
        foreach (var t in fresh)
        {
            var key = MergeKeyOf(t.Type, t.Cron, t.Payload, t.RunWhenOffline, t.Enable);
            if (byKey.TryGetValue(key, out var def))
            {
                items.Add(new MigrationPreviewItem
                {
                    HostTaskId = t.ID,
                    Name = t.Name,
                    Cron = t.Cron,
                    Type = t.Type,
                    Payload = t.Payload,
                    RunWhenOffline = t.RunWhenOffline,
                    InstanceId = t.InstanceId,
                    Merged = true,
                    TargetName = def.Name
                });
            }
            else
            {
                items.Add(new MigrationPreviewItem
                {
                    HostTaskId = t.ID,
                    Name = t.Name,
                    Cron = t.Cron,
                    Type = t.Type,
                    Payload = t.Payload,
                    RunWhenOffline = t.RunWhenOffline,
                    InstanceId = t.InstanceId,
                    Merged = false
                });
            }
        }

        return new MigrationPreview { Skipped = false, Count = items.Count, Items = items };
    }

    /// <summary>
    /// 还原：按迁移记录把宿主任务恢复为原有启用状态，并清空迁移记录。
    /// </summary>
    public void Restore()
    {
        var record = _store.LoadMigration();
        if (record == null)
        {
            return;
        }

        foreach (var r in record.HostTasks)
        {
            try
            {
                var task = MSLX.SDK.MSLX.Config.Tasks.GetTask(r.TaskId);
                if (task != null)
                {
                    task.Enable = r.OriginalEnable;
                    MSLX.SDK.MSLX.Config.Tasks.UpdateTask(task);
                }
            }
            catch (Exception ex)
            {
                MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 还原宿主任务 {r.TaskId} 失败", ex);
            }
        }

        // 保留迁移记录：即使宿主进程重启触发插件卸载，已导入判重状态也不丢失，
        // 避免已导入任务再次出现在「导入宿主任务」预览中。
        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 已还原 {record.HostTasks.Count} 条宿主任务（保留迁移记录）");
    }

    /// <summary>
    /// 查询迁移状态：是否已迁移 + 当前多服任务数。
    /// </summary>
    public (bool Migrated, string? Reason, int TaskCount, DateTime? MigratedAt) GetStatus()
    {
        var record = _store.LoadMigration();
        if (record != null)
        {
            return (true, null, _store.LoadTasks().Count, record.MigratedAt);
        }

        var tasks = _store.LoadTasks();
        if (tasks.Count > 0)
        {
            return (true, "插件任务已手动创建", tasks.Count, null);
        }

        var hostEnabled = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().Count(t => t.Enable);
        return (false, hostEnabled > 0 ? $"宿主有 {hostEnabled} 条启用任务待迁移" : "宿主没有可迁移的启用任务", 0, null);
    }
}