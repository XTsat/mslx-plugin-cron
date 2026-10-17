<template>
    <div class="mx-auto flex flex-col gap-6 text-[var(--td-text-color-primary)] pb-5">
        <!-- 头部卡片 -->
        <div
            class="design-card flex flex-col sm:flex-row sm:items-center justify-between gap-4 p-5 bg-[var(--td-bg-color-container)]/80 rounded-2xl border border-[var(--td-component-border)] shadow-sm text-left"
        >
            <div class="flex flex-col gap-1 items-start">
                <h2 class="text-lg font-bold tracking-tight text-[var(--td-text-color-primary)] m-0">定时任务绑定</h2>
                <p class="text-sm text-[var(--td-text-color-secondary)] m-0">
                    一个定时任务，绑定多台服务器统一执行
                </p>
            </div>

            <div class="flex items-center gap-3">
                <t-button variant="outline" @click="backToHost">
                    <template #icon><rollback-icon /></template>
                    返回原版定时任务
                </t-button>
                <t-button v-if="isAdmin" variant="outline" @click="migrationVisible = true">
                    <template #icon><swap-icon /></template>
                    宿主任务迁移
                </t-button>
                <t-button variant="dashed" :loading="loading" @click="loadAll">
                    <template #icon><refresh-icon /></template>
                    刷新
                </t-button>
                <t-button theme="primary" @click="openCreate">
                    <template #icon><add-icon /></template>
                    新增任务
                </t-button>
            </div>
        </div>

        <!-- 任务列表 -->
        <div class="relative min-h-[300px]">
            <div v-if="loading && tasks.length === 0" class="flex justify-center items-center py-24">
                <t-loading text="加载数据中..." size="small"></t-loading>
            </div>

            <div
                v-else-if="tasks.length > 0"
                class="design-card bg-[var(--td-bg-color-container)]/80 rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5"
            >
                <t-table
                    row-key="id"
                    :data="tasks"
                    :columns="columns()"
                    size="small"
                    :hover="true"
                    :pagination="tasks.length > 8 ? { pageSize: 8 } : null"
                >
                    <template #empty>
                        <t-empty class="!bg-transparent" description="暂无任务" />
                    </template>
                </t-table>
            </div>

            <div
                v-else
                class="design-card flex flex-col items-center justify-center py-24 bg-white/40 dark:bg-zinc-800/40 rounded-2xl border-2 border-dashed border-[var(--td-component-border)]"
            >
                <t-empty class="!bg-transparent" description="尚未配置任何多服定时任务" />
            </div>
        </div>

        <!-- 宿主任务迁移二级面板：导入清单勾选 / 已导入回显 / 左下角还原 -->
        <host-task-migration-dialog
            v-model:visible="migrationVisible"
            :instances="instances"
            @changed="loadAll"
        />

        <!-- 新建/编辑弹窗 -->
        <t-dialog
            v-model:visible="dialogVisible"
            :header="editingId ? '编辑定时任务' : '新增定时任务'"
            width="600px"
            :confirm-btn="{ content: '保存配置', theme: 'primary', loading: saving }"
            placement="center"
            :on-confirm="save"
        >
            <!-- 复制任务：含无权限服务器的任务无法直接修改，借此创建无绑定副本后自行配置服务器 -->
            <div v-if="editingId" class="flex justify-end mb-1">
                <t-button variant="outline" theme="primary" size="small" :loading="copying" @click="doCopy">
                    <template #icon><copy-icon /></template>
                    复制任务
                </t-button>
            </div>
            <t-form :data="form" label-align="top" class="mt-4">
                <t-form-item v-if="editingId" label="任务 ID">
                    <div
                        class="flex items-center gap-2 bg-zinc-50 dark:bg-zinc-800/50 px-3 py-2 rounded-lg border border-[var(--td-component-border)] w-full"
                    >
                        <span class="font-mono text-xs text-[var(--td-text-color-secondary)] select-all">{{ editingId }}</span>
                    </div>
                </t-form-item>

                <t-form-item label="任务名称" name="name">
                    <t-input v-model="form.name" placeholder="请输入任务名称，例如：每日自动备份" />
                </t-form-item>

                <t-form-item label="执行操作类型" name="type">
                    <t-select v-model="form.type" :options="taskTypeOptions" />
                </t-form-item>

                <t-form-item label="触发规则 (Cron 表达式，秒级)" name="cron">
                    <t-input v-model="form.cron" placeholder="例如: 0 0 4 * * ?">
                        <template #suffix>
                            <t-button variant="text" theme="primary" size="small" class="!h-auto !py-1" @click="showCronGen = true">
                                生成器
                            </t-button>
                        </template>
                    </t-input>
                </t-form-item>

                <t-form-item
                    v-if="form.type === 'command' || form.type === 'restart' || form.type === 'shell'"
                    :label="
                        form.type === 'restart'
                            ? '重启提示语（系统将在重启前60秒自动广播预警并倒计时）'
                            : form.type === 'shell'
                              ? '执行宿主机 Shell 命令'
                              : '控制台执行命令'
                    "
                    name="payload"
                >
                    <t-textarea
                        v-model="form.payload"
                        :autosize="{ minRows: 2, maxRows: 5 }"
                        placeholder="请输入执行内容..."
                    />
                </t-form-item>

                <t-form-item label="绑定服务器（可多选）" name="instanceIds">
                    <t-checkbox-group v-model="form.instanceIds" class="flex flex-col gap-2 w-full">
                        <div
                            v-for="inst in instances"
                            :key="inst.id"
                            class="flex items-center gap-3 bg-zinc-50 dark:bg-zinc-800/50 p-3 rounded-xl border border-[var(--td-component-border)] w-full"
                        >
                            <t-checkbox :value="Number(inst.id)">
                                <span class="text-sm font-medium text-[var(--td-text-color-primary)]">{{ inst.name }}</span>
                                <span class="text-xs text-[var(--td-text-color-secondary)] ml-1.5">#{{ inst.id }}</span>
                            </t-checkbox>
                        </div>
                        <!-- 绑定中但已被删除的服务器：勾选组里没有对应选项，单独渲染才能取消勾选/移除 -->
                        <div
                            v-for="sid in staleBindingIds"
                            :key="'stale-' + sid"
                            class="flex items-center gap-3 p-3 rounded-xl border border-dashed border-[var(--td-error-color)]/50 bg-red-50/60 dark:bg-red-900/10 w-full"
                        >
                            <t-checkbox :value="Number(sid)">
                                <span class="text-sm font-medium text-[var(--td-error-color)]">#{{ sid }}（服务器已删除）</span>
                            </t-checkbox>
                            <span class="ml-auto">
                                <t-button variant="text" theme="danger" size="small" class="!px-1.5" @click="removeStaleBinding(Number(sid))">
                                    <template #icon><close-icon /></template>
                                    移除
                                </t-button>
                            </span>
                        </div>
                    </t-checkbox-group>
                    <div v-if="instances.length === 0" class="text-xs text-[var(--td-text-color-secondary)] mt-1">
                        未获取到服务器列表
                    </div>
                    <div v-if="staleBindingIds.length > 0" class="text-xs text-[var(--td-error-color)] mt-1">
                        该任务仍绑定 {{ staleBindingIds.length }} 台已删除的服务器，取消勾选或点「移除」即可清理
                    </div>
                </t-form-item>

                <t-form-item label="初始状态" name="enable">
                    <div class="flex flex-col gap-3 w-full">
                        <div
                            class="flex items-center gap-3 bg-zinc-50 dark:bg-zinc-800/50 p-3 rounded-xl border border-[var(--td-component-border)] w-full"
                        >
                            <t-switch v-model="form.enable" />
                            <span class="text-xs font-medium text-[var(--td-text-color-secondary)]">
                                {{ form.enable ? '保存后立即生效运行' : '保存后处于暂停状态' }}
                            </span>
                        </div>

                        <div
                            class="flex items-center gap-3 bg-zinc-50 dark:bg-zinc-800/50 p-3 rounded-xl border border-[var(--td-component-border)] w-full"
                        >
                            <t-switch v-model="form.runWhenOffline" />
                            <span class="text-xs font-medium text-[var(--td-text-color-secondary)]">
                                {{ form.runWhenOffline ? '实例未运行时，也会执行该任务' : '仅在实例运行时执行' }}
                            </span>
                        </div>
                    </div>
                </t-form-item>
            </t-form>
        </t-dialog>

        <!-- Cron 表达式生成器（与宿主同款） -->
        <cron-generator v-model:visible="showCronGen" :initial-value="form.cron" @confirm="onCronGenerated" />

        <!-- 快速创建提前提醒任务弹窗（与宿主同款） -->
        <t-dialog
            v-model:visible="showAdvanceModal"
            header="快速创建提前提醒任务"
            width="600px"
            :confirm-btn="{ content: '立即创建', theme: 'primary', loading: advanceLoading, disabled: !advanceForm.cron }"
            placement="center"
            @confirm="handleConfirmCreateAdvance"
        >
            <div v-if="advanceTargetTask" class="flex flex-col gap-4 mt-2">
                <div
                    class="p-3 bg-zinc-50 dark:bg-zinc-800/50 rounded-xl border border-[var(--td-component-border)] flex items-center justify-between text-xs"
                >
                    <span class="text-[var(--td-text-color-secondary)]">
                        关联任务: <b class="text-[var(--td-text-color-primary)]">{{ advanceTargetTask.name }}</b>
                    </span>
                    <span class="font-mono text-[var(--td-text-color-secondary)]">原 Cron: {{ advanceTargetTask.cron }}</span>
                </div>

                <div>
                    <div class="text-xs font-bold text-[var(--td-text-color-primary)] mb-1.5">提前提醒时间</div>
                    <t-radio-group v-model="advanceMinutes" variant="default-filled" @change="updateAdvanceFormPreview">
                        <t-radio-button :value="5">提前 5 分钟</t-radio-button>
                        <t-radio-button :value="10">提前 10 分钟</t-radio-button>
                        <t-radio-button :value="15">提前 15 分钟</t-radio-button>
                        <t-radio-button :value="30">提前 30 分钟</t-radio-button>
                    </t-radio-group>
                </div>

                <div>
                    <div class="text-xs font-bold text-[var(--td-text-color-primary)] mb-1.5">提醒通知样式</div>
                    <t-radio-group v-model="advanceTemplateType" variant="default-filled" @change="updateAdvanceFormPreview">
                        <t-radio-button value="say">控制台标准广播 (通用)</t-radio-button>
                        <t-radio-button value="tellraw">彩色聊天框 (仅 MC Java)</t-radio-button>
                        <t-radio-button value="title">全屏大标题 (仅 MC Java)</t-radio-button>
                    </t-radio-group>
                </div>

                <div>
                    <div class="text-xs font-bold text-[var(--td-text-color-primary)] mb-1.5">新任务名称</div>
                    <t-input v-model="advanceForm.name" placeholder="请输入任务名称" />
                </div>

                <div>
                    <div class="text-xs font-bold text-[var(--td-text-color-primary)] mb-1.5 flex items-center justify-between">
                        <span>定时 Cron 表达式</span>
                        <span v-if="!advanceForm.cron" class="text-amber-500 font-normal">当前规则无法自动推算，请手动填写</span>
                    </div>
                    <t-input v-model="advanceForm.cron" placeholder="例如: 0 50 3 * * ?" />
                </div>

                <div>
                    <div class="text-xs font-bold text-[var(--td-text-color-primary)] mb-1.5">通知执行指令</div>
                    <t-textarea
                        v-model="advanceForm.payload"
                        :autosize="{ minRows: 2, maxRows: 4 }"
                        placeholder="发送给服务器控制台的提醒指令"
                    />
                </div>
            </div>
        </t-dialog>
    </div>
</template>

<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue';
import { Button, DialogPlugin, MessagePlugin, Switch, Tag } from 'tdesign-vue-next';
import {
    AddIcon,
    CloseIcon,
    CopyIcon,
    DeleteIcon,
    EditIcon,
    NotificationIcon,
    PlayCircleIcon,
    RefreshIcon,
    RollbackIcon,
    ServerIcon,
    SwapIcon,
    TimeIcon,
} from 'tdesign-icons-vue-next';
import {
    cronTaskApi,
    instanceApi,
    type CronTaskDefinition,
    type CronTaskPayload,
    type InstanceItem,
} from '../api/pluginCron';
import CronGenerator from '../components/CronGenerator.vue';
import HostTaskMigrationDialog from '../components/HostTaskMigrationDialog.vue';
import { generateAdvanceNoticePayload, shiftCronMinutes } from '../utils/cronHelper';
import { TASK_TYPE_LABELS as TYPE_LABELS, taskTypeColor as getColorByType, taskTypeIcon as getIconByType, formatDateTime as formatTime } from '../utils/cronTaskMeta';

// ===== 常量 =====

const userStore = (window as any).MSLX_Stores?.getUserStore?.();
const isAdmin = computed(() => !!userStore?.isAdmin);

const taskTypeOptions = computed(() => {
    const options = Object.keys(TYPE_LABELS).map((t) => ({
        label: TYPE_LABELS[t],
        value: t,
    }));
    // 执行宿主机 Shell 脚本仅管理员可用（后端同样校验）
    if (!isAdmin.value) {
        return options.filter((o) => o.value !== 'shell');
    }
    return options;
});

// ===== 状态 =====

const loading = ref(false);
const tasks = ref<CronTaskDefinition[]>([]);
const instances = ref<InstanceItem[]>([]);
const migrationVisible = ref(false);

const dialogVisible = ref(false);
const editingId = ref<string | null>(null);
const saving = ref(false);
const copying = ref(false);

// Cron 表达式生成器
const showCronGen = ref(false);

// 提前提醒任务
const showAdvanceModal = ref(false);
const advanceLoading = ref(false);
const advanceTargetTask = ref<CronTaskDefinition | null>(null);
const advanceMinutes = ref(10);
const advanceTemplateType = ref<'title' | 'tellraw' | 'say'>('say');
const advanceForm = ref({
    name: '',
    cron: '',
    payload: '',
});

const form = reactive<CronTaskPayload>({
    name: '',
    cron: '0 0 4 * * ?',
    type: 'command',
    payload: '',
    enable: true,
    runWhenOffline: true,
    instanceIds: [],
});

// ===== 辅助 =====

const instanceName = (id: number): string => {
    const inst = instances.value.find((i) => Number(i.id) === id);
    return inst ? String(inst.name) : `#${id}`;
};

const instanceExists = (id: number): boolean => instances.value.some((i) => Number(i.id) === id);

// 编辑状态下任务仍绑定、但已不在服务器列表中的实例（服务器已被删除）。
// 勾选组只渲染现有服务器，这类绑定没有对应选项，必须单独渲染才能取消勾选/移除。
// 以 form.instanceIds 为准：用户取消勾选后行随即消失，与表单状态保持一致。
const staleBindingIds = computed<number[]>(() => {
    if (!editingId.value) return [];
    const task = tasks.value.find((t) => t.id === editingId.value);
    if (!task) return [];
    const boundIds = new Set(task.bindings.map((b) => b.instanceId));
    return [...boundIds].filter((id) => !instanceExists(id) && form.instanceIds.includes(id));
});

const removeStaleBinding = (id: number) => {
    form.instanceIds = form.instanceIds.filter((i) => i !== id);
};

// ===== 数据加载 =====

const loadAll = async () => {
    loading.value = true;
    try {
        const [taskList, instanceList] = await Promise.all([
            cronTaskApi.list(),
            instanceApi.list(),
        ]);
        tasks.value = taskList || [];
        instances.value = instanceList || [];
    } catch (e: any) {
        MessagePlugin.error('获取任务列表失败: ' + (e?.message || '未知错误'));
    } finally {
        loading.value = false;
    }
};

// ===== 返回原版定时任务 =====

const MODE_KEY = 'mslx-cronbind-mode';

const backToHost = () => {
    try {
        localStorage.setItem(MODE_KEY, 'host');
    } catch { /* ignore */ }
    window.location.href = '/instance/cron';
};

// ===== 新建 / 编辑 =====

const openCreate = () => {
    editingId.value = null;
    Object.assign(form, {
        name: '',
        cron: '0 0 4 * * ?',
        type: 'command',
        payload: '',
        enable: true,
        runWhenOffline: true,
        instanceIds: [],
    });
    dialogVisible.value = true;
};

const openEdit = (task: CronTaskDefinition) => {
    editingId.value = task.id;
    Object.assign(form, {
        name: task.name,
        cron: task.cron,
        type: task.type,
        payload: task.payload,
        enable: task.enable,
        runWhenOffline: task.runWhenOffline,
        instanceIds: task.bindings.map((b) => b.instanceId),
    });
    dialogVisible.value = true;
};

const save = async () => {
    if (!form.name.trim()) {
        MessagePlugin.warning('任务名称不能为空');
        return;
    }

    saving.value = true;
    try {
        await cronTaskApi.validateCron(form.cron);
    } catch (e: any) {
        saving.value = false;
        MessagePlugin.error('Cron 表达式无效: ' + (e?.message || e));
        return;
    }

    try {
        if (editingId.value) {
            await cronTaskApi.update(editingId.value, { ...form });
            MessagePlugin.success('更新成功');
        } else {
            await cronTaskApi.create({ ...form });
            MessagePlugin.success('创建成功');
        }
        dialogVisible.value = false;
        await loadAll();
    } catch (e: any) {
        MessagePlugin.error('保存失败: ' + (e?.message || e));
    } finally {
        saving.value = false;
    }
};

// 复制任务：含无权限服务器的共享任务普通用户无法直接修改，复制为无绑定副本后自行配置服务器
const doCopy = async () => {
    if (!editingId.value || copying.value) return;
    copying.value = true;
    try {
        const copied = await cronTaskApi.copy(editingId.value);
        MessagePlugin.success('已复制为无绑定服务器的新任务，请在副本上配置你有权限的服务器');
        await loadAll();
        // 不关闭弹窗，直接切到副本：副本无绑定，勾选自己有权限的服务器后保存即可
        openEdit(copied);
    } catch (e: any) {
        MessagePlugin.error('复制失败: ' + (e?.message || e));
    } finally {
        copying.value = false;
    }
};

// ===== 行操作 =====

const onCronGenerated = (cron: string) => {
    form.cron = cron;
};

// --- 提前提醒任务（与宿主 cronTasksManager 同款） ---

const handleOpenAdvance = (task: CronTaskDefinition) => {
    advanceTargetTask.value = task;
    advanceMinutes.value = 10;
    advanceTemplateType.value = 'say';
    updateAdvanceFormPreview();
    showAdvanceModal.value = true;
};

const updateAdvanceFormPreview = () => {
    if (!advanceTargetTask.value) return;
    const shifted = shiftCronMinutes(advanceTargetTask.value.cron, advanceMinutes.value);
    advanceForm.value.name = `[提前${advanceMinutes.value}分钟提醒] ${advanceTargetTask.value.name}`;
    // 算不出来就让用户自己填
    advanceForm.value.cron = shifted || '';
    advanceForm.value.payload = generateAdvanceNoticePayload(
        advanceTemplateType.value,
        advanceMinutes.value,
        advanceTargetTask.value.type,
        advanceTargetTask.value.name,
    );
};

const handleConfirmCreateAdvance = async () => {
    if (!advanceForm.value.name || !advanceForm.value.cron || !advanceTargetTask.value) {
        MessagePlugin.warning('任务名称与 Cron 规则不能为空');
        return;
    }
    if (advanceForm.value.cron.trim() === advanceTargetTask.value.cron.trim()) {
        MessagePlugin.warning('提醒任务的触发时间不能与原任务完全一致，请修改 Cron');
        return;
    }
    advanceLoading.value = true;
    try {
        // 提醒任务绑定原任务的有效服务器实例（过滤已删除的失效绑定，避免新任务继承僵尸绑定）
        await cronTaskApi.create({
            name: advanceForm.value.name,
            cron: advanceForm.value.cron,
            payload: advanceForm.value.payload,
            type: 'command',
            enable: true,
            runWhenOffline: true,
            instanceIds: advanceTargetTask.value.bindings
                .map((b) => b.instanceId)
                .filter((id) => instanceExists(id)),
        });
        MessagePlugin.success('提前提醒任务创建成功');
        showAdvanceModal.value = false;
        await loadAll();
    } catch (e: any) {
        MessagePlugin.error(e?.message || '创建提前提醒任务失败');
    } finally {
        advanceLoading.value = false;
    }
};

const doToggle = async (task: CronTaskDefinition) => {
    try {
        await cronTaskApi.toggle(task.id);
        task.enable = !task.enable;
        MessagePlugin.success(`任务 [${task.name}] 已${task.enable ? '启用' : '暂停'}`);
    } catch (e: any) {
        MessagePlugin.error('状态更新失败: ' + (e?.message || e));
        await loadAll();
    }
};

const doDelete = async (task: CronTaskDefinition) => {
    const confirmDlg = DialogPlugin.confirm({
        header: '确认删除',
        body: `确定要删除任务「${task.name}」吗？此操作不可恢复。`,
        confirmBtn: '删除',
        theme: 'danger',
        cancelBtn: '取消',
        onConfirm: async () => {
            confirmDlg.destroy();
            try {
                await cronTaskApi.remove(task.id);
                MessagePlugin.success('删除成功');
                await loadAll();
            } catch (e: any) {
                MessagePlugin.error('删除失败: ' + (e?.message || e));
            }
        },
    });
};

const doRun = async (task: CronTaskDefinition) => {
    const confirmDlg = DialogPlugin.confirm({
        header: '立即执行',
        body: `将对「${task.name}」绑定的 ${task.bindings.length} 台服务器立即执行一次（重启类任务耗时约 1-2 分钟），是否继续？`,
        confirmBtn: '立即执行',
        cancelBtn: '取消',
        onConfirm: async () => {
            confirmDlg.destroy();
            const loadingDlg = DialogPlugin({
                header: '执行中',
                body: '正在执行，请稍候...',
                closeBtn: false,
                footer: false,
            });
            try {
                const results: any = (await cronTaskApi.run(task.id)) || [];
                loadingDlg.destroy();
                const lines = results
                    .map(
                        (r: any) =>
                            `#${r.instanceId} ${instanceName(r.instanceId)}：${r.success ? '✅' : '❌'} ${r.message}`,
                    )
                    .join('\n');
                DialogPlugin({
                    header: '执行结果',
                    body: () => h('div', { style: 'white-space: pre-line; line-height: 1.7; max-height: 300px; overflow: auto;' }, lines),
                    confirmBtn: '知道了',
                });
            } catch (e: any) {
                loadingDlg.destroy();
                MessagePlugin.error('执行失败: ' + (e?.message || e));
            }
        },
    });
};

// ===== 表格列 =====

const columns = () => [
    {
        colKey: 'name',
        title: '任务名称',
        cell: (h: any, { row }: any) =>
            h('div', { class: 'flex items-center gap-2' }, [
                h(TimeIcon, { class: 'text-[var(--color-primary)] opacity-90 shrink-0' }),
                h('span', { class: 'font-medium text-[var(--td-text-color-primary)] truncate', attrs: { title: row.name } }, row.name),
            ]),
    },
    {
        colKey: 'type',
        title: '类型',
        width: 150,
        cell: (h: any, { row }: any) =>
            h(
                Tag,
                {
                    size: 'small',
                    variant: 'light',
                    theme: getColorByType(row.type),
                    class: '!rounded-md !px-2 font-medium !inline-flex !items-center !justify-center !w-fit',
                },
                {
                    icon: () => h(getIconByType(row.type), { class: 'opacity-80 shrink-0' }),
                    default: () => ' ' + String(row.type).toUpperCase(),
                },
            ),
    },
    {
        colKey: 'cron',
        title: 'Cron 规则',
        width: 140,
        cell: (h: any, { row }: any) =>
            h(
                'span',
                {
                    class: 'font-mono text-xs text-[var(--td-text-color-secondary)] bg-zinc-100 dark:bg-zinc-900 px-2.5 py-1 rounded-md border border-[var(--td-component-border)]',
                },
                row.cron,
            ),
    },
    {
        colKey: 'payload',
        title: '执行参数',
        ellipsis: true,
        cell: (h: any, { row }: any) => {
            // 直接显示执行参数原始值；无参数的任务（如备份）留空，不做占位文案
            const text = row.payload || '';
            return h('span', { attrs: { title: text }, class: 'truncate block max-w-[280px]' }, text);
        },
    },
    {
        colKey: 'bindings',
        title: '绑定服务器',
        cell: (h: any, { row }: any) => {
            if (!row.bindings?.length) {
                return h('span', { class: 'text-[var(--td-text-color-placeholder)]' }, '未绑定');
            }
            return h('div', { class: 'flex flex-wrap gap-1' }, row.bindings.map((b: any) => {
                const gone = !instanceExists(b.instanceId);
                return h(
                    Tag,
                    {
                        key: b.instanceId,
                        size: 'small',
                        variant: 'light',
                        theme: gone ? 'warning' : 'primary',
                        class: '!rounded-md !px-2',
                        title: `上次执行: ${formatTime(b.lastRunTime)}`,
                    },
                    {
                        icon: () => h(ServerIcon, { class: 'opacity-80 shrink-0' }),
                        default: () => ` ${instanceName(b.instanceId)}${gone ? '（已删除）' : ''}`,
                    },
                );
            }));
        },
    },
    {
        colKey: 'enable',
        title: '状态',
        width: 70,
        cell: (h: any, { row }: any) =>
            h(Switch, { modelValue: row.enable, size: 'small', onChange: () => doToggle(row) }),
    },
    {
        colKey: 'op',
        title: '操作',
        width: 300,
        cell: (h: any, { row }: any) => {
            const btns = [
                h(
                    Button,
                    { theme: 'primary', variant: 'text', size: 'small', class: '!px-1.5 hover:!bg-[var(--color-primary)]/10 shrink-0', onClick: () => doRun(row) },
                    { icon: () => h(PlayCircleIcon, { class: 'shrink-0' }), default: () => ' 执行' },
                ),
            ];
            if (['stop', 'restart'].includes(String(row.type).toLowerCase())) {
                btns.push(
                    h(
                        Button,
                        { theme: 'primary', variant: 'text', size: 'small', class: '!px-1.5 hover:!bg-[var(--color-primary)]/10 shrink-0', onClick: () => handleOpenAdvance(row) },
                        { icon: () => h(NotificationIcon, { class: 'shrink-0' }), default: () => ' 提醒' },
                    ),
                );
            }
            btns.push(
                h(
                    Button,
                    { theme: 'primary', variant: 'text', size: 'small', class: '!px-1.5 hover:!bg-[var(--color-primary)]/10 shrink-0', onClick: () => openEdit(row) },
                    { icon: () => h(EditIcon, { class: 'shrink-0' }), default: () => ' 编辑' },
                ),
                h(
                    Button,
                    { theme: 'danger', variant: 'text', size: 'small', class: '!px-1.5 hover:!bg-red-500/10 shrink-0', onClick: () => doDelete(row) },
                    { icon: () => h(DeleteIcon, { class: 'shrink-0' }), default: () => ' 删除' },
                ),
            );
            return h('div', { class: 'flex items-center gap-1.5 shrink-0' }, btns);
        },
    },
];

// ===== 初始化 =====

onMounted(async () => {
    await loadAll();
});
</script>