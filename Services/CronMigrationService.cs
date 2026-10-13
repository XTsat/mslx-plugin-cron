using MSLX.Plugin.Cron.Models;
using MSLX.SDK.Models.Instance;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 宿主旧任务 → 多服任务 迁移服务。
/// 归并键：(Type 小写, Cron 去空白, Payload, RunWhenOffline, 启停状态)。
/// 接管流程（迁移期间暂停插件调度，避免双跑）：
/// 先持久化「原启停状态 + 待接管进度」→ 宿主任务逐个禁用（成功才标记 done）→
/// 仅对 done 的宿主任务做插件归并（事务内读改写）→ 归并落盘后标记 PluginTasksMerged。
/// 任何中间态失败都不会让宿主与插件同时执行；中断后重试按迁移记录恢复。
/// 卸载时按迁移记录还原宿主任务。
/// </summary>
public class CronMigrationService
{
    private readonly CronTaskStore _store;
    private readonly CronSchedulerService _scheduler;
    private readonly object _migrateLock = new();

    public CronMigrationService(CronTaskStore store, CronSchedulerService scheduler)
    {
        _store = store;
        _scheduler = scheduler;
    }

    /// <summary>
    /// 导入宿主任务（幂等增量，可反复调用）：
    /// - 迁移记录中已接管(done)的宿主任务 → 跳过；pending 的按宿主任务 ID 恢复重试；
    /// - 新任务按 (Type, Cron, Payload, RunWhenOffline, 原启停状态) 归并：与已有插件任务同键
    ///   且启停状态一致则把实例并入 Bindings，否则新建插件任务（保留宿主原启停状态）；
    /// - 接管顺序：先持久化原状态与进度，再禁用宿主任务，最后归并插件任务——
    ///   禁用失败的任务保持 pending、不归并，不会出现宿主与插件同时执行；
    ///   记录写入失败时因原状态已提前落盘，重试按 ID 恢复，不会重复导入或丢失原启用状态。
    /// 迁移期间暂停插件调度；迁移请求串行化执行。
    /// </summary>
    public MigrationResult Migrate()
    {
        // 串行化迁移请求：并发点击不会交错处理同一批宿主任务
        lock (_migrateLock)
        {
            var record = _store.LoadMigration() ?? new MigrationRecord { MigratedAt = DateTime.Now };
            var hostTasks = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().ToList();
            if (hostTasks.Count == 0)
            {
                return new MigrationResult { Skipped = true, Reason = "宿主没有定时任务可导入" };
            }

            var doneIds = record.HostTasks
                .Where(r => r.Status == HostTaskRecord.StatusDone)
                .Select(r => r.TaskId).ToHashSet();
            var pendingBefore = record.HostTasks
                .Where(r => r.Status == HostTaskRecord.StatusPending).Count();
            // 全新任务 = 迁移记录中完全不存在的宿主任务；pending 的按 ID 恢复，不再当作新任务重复导入
            var fresh = hostTasks.Where(t =>
                !doneIds.Contains(t.ID) &&
                record.HostTasks.All(r => r.TaskId != t.ID)).ToList();
            if (fresh.Count == 0 && pendingBefore == 0 && record.PluginTasksMerged)
            {
                return new MigrationResult { Skipped = true, Reason = "没有新的宿主任务需要导入（已全部接管）" };
            }

            // 迁移期间暂停插件调度：避免插件任务先执行、宿主任务尚未禁用造成双跑
            _scheduler.Pause();
            try
            {
                // ① 修改宿主之前先持久化原状态与迁移进度；此步失败会在改动任何宿主任务前抛出，可安全重试
                foreach (var t in fresh)
                {
                    record.HostTasks.Add(new HostTaskRecord
                    {
                        TaskId = t.ID,
                        InstanceId = t.InstanceId,
                        OriginalEnable = t.Enable,
                        Status = HostTaskRecord.StatusPending
                    });
                }
                _store.SaveMigration(record);

                // ② 逐个接管：禁用成功才标记 done 并持久化进度；失败/异常保持 pending（不归并），下次按 ID 重试
                var failedCount = 0;
                var migrated = 0;
                foreach (var entry in record.HostTasks
                             .Where(r => r.Status == HostTaskRecord.StatusPending).ToList())
                {
                    var hostTask = hostTasks.FirstOrDefault(t => t.ID == entry.TaskId);
                    if (hostTask == null)
                    {
                        // 宿主任务已被并发删除：无可接管也无须还原，标记完成避免反复重试
                        entry.Status = HostTaskRecord.StatusDone;
                        _store.SaveMigration(record);
                        continue;
                    }

                    if (hostTask.Enable)
                    {
                        try
                        {
                            hostTask.Enable = false;
                            if (!MSLX.SDK.MSLX.Config.Tasks.UpdateTask(hostTask))
                            {
                                failedCount++;
                                MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 禁用宿主任务 {hostTask.ID} 失败，保持待接管状态；下次迁移将重试");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 禁用宿主任务 {hostTask.ID} 失败，保持待接管状态；下次迁移将重试", ex);
                            continue;
                        }
                    }

                    // 禁用成功（或上次已禁用）才登记 done；此处记录写失败由调用方提示重试，重试时再禁用是幂等的
                    entry.Status = HostTaskRecord.StatusDone;
                    _store.SaveMigration(record);
                    migrated++;
                }

                // ③ 仅归并已接管(done)的宿主任务；事务内读改写，防止旧快照覆盖并发创建/修改/删除的任务
                var items = new List<MigrationItem>();
                var mergedTasks = _store.UpdateTasks(tasks =>
                {
                    var byKey = new Dictionary<string, CronTaskDefinition>();
                    foreach (var def in tasks)
                    {
                        var key = MergeKeyOf(def.Type, def.Cron, def.Payload, def.RunWhenOffline, def.Enable);
                        if (!byKey.ContainsKey(key))
                        {
                            byKey[key] = def;
                        }
                    }

                    var changed = false;
                    foreach (var entry in record.HostTasks.Where(r => r.Status == HostTaskRecord.StatusDone))
                    {
                        var hostTask = hostTasks.FirstOrDefault(t => t.ID == entry.TaskId);
                        if (hostTask == null)
                        {
                            continue; // 宿主任务已不存在，不重建
                        }

                        // 归并键使用已持久化的原启停状态：此时宿主任务已被禁用，不能再读宿主当前状态
                        var key = MergeKeyOf(hostTask.Type, hostTask.Cron, hostTask.Payload, hostTask.RunWhenOffline, entry.OriginalEnable);
                        if (byKey.TryGetValue(key, out var def))
                        {
                            // 同规则且启停状态一致的任务：仅把新实例并入绑定，避免重复任务
                            if (def.Bindings.All(b => b.InstanceId != hostTask.InstanceId))
                            {
                                def.Bindings.Add(new TaskBinding { InstanceId = hostTask.InstanceId, LastRunTime = hostTask.LastRunTime });
                                items.Add(new MigrationItem { Name = def.Name, InstanceId = hostTask.InstanceId, Merged = true });
                                changed = true;
                            }
                        }
                        else
                        {
                            // 新建任务保留宿主原启停状态：停用的宿主任务导入后仍为停用
                            def = new CronTaskDefinition
                            {
                                Name = hostTask.Name,
                                Cron = hostTask.Cron,
                                Type = hostTask.Type,
                                Payload = hostTask.Payload,
                                RunWhenOffline = hostTask.RunWhenOffline,
                                Enable = entry.OriginalEnable,
                                Bindings = new List<TaskBinding>
                                {
                                    new TaskBinding { InstanceId = hostTask.InstanceId, LastRunTime = hostTask.LastRunTime }
                                }
                            };
                            tasks.Add(def);
                            byKey[key] = def;
                            items.Add(new MigrationItem { Name = def.Name, InstanceId = hostTask.InstanceId, Merged = false });
                            changed = true;
                        }
                    }

                    return changed ? tasks : null; // 无实际变化不落盘
                });

                // ④ 归并落盘（或确认无需变更）后标记完成，中断后重试不再误判、也不丢失已禁用未保存的进度
                record.PluginTasksMerged = true;
                _store.SaveMigration(record);

                return new MigrationResult
                {
                    MigratedCount = migrated,
                    FailedCount = failedCount,
                    TaskCount = mergedTasks?.Count ?? _store.LoadTasks().Count,
                    Items = items
                };
            }
            finally
            {
                _scheduler.Resume();
            }
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