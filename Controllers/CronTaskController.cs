using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.Cron.Models;
using MSLX.Plugin.Cron.Services;
using MSLX.SDK.Models;

namespace MSLX.Plugin.Cron.Controllers;

/// <summary>
/// 多服定时任务 REST API。
/// 鉴权模式与宿主 CronTaskController 一致：UserId claim + HasResourcePermission("server", instanceId)，
/// shell 类型仅管理员。非管理员查看列表时按权限过滤绑定服务器（自己的任务始终可见）；
/// 编辑包含其无权限服务器的任务被禁止——需要个人版本时用「复制任务」创建无绑定副本自行配置。
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
                // 至少一台绑定有权限 或 自己是创建者（含未绑定服务器的副本）才可见；绑定裁剪为有权限的子集
                if (allowed.Count > 0 || task.CreatorUserId == UserId)
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
            // 自己的任务（含未绑定服务器的副本）即使无可操作绑定也可见，其余任务裁剪后为空视为不可见
            if (task.Bindings.Count == 0 && task.CreatorUserId != UserId)
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

        CronTaskDefinition? created = null;
        var result = _store.UpdateTasks(tasks =>
        {
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
                    .ToList(),
                CreatorUserId = UserId
            };
            tasks.Add(task);
            created = task;
            return tasks;
        });

        if (result == null)
        {
            return BadRequestMsg("任务创建失败，请重试");
        }

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 创建任务 [{created!.Name}] 绑定 {created.Bindings.Count} 台服务器");
        return Ok(new ApiResponse<string> { Code = 200, Message = "任务创建成功", Data = created.Id });
    }

    /// <summary>
    /// 复制任务为不带任何绑定服务器的独立副本。
    /// 共享任务（含无权限服务器的任务）普通用户无法直接修改，借此获得自己的版本后在副本上配置有权限的服务器。
    /// </summary>
    [HttpPost("copy/{id}")]
    public IActionResult Copy(string id)
    {
        var source = _store.LoadTasks().FirstOrDefault(t => t.Id == id);
        if (source == null)
        {
            return NotFoundMsg("未找到指定 ID 的任务");
        }

        if (!IsAdmin)
        {
            // 与列表可见性一致：至少一台绑定服务器有权限才可复制（完全无权限的任务对普通用户本就不可见）
            if (!source.Bindings.Any(b => HasPermission(b.InstanceId)))
            {
                return Forbidden("你对该任务绑定的服务器都没有操作权限");
            }

            // 副本继承源任务的 shell 类型；普通用户无法创建/执行/启用 shell 任务，复制只会得到不可用的死副本
            if (source.Type?.ToLower() == "shell")
            {
                return Forbidden("仅管理员可复制 shell 类型的定时任务");
            }
        }

        CronTaskDefinition? created = null;
        var result = _store.UpdateTasks(tasks =>
        {
            var task = new CronTaskDefinition
            {
                Name = source.Name,
                Cron = source.Cron,
                Type = source.Type,
                Payload = source.Payload,
                Enable = source.Enable,
                RunWhenOffline = source.RunWhenOffline,
                // 副本不继承任何绑定服务器：由复制者自行配置自己有权限的服务器
                Bindings = new List<TaskBinding>(),
                CreatorUserId = UserId
            };
            tasks.Add(task);
            created = task;
            return tasks;
        });

        if (result == null || created == null)
        {
            return BadRequestMsg("任务复制失败，请重试");
        }

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 用户 {UserId} 复制任务 [{source.Name}] 为无绑定副本 [{created.Name}]");
        return Ok(new ApiResponse<CronTaskDefinition>
        {
            Code = 200,
            Message = "已复制为无绑定服务器的新任务，可在副本上配置你有权限的服务器",
            Data = created
        });
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

        // 查找、权限校验与修改在同一事务内完成，防止并发期间任务被其它请求改动
        string? forbidMessage = null;
        string? updatedName = null;
        int updatedBindingCount = 0;
        var result = _store.UpdateTasks(tasks =>
        {
            var existing = tasks.FirstOrDefault(t => t.Id == request.Id);
            if (existing == null)
            {
                return null;
            }

            if (!IsAdmin)
            {
                // 任务含用户无权限的服务器（管理员服务器）时禁止修改：保存会按前端表单重建绑定，
                // 若不拦截会把裁剪隐藏的其他服务器绑定整体剔除，等价于普通用户改动共享任务影响他人/管理员的服务器。
                // 需要个人版本时在编辑弹窗点「复制任务」创建无绑定副本，自行配置自己有权限的服务器。
                if (existing.Bindings.Any(b => !HasPermission(b.InstanceId)))
                {
                    forbidMessage = "该任务包含你无权限的服务器，无法修改；可在编辑弹窗点击「复制任务」创建无服务器的副本，配置你有权限的服务器";
                    return null;
                }
            }

            foreach (var instanceId in request.InstanceIds)
            {
                if (!HasPermission(instanceId))
                {
                    forbidMessage = $"你对服务器 {instanceId} 没有操作权限";
                    return null;
                }
            }

            if (request.Type.ToLower() == "shell" && !IsAdmin)
            {
                forbidMessage = "仅管理员可创建 shell 类型的定时任务";
                return null;
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
            updatedName = existing.Name;
            updatedBindingCount = newBindings.Count;
            return tasks;
        });

        if (result == null)
        {
            if (forbidMessage != null)
            {
                return Forbidden(forbidMessage);
            }

            return NotFoundMsg("未找到指定 ID 的任务");
        }

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 更新任务 [{updatedName}] 绑定 {updatedBindingCount} 台服务器");
        return Ok(new ApiResponse<object> { Code = 200, Message = "任务更新成功" });
    }

    /// <summary>删除任务</summary>
    [HttpPost("delete/{id}")]
    public IActionResult Delete(string id)
    {
        string? forbidMessage = null;
        string? deletedName = null;
        var unauthorized = new List<uint>();
        var result = _store.UpdateTasks(tasks =>
        {
            var task = tasks.FirstOrDefault(t => t.Id == id);
            if (task == null)
            {
                return null;
            }

            if (!IsAdmin)
            {
                // 至少一台绑定服务器有权限才允许删除：无权限的任务在列表中本就不可见，
                // 此校验只拦截对完全无权限任务的 API 直调，不产生「编辑/删除死角」。
                // 个别无权限/已删除服务器绑定不再逐台校验，允许整单删除（多服任务为共享资源）。
                if (!task.Bindings.Any(b => HasPermission(b.InstanceId)))
                {
                    forbidMessage = "你对该任务绑定的服务器都没有操作权限";
                    return null;
                }

                unauthorized = task.Bindings
                    .Where(b => !HasPermission(b.InstanceId))
                    .Select(b => b.InstanceId)
                    .ToList();
            }

            tasks.Remove(task);
            deletedName = task.Name;
            return tasks;
        });

        if (result == null)
        {
            if (forbidMessage != null)
            {
                return Forbidden(forbidMessage);
            }

            return BadRequestMsg("删除失败，未找到指定 ID 的任务");
        }

        if (!IsAdmin && unauthorized.Count > 0)
        {
            MSLX.SDK.MSLX.Logger.Warn($"[mslx-plugin-cron] 普通用户 {UserId} 删除任务 [{deletedName}]，其中 {unauthorized.Count} 台绑定服务器无操作权限");
        }

        MSLX.SDK.MSLX.Logger.Info($"[mslx-plugin-cron] 删除任务 [{deletedName}]");
        return Ok(new ApiResponse<object> { Code = 200, Message = "任务已删除" });
    }

    /// <summary>启用/停用任务</summary>
    [HttpPost("toggle/{id}")]
    public IActionResult Toggle(string id)
    {
        string? forbidMessage = null;
        bool? newEnable = null;
        string? toggledName = null;
        var unauthorized = new List<uint>();
        var result = _store.UpdateTasks(tasks =>
        {
            var task = tasks.FirstOrDefault(t => t.Id == id);
            if (task == null)
            {
                return null;
            }

            if (!IsAdmin)
            {
                // 停用任务翻转为启用会恢复调度执行；shell 任务的启用等同恢复执行系统命令，仅限管理员
                if (task.Type?.ToLower() == "shell" && !task.Enable)
                {
                    forbidMessage = "仅管理员可启用 shell 类型的定时任务";
                    return null;
                }

                // 至少一台绑定服务器有权限才允许整体启停：无权限的任务在列表中本就不可见，
                // 此校验只拦截对完全无权限任务的 API 直调。含个别权限被收回的绑定不再逐台校验，
                // 否则这类任务将无法启停（连停掉它都不行），形成操作死角。
                if (!task.Bindings.Any(b => HasPermission(b.InstanceId)))
                {
                    forbidMessage = "你对该任务绑定的服务器都没有操作权限";
                    return null;
                }

                unauthorized = task.Bindings
                    .Where(b => !HasPermission(b.InstanceId))
                    .Select(b => b.InstanceId)
                    .ToList();
            }

            task.Enable = !task.Enable;
            newEnable = task.Enable;
            toggledName = task.Name;
            return tasks;
        });

        if (result == null)
        {
            if (forbidMessage != null)
            {
                return Forbidden(forbidMessage);
            }

            return BadRequestMsg("未找到指定 ID 的任务");
        }

        if (!IsAdmin && unauthorized.Count > 0)
        {
            MSLX.SDK.MSLX.Logger.Warn($"[mslx-plugin-cron] 普通用户 {UserId} {(newEnable == true ? "启用" : "停用")}任务 [{toggledName}]，其中 {unauthorized.Count} 台绑定服务器无操作权限");
        }

        return Ok(new ApiResponse<object> { Code = 200, Message = newEnable == true ? "任务已启用" : "任务已停用" });
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
            // shell 任务执行的是系统命令，无论是否拥有服务器权限，普通用户一律禁止手动执行
            if (task.Type?.ToLower() == "shell")
            {
                return Forbidden("仅管理员可执行 shell 类型的定时任务");
            }

            foreach (var binding in task.Bindings)
            {
                if (!HasPermission(binding.InstanceId))
                {
                    return Forbidden($"你对服务器 {binding.InstanceId} 没有操作权限");
                }
            }
        }

        // 并行分发到各绑定服务器，与定时调度路径一致，避免串行执行拖长总耗时
        // （如多台 restart 每台约 75 秒，串行会超过前端 120s 超时）
        var results = (await Task.WhenAll(task.Bindings.Select(async binding =>
        {
            // 与定时任务共用执行路径并登记到调度器 _running：卸载时可取消并等待手动执行退出
            var (success, message) = await _scheduler.RunNowAsync(task, binding.InstanceId);
            return new TaskRunResult
            {
                InstanceId = binding.InstanceId,
                Success = success,
                Message = message
            };
        }))).ToList();

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
            var message = result.Skipped
                ? result.Reason ?? "无需迁移"
                : result.FailedCount > 0
                    ? $"迁移完成，但有 {result.FailedCount} 条宿主任务禁用失败（保持待接管状态，可再次执行迁移重试）"
                    : "迁移完成";
            return Ok(new ApiResponse<MigrationResult>
            {
                Code = 200,
                Message = message,
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

    /// <summary>
    /// 手动还原全部宿主任务（仅管理员，兜底）：把迁移时禁用的宿主任务按原启停状态恢复。
    /// 卸载插件前的应急通道——正常情况下宿主任务由插件持续接管、保持禁用。
    /// 迁移动态为增量幂等设计，还原后保留迁移记录，已导入任务不会重复出现在导入预览中。
    /// </summary>
    [HttpPost("migrate-restore")]
    public IActionResult MigrateRestore()
    {
        if (!IsAdmin)
        {
            return Forbidden("仅管理员可执行还原");
        }

        try
        {
            _migration.Restore();
            return Ok(new ApiResponse<object> { Code = 200, Message = "已按迁移记录还原宿主任务并停止插件调度（迁移记录保留，已导入任务不再出现在导入预览）" });
        }
        catch (Exception ex)
        {
            MSLX.SDK.MSLX.Logger.Error("[mslx-plugin-cron] 还原宿主任务失败", ex);
            return BadRequestMsg($"还原失败: {ex.Message}");
        }
    }
}