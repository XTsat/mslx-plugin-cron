using System.ComponentModel.DataAnnotations;
using MSLX.SDK.Models.Instance;
using Newtonsoft.Json;

namespace MSLX.Plugin.Cron.Models;

/// <summary>
/// 插件自己的多服任务定义（任务绑定服务器）。
/// 存储于插件数据目录 cron-tasks.json。
/// </summary>
public class CronTaskDefinition
{
    [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("cron")] public string Cron { get; set; } = "";
    [JsonProperty("type")] public string Type { get; set; } = "command";
    [JsonProperty("payload")] public string Payload { get; set; } = "";
    [JsonProperty("enable")] public bool Enable { get; set; } = true;
    [JsonProperty("runWhenOffline")] public bool RunWhenOffline { get; set; } = true;
    [JsonProperty("bindings")] public List<TaskBinding> Bindings { get; set; } = new();
}

/// <summary>
/// 服务器绑定：该服务器在此任务上的启用状态与上次执行时间。
/// </summary>
public class TaskBinding
{
    [JsonProperty("instanceId")] public uint InstanceId { get; set; }
    [JsonProperty("lastRunTime")] public DateTime? LastRunTime { get; set; }
}

/// <summary>
/// 创建/更新任务的请求体（校验与宿主 CreateTaskRequest 保持一致）。
/// </summary>
public class CronTaskRequest
{
    [Required(ErrorMessage = "任务名称不能为空")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Cron 表达式不能为空")]
    [CreateTaskRequest.CronExpressionAttribute(ErrorMessage = "无效的 Cron 表达式，请检查格式 (示例: '0 0 12 * * ?')")]
    public string Cron { get; set; } = "";

    [Required]
    [AllowedValues("command", "start", "stop", "restart", "backup", "shell", ErrorMessage = "不支持的任务类型，仅支持: command, start, stop, restart, backup, shell")]
    public string Type { get; set; } = "command";

    public string Payload { get; set; } = "";

    public bool Enable { get; set; } = true;

    public bool RunWhenOffline { get; set; } = true;

    [Required(ErrorMessage = "请至少选择一台服务器")]
    [MinLength(1, ErrorMessage = "请至少选择一台服务器")]
    public List<uint> InstanceIds { get; set; } = new();
}

/// <summary>
/// 更新任务请求体。
/// </summary>
public class CronTaskUpdateRequest : CronTaskRequest
{
    [Required(ErrorMessage = "任务 ID 不能为空")]
    public string Id { get; set; } = "";
}

/// <summary>
/// 迁移记录：宿主哪些任务被禁用、原状态如何，用于卸载时还原与中断重试。
/// </summary>
public class MigrationRecord
{
    [JsonProperty("migratedAt")] public DateTime MigratedAt { get; set; } = DateTime.Now;
    [JsonProperty("hostTasks")] public List<HostTaskRecord> HostTasks { get; set; } = new();

    /// <summary>
    /// 插件任务归并是否已落盘。宿主任务全部禁用成功后才置 true；
    /// 用于区分「宿主已禁用、插件未保存」的半完成态，中断后重试据此恢复归并而不是误判为全部完成。
    /// </summary>
    [JsonProperty("pluginTasksMerged")] public bool PluginTasksMerged { get; set; }
}

public class HostTaskRecord
{
    public const string StatusPending = "pending";
    public const string StatusDone = "done";

    [JsonProperty("taskId")] public string TaskId { get; set; } = "";
    [JsonProperty("instanceId")] public uint InstanceId { get; set; }
    [JsonProperty("originalEnable")] public bool OriginalEnable { get; set; }
    /// <summary>
    /// 接管进度：pending = 原状态已持久化、宿主任务尚未确认禁用成功；done = 宿主任务已禁用、插件侧已接管。
    /// </summary>
    [JsonProperty("status")] public string Status { get; set; } = StatusPending;
}

public class MigrationResult
{
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public int MigratedCount { get; set; }
    /// <summary>本次迁移中禁用宿主任务失败的条数（保持待接管状态，下次迁移自动重试）。</summary>
    public int FailedCount { get; set; }
    public int TaskCount { get; set; }
    public List<MigrationItem> Items { get; set; } = new();
}

/// <summary>导入明细：本次导入的宿主任务去向（供前端结果弹窗展示）。</summary>
public class MigrationItem
{
    public string Name { get; set; } = "";
    public uint InstanceId { get; set; }
    /// <summary>true = 并入已有多服任务的绑定；false = 新建了一个多服任务。</summary>
    public bool Merged { get; set; }
}

/// <summary>导入预览（干跑，不写任何数据）：展示点击导入后会发生什么，供用户确认。</summary>
public class MigrationPreview
{
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public int Count { get; set; }
    public List<MigrationPreviewItem> Items { get; set; } = new();
}

public class MigrationPreviewItem
{
    public string HostTaskId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cron { get; set; } = "";
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool RunWhenOffline { get; set; }
    public uint InstanceId { get; set; }
    /// <summary>true = 并入已有多服任务；false = 新建任务。</summary>
    public bool Merged { get; set; }
    /// <summary>Merged=true 时并入的目标多服任务名称。</summary>
    public string TargetName { get; set; } = "";
}

public class CronValidateRequest
{
    [Required(ErrorMessage = "Cron 表达式不能为空")]
    public string Cron { get; set; } = "";
}

public class TaskRunResult
{
    public uint InstanceId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
