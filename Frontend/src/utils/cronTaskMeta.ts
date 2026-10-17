import { CodeIcon, PlayCircleIcon, RefreshIcon, StopCircleIcon } from 'tdesign-icons-vue-next';

// 任务类型元信息：主任务页与服务器设置页「定时任务绑定」共用，避免两处文案/颜色漂移
export const TASK_TYPE_LABELS: Record<string, string> = {
    command: '发送命令 (Command)',
    backup: '备份存档 (Backup)',
    start: '开启服务器 (Start)',
    stop: '停止服务器 (Stop)',
    restart: '重启服务器 (Restart)',
    shell: '执行脚本 (Shell Command)',
};

export const taskTypeColor = (type: string) => {
    const t = type.toLowerCase();
    if (t === 'start') return 'success';
    if (t === 'stop') return 'danger';
    if (t === 'restart') return 'warning';
    if (t === 'shell') return 'default';
    return 'primary';
};

export const taskTypeIcon = (type: string) => {
    const t = type.toLowerCase();
    if (t === 'start') return PlayCircleIcon;
    if (t === 'stop') return StopCircleIcon;
    if (t === 'restart') return RefreshIcon;
    return CodeIcon;
};

const pad = (n: number) => String(n).padStart(2, '0');

export const formatDateTime = (t?: string | null): string => {
    if (!t) return '—';
    const d = new Date(t);
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};
