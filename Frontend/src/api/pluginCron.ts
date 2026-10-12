import request from 'mslx-request';

// ===== 类型 =====

export interface TaskBinding {
    instanceId: number;
    lastRunTime: string | null;
}

export interface CronTaskDefinition {
    id: string;
    name: string;
    cron: string;
    type: string;
    payload: string;
    enable: boolean;
    runWhenOffline: boolean;
    bindings: TaskBinding[];
}

export interface CronTaskPayload {
    name: string;
    cron: string;
    type: string;
    payload: string;
    enable: boolean;
    runWhenOffline: boolean;
    instanceIds: number[];
}

export interface MigrationStatus {
    migrated: boolean;
    reason?: string;
    taskCount: number;
    migratedAt?: string | null;
}

export interface MigrationPreviewItem {
    hostTaskId: string;
    name: string;
    cron: string;
    type: string;
    payload: string;
    runWhenOffline: boolean;
    instanceId: number;
    merged: boolean;
    targetName: string;
}

export interface MigrationPreview {
    skipped: boolean;
    reason?: string;
    count: number;
    items: MigrationPreviewItem[];
}

export interface TaskRunResult {
    instanceId: number;
    success: boolean;
    message: string;
}

export interface InstanceItem {
    id: number;
    name: string;
    status?: number | string;
    [key: string]: any;
}

// ===== 插件任务 API =====

const BASE = '/api/plugins/mslx-plugin-cron/tasks';

// 节点路由：插件任务 API 与实例列表一样强制发往当前选中节点。
// 宿主 mslx-request 默认按 URL 正则 auto 判定——/api/instance/* 走子节点、/api/plugins/* 走主节点；
// 不带该参数时页面展示子节点实例，却在主节点创建/执行任务，两侧实例 ID 相同时会操作到错误节点。
const NODE_REQUEST: Record<string, unknown> = { requestToSlaveNode: true };

export const cronTaskApi = {
    list: () => request.get<CronTaskDefinition[]>({ url: `${BASE}/list` }, NODE_REQUEST),
    get: (id: string) => request.get<CronTaskDefinition>({ url: `${BASE}/${id}` }, NODE_REQUEST),
    create: (data: CronTaskPayload) => request.post<string>({ url: `${BASE}/create`, data }, NODE_REQUEST),
    update: (id: string, data: CronTaskPayload) => request.post({ url: `${BASE}/update`, data: { id, ...data } }, NODE_REQUEST),
    remove: (id: string) => request.post({ url: `${BASE}/delete/${id}` }, NODE_REQUEST),
    toggle: (id: string) => request.post({ url: `${BASE}/toggle/${id}` }, NODE_REQUEST),
    run: (id: string) => request.post<TaskRunResult[]>({ url: `${BASE}/run/${id}`, timeout: 120 * 1000 }, NODE_REQUEST),
    validateCron: (cron: string) => request.post({ url: `${BASE}/validate-cron`, data: { cron } }, NODE_REQUEST),
    migrate: () => request.post({ url: `${BASE}/migrate` }, NODE_REQUEST),
    migratePreview: () => request.post<MigrationPreview>({ url: `${BASE}/migrate-preview` }, NODE_REQUEST),
    migrateStatus: () => request.get<MigrationStatus>({ url: `${BASE}/migrate-status` }, NODE_REQUEST),
};

// ===== 宿主实例 API =====

export const instanceApi = {
    // 显式声明与插件任务 API 一致的节点路由，避免依赖宿主 URL 正则的隐式判定
    list: () => request.get<InstanceItem[]>({ url: '/api/instance/list' }, NODE_REQUEST),
};

// ===== 常用 Cron 预设（宿主 cronHelper 的简化版） =====

export const CRON_PRESETS: { label: string; value: string }[] = [
    { label: '每分钟', value: '0 * * * * ?' },
    { label: '每 5 分钟', value: '0 */5 * * * ?' },
    { label: '每小时', value: '0 0 * * * ?' },
    { label: '每天 4:00', value: '0 0 4 * * ?' },
    { label: '每周一 4:00', value: '0 0 4 ? * 1' },
    { label: '每月 1 日 4:00', value: '0 0 4 1 * ?' },
];
