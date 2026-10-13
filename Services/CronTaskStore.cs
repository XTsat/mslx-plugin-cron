using MSLX.Plugin.Cron.Models;
using MSLX.SDK;
using Newtonsoft.Json;

namespace MSLX.Plugin.Cron.Services;

/// <summary>
/// 插件数据存储：负责 cron-tasks.json（多服任务定义）与
/// cron-migration.json（迁移记录）的读写，线程安全。
/// </summary>
public class CronTaskStore
{
    private readonly object _lock = new();
    private readonly string _dataPath;
    private readonly string _tasksPath;
    private readonly string _migrationPath;

    public CronTaskStore()
    {
        _dataPath = PluginEntry.Instance.Config().GetDataPath();
        _tasksPath = Path.Combine(_dataPath, "cron-tasks.json");
        _migrationPath = Path.Combine(_dataPath, "cron-migration.json");
    }

    public string MigrationPath => _migrationPath;

    public List<CronTaskDefinition> LoadTasks()
    {
        lock (_lock)
        {
            return LoadTasksLocked();
        }
    }

    public void SaveTasks(List<CronTaskDefinition> tasks)
    {
        lock (_lock)
        {
            SaveTasksLocked(tasks);
        }
    }

    /// <summary>
    /// 在单把锁内完成「读取 → 修改 → 保存」完整事务，防止并发下旧快照覆盖新数据。
    /// transform 就地修改任务列表并原样返回；返回 null 表示放弃本次修改（不落盘）。
    /// </summary>
    public List<CronTaskDefinition>? UpdateTasks(
        Func<List<CronTaskDefinition>, List<CronTaskDefinition>?> transform)
    {
        lock (_lock)
        {
            var tasks = LoadTasksLocked();
            var result = transform(tasks);
            if (result == null)
            {
                return null;
            }

            SaveTasksLocked(result);
            return result;
        }
    }

    /// <summary>
    /// 调度器专用：单个事务内批量更新多个绑定的 LastRunTime（读→定位→改→保存 全程持锁），
    /// 只动到期绑定的时间戳，任务列表其它部分（含停用任务）原样保留。
    /// 定位不到的任务/绑定（已被并发删除）自动跳过；返回实际更新的绑定数。
    /// </summary>
    public int UpdateBindingsLastRunTime(
        IReadOnlyList<(string TaskId, uint InstanceId, DateTime LastRunTime)> updates)
    {
        var updated = 0;
        UpdateTasks(tasks =>
        {
            foreach (var (taskId, instanceId, lastRunTime) in updates)
            {
                var binding = tasks
                    .FirstOrDefault(t => t.Id == taskId)?
                    .Bindings.FirstOrDefault(b => b.InstanceId == instanceId);
                if (binding == null)
                {
                    continue; // 任务或绑定已被并发删除，跳过本次写回
                }

                binding.LastRunTime = lastRunTime;
                updated++;
            }

            return updated > 0 ? tasks : null;
        });

        return updated;
    }

    /// <summary>
    /// 跟随清理已删除服务器的绑定：移除实例已不存在的绑定条目；任务因此失去全部绑定则一并删除该任务。
    /// 仅当确有删除时落盘；返回 (删除的绑定数, 删除的任务数)。调用方负责保证 instanceExists 基于有效的服务器清单。
    /// </summary>
    public (int RemovedBindings, int RemovedTasks) PruneDeletedInstances(Func<uint, bool> instanceExists)
    {
        var removedBindings = 0;
        var removedTasks = 0;
        UpdateTasks(tasks =>
        {
            for (var i = tasks.Count - 1; i >= 0; i--)
            {
                var task = tasks[i];
                if (task.Bindings.Count == 0)
                {
                    continue; // 本就无绑定的任务不属于本次清理范畴，留给用户处理
                }

                var kept = task.Bindings.Where(b => instanceExists(b.InstanceId)).ToList();
                removedBindings += task.Bindings.Count - kept.Count;
                if (kept.Count == 0)
                {
                    tasks.RemoveAt(i); // 任务随其最后一名绑定服务器一起删除
                    removedTasks++;
                }
                else
                {
                    task.Bindings = kept;
                }
            }

            return removedBindings > 0 || removedTasks > 0 ? tasks : null;
        });

        return (removedBindings, removedTasks);
    }

    private List<CronTaskDefinition> LoadTasksLocked()
    {
        if (!File.Exists(_tasksPath))
        {
            return new List<CronTaskDefinition>();
        }

        try
        {
            var json = File.ReadAllText(_tasksPath);
            return JsonConvert.DeserializeObject<List<CronTaskDefinition>>(json) ?? new List<CronTaskDefinition>();
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 读取 cron-tasks.json 失败", ex);
            return new List<CronTaskDefinition>();
        }
    }

    private void SaveTasksLocked(List<CronTaskDefinition> tasks)
    {
        Directory.CreateDirectory(_dataPath);
        var json = JsonConvert.SerializeObject(tasks, Formatting.Indented);
        AtomicWrite(_tasksPath, json);
    }

    public MigrationRecord? LoadMigration()
    {
        lock (_lock)
        {
            if (!File.Exists(_migrationPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(_migrationPath);
                return JsonConvert.DeserializeObject<MigrationRecord>(json);
            }
            catch (Exception ex)
            {
                MSLX.SDK.MSLX.Logger.Error($"[mslx-plugin-cron] 读取 cron-migration.json 失败", ex);
                return null;
            }
        }
    }

    public void SaveMigration(MigrationRecord record)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dataPath);
            var json = JsonConvert.SerializeObject(record, Formatting.Indented);
            AtomicWrite(_migrationPath, json);
        }
    }

    public void ClearMigration()
    {
        lock (_lock)
        {
            if (File.Exists(_migrationPath))
            {
                File.Delete(_migrationPath);
            }
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, true);
    }
}