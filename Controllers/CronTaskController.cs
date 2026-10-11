using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.Cron.Models;
using MSLX.Plugin.Cron.Services;
using MSLX.SDK.Models;

namespace MSLX.Plugin.Cron.Controllers;

/// <summary>
/// 多服定时任务 REST API。
/// 鉴权模式与宿主 CronTaskController 一致：UserId claim + HasResourcePermission("server", instanceId)，
/// shell 类型仅管理员。非管理员查看列表时按权限过滤绑定服务器。
/// </summary>
[ApiController]
[Route("api/plugins/mslx-plugin-cron/tasks")]
public class CronTaskController : ControllerBase
{
    private readonly CronTaskStore _store;
    private readonly CronMigrationService _migration;
    private readonly CronSchedulerService _scheduler;

    public CronTaskController(CronTaskStore store, CronMigrationService migration, CronSchedulerService scheduler)
    {
        _store = store;
        _migration = migration;
        _scheduler = scheduler;
    }

    private string UserId => User?.FindFirst("UserId")?.Value ?? "";

    private bool IsAdmin => CurrentUser?.Role == "admin";

    private SDK.Models.UserInfo? CurrentUser => MSLX.SDK.MSLX.Config.Users.GetUserById(UserId);

    private bool HasPermission(uint instanceId) =>
        IsAdmin || MSLX.SDK.MSLX.Config.Users.HasResourcePermission(UserId, "server", (int)instanceId);

    private IActionResult Forbidden(string message) =>
        Ok(new ApiResponse<object> { Code = 403, Message = message });

    private IActionResult BadRequestMsg(string message) =>
        BadRequest(new ApiResponse<object> { Code = 400, Message = message });

    private IActionResult NotFoundMsg(string message) =>
        NotFound(new ApiResponse<object> { Code = 404, Message = message });

    /// <summary>获取全部多服任务（非管理员按权限过滤绑定服务器）</summary>
    [HttpGet("list")]
    public IActionResult List()
    {
        var tasks = _store.LoadTasks();

        if (!IsAdmin)
        {
            var filtered = new List<CronTaskDefinition>();
            foreach (var task in tasks)
            {
                var allowed = task.Bindings.Where(b => HasPermission(b.InstanceId)).ToList();
                if (allowed.Count > 0)
                {
                    task.Bindings = allowed;
                    filtered.Add(task);
                }
            }

            tasks = filtered;
        }

        return Ok(new ApiResponse<List<CronTaskDefinition>>
        {
            Code = 200,
            Message = "获取成功",
            Data = tasks
        });
    }

    /// <summary>获取单个任务</summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var task = _store.LoadTasks().FirstOrDefault(t => t.Id == id);
        if (task == null)
        {
            return NotFoundMsg("未找到指定 ID 的任务");
        }

        if (!IsAdmin)
        {
            task.Bindings = task.Bindings.Where(b => HasPermission(b.InstanceId)).ToList();
            if (task.Bindings.Count == 0)
            {
                return NotFoundMsg("未找到指定 ID 的任务");
            }
        }

        return Ok(new ApiResponse<CronTaskDefinition> { Code = 200, Message = "获取成功", Data = task });
    }

    /// <summary>创建多服任务</summary>
    [HttpPost("create")]
    public IActionResult Create([FromBody] CronTaskRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors).Select(e => e.ErrorMessage))
            });
        }

        // 权限：每个目标服务器都必须有权限
        foreach (var instanceId in request.InstanceIds)
        {
            if (!HasPermission(instanceId))
            {
                return Forbidden($"你对服务器 {instanceId} 没有操作权限");
            }
        }

        // shell 仅管理员
        if (request.Type.ToLower() == "shell" && !IsAdmin)
        {
            return Forbidden("仅管理员可创建 shell 类型的定时任务");
        }

        var tasks = _store.LoadTasks();
        var task = new CronTaskDefinition
        {
            Name = request.Name,
            Cron = request.Cron,
            Type = request.Type,
            Payload = request.Payload,
            Enable = request.Enable,
            RunWhenOffline = request.RunWhenOffline,
            Bindings = request.InstanceIds
                .Distinct()
                .Select(i => new TaskBinding { InstanceId = i, LastRunTime = null })
                .ToList()
        };
        tasks.Add(task);
        _store.SaveTasks(tasks);

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 创建任务 [{task.Name}] 绑定 {task.Bindings.Count} 台服务器");
        return Ok(new ApiResponse<string> { Code = 200, Message = "任务创建成功", Data = task.Id });
    }

    /// <summary>更新多服任务（bindings 按 InstanceId 重建，保留原 LastRunTime）</summary>
    [HttpPost("update")]
    public IActionResult Update([FromBody] CronTaskUpdateRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors).Select(e => e.ErrorMessage))
            });
        }

        var tasks = _store.LoadTasks();
        var existing = tasks.FirstOrDefault(t => t.Id == request.Id);
        if (existing == null)
        {
            return NotFoundMsg("未找到指定 ID 的任务");
        }

        // 权限：原绑定与目标服务器都需有权限
        if (!IsAdmin)
        {
            foreach (var binding in existing.Bindings)
            {
                if (!HasPermission(binding.InstanceId))
                {
                    return Forbidden($"你对服务器 {binding.InstanceId} 没有操作权限");
                }
            }
        }

        foreach (var instanceId in request.InstanceIds)
        {
            if (!HasPermission(instanceId))
            {
                return Forbidden($"你对服务器 {instanceId} 没有操作权限");
            }
        }

        if (request.Type.ToLower() == "shell" && !IsAdmin)
        {
            return Forbidden("仅管理员可创建 shell 类型的定时任务");
        }

        var oldRunTimes = existing.Bindings.ToDictionary(b => b.InstanceId, b => b.LastRunTime);
        var newBindings = new List<TaskBinding>();
        foreach (var instanceId in request.InstanceIds.Distinct())
        {
            newBindings.Add(new TaskBinding
            {
                InstanceId = instanceId,
                LastRunTime = oldRunTimes.TryGetValue(instanceId, out var last) ? last : null
            });
        }

        existing.Name = request.Name;
        existing.Cron = request.Cron;
        existing.Type = request.Type;
        existing.Payload = request.Payload;
        existing.Enable = request.Enable;
        existing.RunWhenOffline = request.RunWhenOffline;
        existing.Bindings = newBindings;

        _store.SaveTasks(tasks);

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 更新任务 [{existing.Name}] 绑定 {existing.Bindings.Count} 台服务器");
        return Ok(new ApiResponse<object> { Code = 200, Message = "任务更新成功" });
    }

    /// <summary>删除任务</summary>
    [HttpPost("delete/{id}")]
    public IActionResult Delete(string id)
    {
        var tasks = _store.LoadTasks();
        var task = tasks.FirstOrDefault(t => t.Id == id);
        if (task == null)
        {
            return BadRequestMsg("删除失败，未找到指定 ID 的任务");
        }

        if (!IsAdmin)
        {
            foreach (var binding in task.Bindings)
            {
                if (!HasPermission(binding.InstanceId))
                {
                    return Forbidden($"你对服务器 {binding.InstanceId} 没有操作权限");
                }
            }
        }

        tasks.Remove(task);
        _store.SaveTasks(tasks);
        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 删除任务 [{task.Name}]");
        return Ok(new ApiResponse<object> { Code = 200, Message = "任务已删除" });
    }

    /// <summary>启用/停用任务</summary>
    [HttpPost("toggle/{id}")]
    public IActionResult Toggle(string id)
    {
        var tasks = _store.LoadTasks();
        var task = tasks.FirstOrDefault(t => t.Id == id);
        if (task == null)
        {
            return BadRequestMsg("未找到指定 ID 的任务");
        }

        if (!IsAdmin)
        {
            foreach (var binding in task.Bindings)
            {
                if (!HasPermission(binding.InstanceId))
                {
                    return Forbidden($"你对服务器 {binding.InstanceId} 没有操作权限");
                }
            }
        }

        task.Enable = !task.Enable;
        _store.SaveTasks(tasks);
        return Ok(new ApiResponse<object> { Code = 200, Message = task.Enable ? "任务已启用" : "任务已停用" });
    }

    /// <summary>立即执行一次（对全部绑定服务器，同步等待结果）</summary>
    [HttpPost("run/{id}")]
    public async Task<IActionResult> Run(string id)
    {
        var task = _store.LoadTasks().FirstOrDefault(t => t.Id == id);
        if (task == null)
        {
            return BadRequestMsg("未找到指定 ID 的任务");
        }

        if (!IsAdmin)
        {
            foreach (var binding in task.Bindings)
            {
                if (!HasPermission(binding.InstanceId))
                {
                    return Forbidden($"你对服务器 {binding.InstanceId} 没有操作权限");
                }
            }
        }

        var results = new List<TaskRunResult>();
        foreach (var binding in task.Bindings)
        {
            var (success, message) = await _scheduler.ExecuteAsync(task, binding.InstanceId);
            results.Add(new TaskRunResult
            {
                InstanceId = binding.InstanceId,
                Success = success,
                Message = message
            });
        }

        return Ok(new ApiResponse<List<TaskRunResult>>
        {
            Code = 200,
            Message = "执行完成",
            Data = results
        });
    }

    /// <summary>校验 Cron 表达式（秒级）</summary>
    [HttpPost("validate-cron")]
    public IActionResult ValidateCron([FromBody] CronValidateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cron))
        {
            return BadRequestMsg("Cron 表达式不能为空");
        }

        try
        {
            Cronos.CronExpression.Parse(request.Cron, Cronos.CronFormat.IncludeSeconds);
            return Ok(new ApiResponse<object> { Code = 200, Message = "Cron 表达式有效" });
        }
        catch (Exception ex)
        {
            return BadRequestMsg($"无效的 Cron 表达式: {ex.Message}");
        }
    }

    /// <summary>一键迁移宿主旧任务（仅管理员）</summary>
    [HttpPost("migrate")]
    public IActionResult Migrate()
    {
        if (!IsAdmin)
        {
            return Forbidden("仅管理员可执行迁移");
        }

        try
        {
            var result = _migration.Migrate();
            return Ok(new ApiResponse<MigrationResult>
            {
                Code = 200,
                Message = result.Skipped ? result.Reason ?? "无需迁移" : "迁移完成",
                Data = result
            });
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Error("[mslx-plugin-cron] 迁移失败", ex);
            return BadRequestMsg($"迁移失败: {ex.Message}");
        }
    }

    /// <summary>导入预览（干跑）：扫描宿主任务并计算去向（并入/新建），不写任何数据（仅管理员）</summary>
    [HttpPost("migrate-preview")]
    public IActionResult MigratePreview()
    {
        if (!IsAdmin)
        {
            return Forbidden("仅管理员可执行迁移");
        }

        try
        {
            var result = _migration.Preview();
            return Ok(new ApiResponse<MigrationPreview>
            {
                Code = 200,
                Message = result.Skipped ? result.Reason ?? "无需迁移" : "获取成功",
                Data = result
            });
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Error("[mslx-plugin-cron] 导入预览失败", ex);
            return BadRequestMsg($"预览失败: {ex.Message}");
        }
    }

    /// <summary>迁移状态</summary>
    [HttpGet("migrate-status")]
    public IActionResult MigrateStatus()
    {
        var (migrated, reason, taskCount, migratedAt) = _migration.GetStatus();
        return Ok(new ApiResponse<object>
        {
            Code = 200,
            Message = "获取成功",
            Data = new
            {
                migrated,
                reason,
                taskCount,
                migratedAt
            }
        });
    }
}