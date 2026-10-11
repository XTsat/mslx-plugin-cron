using MSLX.Plugin.Cron.Models;
using MSLX.SDK.Models.Instance;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 宿主旧任务 → 多服任务 迁移服务。
/// 归并键：(Type 小写, Cron 去空白, Payload, RunWhenOffline)。
/// 迁移流程：归并写入插件存储 → 宿主任务逐个禁用（保留原状态）→ 写迁移记录。
/// 卸载时按迁移记录还原宿主任务。
/// </summary>
public class CronMigrationService
{
    private readonly CronTaskStore _store;

    public CronMigrationService(CronTaskStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 导入宿主任务（幂等增量，可反复调用）：
    /// - 迁移记录中已导入过的宿主任务 → 跳过；
    /// - 新任务按 (Type, Cron, Payload, RunWhenOffline) 归并：与已有插件任务同键则把实例并入 Bindings，
    ///   否则新建插件任务；
    /// - 新导入的宿主任务被禁用（保留原状态），卸载时按迁移记录还原。
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

        var existing = _store.LoadTasks();
        var byKey = new Dictionary<string, CronTaskDefinition>();
        foreach (var def in existing)
        {
            var key = KeyOf(def.Type, def.Cron, def.Payload, def.RunWhenOffline);
            if (!byKey.ContainsKey(key))
            {
                byKey[key] = def;
            }
        }

        var tasks = existing.ToList();
        var items = new List<MigrationItem>();
        foreach (var t in fresh)
        {
            var key = KeyOf(t.Type, t.Cron, t.Payload, t.RunWhenOffline);
            if (byKey.TryGetValue(key, out var def))
            {
                // 同规则任务：仅把新实例并入绑定，避免重复任务
                if (def.Bindings.All(b => b.InstanceId != t.InstanceId))
                {
                    def.Bindings.Add(new TaskBinding { InstanceId = t.InstanceId, LastRunTime = t.LastRunTime });
                }
                items.Add(new MigrationItem { Name = def.Name, InstanceId = t.InstanceId, Merged = true });
            }
            else
            {
                def = new CronTaskDefinition
                {
                    Name = t.Name,
                    Cron = t.Cron,
                    Type = t.Type,
                    Payload = t.Payload,
                    RunWhenOffline = t.RunWhenOffline,
                    Enable = true,
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
        foreach (var t in fresh)
        {
            record.HostTasks.Add(new HostTaskRecord
            {
                TaskId = t.ID,
                InstanceId = t.InstanceId,
                OriginalEnable = t.Enable
            });

            t.Enable = false;
            MSLX.SDK.MSLX.Config.Tasks.UpdateTask(t);
        }
        _store.SaveMigration(record);

        return new MigrationResult
        {
            MigratedCount = fresh.Count,
            TaskCount = tasks.Count
        };
    }

    private static string KeyOf(string type, string cron, string? payload, bool runWhenOffline)
    {
        return $"{type.ToLower().Trim()}|{cron.Trim()}|{payload ?? ""}|{(runWhenOffline ? 1 : 0)}";
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
            var key = KeyOf(def.Type, def.Cron, def.Payload, def.RunWhenOffline);
            if (!byKey.ContainsKey(key))
            {
                byKey[key] = def;
            }
        }

        var items = new List<MigrationPreviewItem>();
        foreach (var t in fresh)
        {
            var key = KeyOf(t.Type, t.Cron, t.Payload, t.RunWhenOffline);
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