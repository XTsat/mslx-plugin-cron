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
    }

    public void SaveTasks(List<CronTaskDefinition> tasks)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dataPath);
            var json = JsonConvert.SerializeObject(tasks, Formatting.Indented);
            AtomicWrite(_tasksPath, json);
        }
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