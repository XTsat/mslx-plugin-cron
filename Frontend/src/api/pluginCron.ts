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

export const cronTaskApi = {
    list: () => request.get<CronTaskDefinition[]>({ url: `${BASE}/list` }),
    get: (id: string) => request.get<CronTaskDefinition>({ url: `${BASE}/${id}` }),
    create: (data: CronTaskPayload) => request.post<string>({ url: `${BASE}/create`, data }),
    update: (id: string, data: CronTaskPayload) => request.post({ url: `${BASE}/update`, data: { id, ...data } }),
    remove: (id: string) => request.post({ url: `${BASE}/delete/${id}` }),
    toggle: (id: string) => request.post({ url: `${BASE}/toggle/${id}` }),
    run: (id: string) => request.post<TaskRunResult[]>({ url: `${BASE}/run/${id}`, timeout: 120 * 1000 }),
    validateCron: (cron: string) => request.post({ url: `${BASE}/validate-cron`, data: { cron } }),
    migrate: () => request.post({ url: `${BASE}/migrate` }),
    migratePreview: () => request.post<MigrationPreview>({ url: `${BASE}/migrate-preview` }),
    migrateStatus: () => request.get<MigrationStatus>({ url: `${BASE}/migrate-status` }),
};

// ===== 宿主实例 API =====

export const instanceApi = {
    list: () => request.get<InstanceItem[]>({ url: '/api/instance/list' }),
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
