using MSLX.Plugin.Cron.Models;
using MSLX.SDK.Models.Instance;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 宿主旧任务 → 多服任务 迁移服务。
/// 归并键：(Type 小写, Cron 去空白, Payload, RunWhenOffline, 启停状态)。
/// 接管流程（迁移期间暂停插件调度，避免双跑）：
/// 先持久化「原启停状态 + 待接管进度」→ 宿主任务逐个禁用（成功才标记 disabled）→
/// 仅对 disabled 的宿主任务做插件归并（事务内读改写）→ 归并落盘后统一标记 adopted。
/// 任何中间态失败都不会让宿主与插件同时执行；中断后重试按迁移记录恢复。
/// adopted 条目不再重走归并，避免用户修改/停用/删除的任务在增量导入时被复活。
/// 插件卸载时不自动还原宿主任务（宿主进程每次停止都会触发卸载，自动还原会导致双重执行）；
/// 还原仅由 <see cref="Restore"/> 手动触发（「宿主任务迁移」面板左下角「还原任务」按钮）：
/// 还原会停止插件调度并清空所有服务器绑定（任务定义保留），确保插件重载后也不会再执行。
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
    /// - 迁移记录中已彻底接管(adopted)的宿主任务 → 跳过；pending/disabled（上次中断遗留）按宿主任务 ID 恢复，
    ///   恢复时若宿主任务已被手动重新启用会再次禁用（复核），避免归并后宿主与插件双跑；
    /// - 新任务按 (Type, Cron, Payload, RunWhenOffline, 原启停状态) 归并：与已有插件任务同键
    ///   且启停状态一致则把实例并入 Bindings，否则新建插件任务（保留宿主原启停状态）；
    /// - 接管顺序：先持久化原状态与进度，再禁用宿主任务，最后归并插件任务——
    ///   禁用失败的任务保持 pending、不归并，不会出现宿主与插件同时执行；
    ///   记录写入失败时因原状态已提前落盘，重试按 ID 恢复，不会重复导入或丢失原启用状态；
    /// - 归并范围仅限本次新接管(pending→disabled)的任务：已彻底接管的 adopted 条目不再重走归并，
    ///   否则用户后续在插件侧停用/修改/删除的任务会被当成宿主新任务重新创建（复活）。
    /// 迁移期间暂停插件调度；迁移请求串行化执行。
    /// </summary>
    /// <param name="hostTaskIds">
    /// 用户勾选要导入的宿主任务 ID；为空表示导入全部待接管任务（兼容旧调用）。
    /// 未勾选的 pending/disabled 条目保持原状态，下次可继续导入，不会被误标记为已接管。
    /// </param>
    public MigrationResult Migrate(IReadOnlyCollection<string>? hostTaskIds = null)
    {
        // 串行化迁移请求：并发点击不会交错处理同一批宿主任务
        lock (_migrateLock)
        {
            NormalizeRecordStatuses();
            var record = _store.LoadMigration() ?? new MigrationRecord { MigratedAt = DateTime.Now };
            var hostTasks = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().ToList();
            if (hostTasks.Count == 0)
            {
                return new MigrationResult { Skipped = true, Reason = "宿主没有定时任务可导入" };
            }

            // 仅处理勾选的宿主任务；空集合说明用户什么都没选，直接返回而不是悄悄导入全部
            var selectedIds = hostTaskIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet();
            if (selectedIds is { Count: 0 })
            {
                return new MigrationResult { Skipped = true, Reason = "请选择要导入的宿主任务" };
            }

            bool IsSelected(string taskId) => selectedIds == null || selectedIds.Contains(taskId);

            var recordIds = record.HostTasks.Select(r => r.TaskId).ToHashSet();
            // 全新任务 = 迁移记录中完全不存在的宿主任务；pending/disabled 的按 ID 恢复，不再当作新任务重复导入
            var fresh = hostTasks.Where(t =>
                !recordIds.Contains(t.ID) &&
                IsSelected(t.ID)).ToList();
            var pendingBefore = record.HostTasks.Count(r =>
                (r.Status == HostTaskRecord.StatusPending || r.Status == HostTaskRecord.StatusDisabled) &&
                IsSelected(r.TaskId));
            if (fresh.Count == 0 && pendingBefore == 0)
            {
                return new MigrationResult { Skipped = true, Reason = "所选宿主任务已全部接管，没有需要导入的任务" };
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

                // ② 逐个接管：pending 与上次中断遗留的 disabled 都需复核宿主启停状态——
                // 禁用成功才标记 disabled（归并前中间态）并持久化进度；失败/异常保持 pending（不归并），下次按 ID 重试。
                // disabled 条目若在中断期间被用户手动重新启用，须再次禁用才能安全归并；
                // 复核禁用失败则撤回到 pending，避免按历史 OriginalEnable 归并出宿主与插件双跑。
                var failedCount = 0;
                var migrated = 0;
                foreach (var entry in record.HostTasks
                             .Where(r => (r.Status == HostTaskRecord.StatusPending ||
                                          r.Status == HostTaskRecord.StatusDisabled) &&
                                         IsSelected(r.TaskId)).ToList())
                {
                    var hostTask = hostTasks.FirstOrDefault(t => t.ID == entry.TaskId);
                    if (hostTask == null)
                    {
                        // 宿主任务已被并发删除：无可接管也无须还原，直接标记 adopted 避免反复重试
                        entry.Status = HostTaskRecord.StatusAdopted;
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
                                if (entry.Status == HostTaskRecord.StatusDisabled)
                                {
                                    // 复核失败：宿主当前是启用态，撤回到 pending，不进入归并
                                    entry.Status = HostTaskRecord.StatusPending;
                                    _store.SaveMigration(record);
                                }
                                MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 禁用宿主任务 {hostTask.ID} 失败，保持待接管状态；下次迁移将重试");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            if (entry.Status == HostTaskRecord.StatusDisabled)
                            {
                                entry.Status = HostTaskRecord.StatusPending;
                                _store.SaveMigration(record);
                            }
                            MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 禁用宿主任务 {hostTask.ID} 失败，保持待接管状态；下次迁移将重试", ex);
                            continue;
                        }
                    }

                    // 禁用成功（或上次已禁用）先落盘 disabled：归并完成后在阶段④再转 adopted；
                    // 若此步与归并之间进程崩溃，disabled 条目在下次导入时补归并而不是被当作全新任务重复创建
                    entry.Status = HostTaskRecord.StatusDisabled;
                    _store.SaveMigration(record);
                    migrated++;
                }

                // ③ 仅归并本次新接管(disabled)的宿主任务；事务内读改写，防止旧快照覆盖并发创建/修改/删除的任务。
                // 已彻底接管(adopted)的历史条目不在此列：用户可能已在插件侧停用/修改/删除对应任务，
                // 若再次归并会按历史 OriginalEnable 把它们重新创建（复活），故归并动作只允许对本次新接管发生一次。
                var items = new List<MigrationItem>();
                var adoptees = record.HostTasks
                    .Where(r => r.Status == HostTaskRecord.StatusDisabled && IsSelected(r.TaskId)).ToList();
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
                    foreach (var entry in adoptees)
                    {
                        var hostTask = hostTasks.FirstOrDefault(t => t.ID == entry.TaskId);
                        if (hostTask == null)
                        {
                            continue; // 宿主任务已不存在，不重建；阶段④统一标记 adopted 完成
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

                // ④ 所有待接管(disabled)条目无论归并是否产生实际变化，统一落盘为 adopted（彻底完成），
                //    并标记整个迁移记录完成；中断后重试不再误判、也不会丢失已禁用未保存的进度。
                foreach (var entry in record.HostTasks.Where(r => r.Status == HostTaskRecord.StatusDisabled && IsSelected(r.TaskId)))
                {
                    entry.Status = HostTaskRecord.StatusAdopted;
                }
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
    /// 兼容 v1.0.x：旧记录把「宿主已禁用」与「插件已归并」都记为 done，无法逐条区分。
    /// 但迁移记录级标志 PluginTasksMerged 足以区分两种历史状态：
    /// - PluginTasksMerged = true  → 归并早已落盘（完整迁移）→ 归一化为 adopted，绝不重走归并；
    /// - PluginTasksMerged = false → 崩溃在归并落盘前的半完成态 → 归一化为 disabled，补归并一次后转 adopted。
    /// 读取侧（预览/状态查询）用 <see cref="EffectiveStatus"/> 做内存等价判断，不落盘，保持预览「不写任何数据」的契约。
    /// </summary>
    private void NormalizeRecordStatuses()
    {
        var record = _store.LoadMigration();
        if (record == null)
        {
            return;
        }

        var changed = false;
        foreach (var r in record.HostTasks)
        {
            var effective = EffectiveStatus(record, r);
            if (r.Status != effective)
            {
                r.Status = effective;
                changed = true;
            }
        }

        if (changed)
        {
            _store.SaveMigration(record);
        }
    }

    /// <summary>
    /// 记录条目的「生效状态」：旧版 done 值按记录级 PluginTasksMerged 归一化为 adopted/disabled，
    /// 与 <see cref="NormalizeRecordStatuses"/> 的落盘归一化保持一致，供预览/状态查询做内存判断。
    /// </summary>
    private static string EffectiveStatus(MigrationRecord? record, HostTaskRecord r)
    {
        if (record != null && r.Status == HostTaskRecord.StatusDone)
        {
            return record.PluginTasksMerged
                ? HostTaskRecord.StatusAdopted
                : HostTaskRecord.StatusDisabled;
        }

        return r.Status;
    }

    /// <summary>
    /// 导入预览（干跑）：与 Migrate 相同的判重/归并逻辑，但不写任何数据，
    /// 返回全部宿主任务的去向（待导入）与已导入状态，供迁移面板展示、勾选后确认。
    /// </summary>
    public MigrationPreview Preview()
    {
        lock (_migrateLock)
        {
            return PreviewCore();
        }
    }

    private MigrationPreview PreviewCore()
    {
        var record = _store.LoadMigration();
        var hostTasks = MSLX.SDK.MSLX.Config.Tasks.GetTaskList().ToList();
        if (hostTasks.Count == 0)
        {
            return new MigrationPreview
            {
                Skipped = true,
                Reason = "宿主没有定时任务可导入",
                // 宿主任务已全部删除时仍保留迁移记录，还原入口不应因清单为空而消失
                RestorableCount = record?.HostTasks.Count ?? 0
            };
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

        // 已彻底接管(adopted)的宿主任务也返回给面板展示（勾选框回显为选中）；
        // pending/disabled（上次中断遗留）仍按待导入处理，供用户继续勾选导入。
        var recordById = record?.HostTasks
            .GroupBy(r => r.TaskId)
            .ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<string, HostTaskRecord>();
        var pending = new List<MigrationPreviewItem>();
        var imported = new List<MigrationPreviewItem>();
        foreach (var t in hostTasks)
        {
            recordById.TryGetValue(t.ID, out var entry);
            var status = entry == null ? null : EffectiveStatus(record, entry);
            var isImported = status == HostTaskRecord.StatusAdopted;
            // pending 任务取记录中的原启停状态做归并键，与 Migrate 阶段③ 的键保持一致；
            // 宿主任务可能尚未禁用（崩溃在接管中途），不能再读宿主当前状态
            var originalEnable = entry?.OriginalEnable ?? t.Enable;

            var item = new MigrationPreviewItem
            {
                HostTaskId = t.ID,
                Name = t.Name,
                Cron = t.Cron,
                Type = t.Type,
                Payload = t.Payload,
                RunWhenOffline = t.RunWhenOffline,
                InstanceId = t.InstanceId,
                OriginalEnable = originalEnable,
                Imported = isImported
            };

            // 已导入任务的归并目标属于历史信息，插件任务可能已被用户改名/删除，
            // 无法可靠回溯，故只展示宿主任务本身与「已导入」状态，不臆测目标任务名
            if (!isImported)
            {
                var key = MergeKeyOf(t.Type, t.Cron, t.Payload, t.RunWhenOffline, originalEnable);
                if (byKey.TryGetValue(key, out var def))
                {
                    item.Merged = true;
                    item.TargetName = def.Name;
                }
                else
                {
                    // 与 Migrate 一致：首个同键任务会新建多服任务，后续同键任务并入它。
                    // 记录占位目标，避免预览把两个同键任务都显示成「新建多服任务」。
                    byKey[key] = new CronTaskDefinition { Name = t.Name };
                }
            }

            (isImported ? imported : pending).Add(item);
        }

        return new MigrationPreview
        {
            Skipped = false,
            Count = pending.Count + imported.Count,
            PendingCount = pending.Count,
            ImportedCount = imported.Count,
            RestorableCount = record?.HostTasks.Count ?? 0,
            // 待导入的排在前面，用户打开面板即可处理；已导入的置后仅作状态回显
            Items = pending.Concat(imported).ToList()
        };
    }

    /// <summary>
    /// 还原：按迁移记录把宿主任务恢复为原有启用状态；迁移记录保留，避免已导入任务被重复导入。
    /// 还原后停止插件调度并清空所有服务器绑定：宿主任务已交还宿主调度器，插件若继续调度会造成双跑；
    /// 清空绑定落盘后，即使插件重载/宿主重启使调度器重新启动，插件任务也没有可执行的服务器。
    /// </summary>
    public void Restore()
    {
        // 与 Migrate 共用 _migrateLock：还原与迁移并发时不会交错修改宿主任务启停状态，避免短暂双跑
        lock (_migrateLock)
        {
            var record = _store.LoadMigration();
            if (record == null)
            {
                return;
            }

            // 先停止插件调度，再清空绑定并落盘，最后恢复宿主任务：
            // 绑定清空后即使进程在恢复宿主任务途中退出，插件重载也不会执行任何任务；
            // 若反过来先恢复宿主任务，崩溃窗口内插件任务仍带绑定，重载后会与宿主双跑。
            _scheduler.Stop();

            var clearedTasks = 0;
            var clearedBindings = 0;
            _store.UpdateTasks(tasks =>
            {
                foreach (var task in tasks)
                {
                    if (task.Bindings.Count == 0)
                    {
                        continue;
                    }

                    clearedTasks++;
                    clearedBindings += task.Bindings.Count;
                    task.Bindings.Clear();
                }

                return clearedBindings > 0 ? tasks : null;
            });

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
            MSLX.SDK.MSLX.Logger.Info(
                $"[mslx-plugin-cron] 已还原 {record.HostTasks.Count} 条宿主任务，停止插件调度并清空 {clearedTasks} 个任务的 {clearedBindings} 条服务器绑定（任务定义与迁移记录保留）");
        }
    }

    /// <summary>
    /// 查询迁移状态：是否已迁移 + 当前多服任务数。
    /// </summary>
    public (bool Migrated, string? Reason, int TaskCount, DateTime? MigratedAt) GetStatus()
    {
        var record = _store.LoadMigration();
        if (record != null)
        {
            // 存在待接管(pending/disabled)条目时迁移未完成：前端需继续提示迁移，而不是误报已迁移；
            // 旧版 done 值按 PluginTasksMerged 归一化后参与判断（见 EffectiveStatus）
            var incomplete = record.HostTasks.Any(r =>
            {
                var status = EffectiveStatus(record, r);
                return status == HostTaskRecord.StatusPending || status == HostTaskRecord.StatusDisabled;
            });
            if (incomplete)
            {
                return (false, "迁移尚未完成，有待接管任务", _store.LoadTasks().Count, record.MigratedAt);
            }

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