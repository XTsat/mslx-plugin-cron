<template>
    <div class="flex flex-col gap-4 mx-auto w-full pb-8 text-[var(--td-text-color-primary)]">
        <!-- 标题栏：实例设置弹窗内的小节标题，扁平流式布局，不再嵌套卡片 -->
        <div class="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div class="flex flex-col gap-1">
                <div class="flex items-center gap-2">
                    <div class="w-1 h-4 bg-[var(--color-primary)] rounded-full opacity-90"></div>
                    <h3 class="text-base font-bold tracking-tight m-0">定时任务绑定</h3>
                </div>
                <p class="text-xs text-[var(--td-text-color-secondary)] m-0 pl-3">
                    展示所有可用的多服定时任务；可把本服务器加入任务绑定，或取消本服务器已有的绑定
                </p>
            </div>

            <div class="flex items-center gap-2 flex-wrap">
                <t-tag v-if="sid > 0" size="small" variant="light" theme="primary">
                    <template #icon><server-icon /></template>
                    当前服务器：{{ currentInstanceName }}
                </t-tag>
                <t-button variant="outline" size="small" :loading="loading" @click="load">
                    <template #icon><refresh-icon /></template>
                    刷新
                </t-button>
            </div>
        </div>

        <div
            v-if="sid <= 0"
            class="flex items-center justify-center py-12 text-sm text-[var(--td-text-color-secondary)] border border-dashed border-[var(--td-component-border)] rounded-xl"
        >
            未获取到当前服务器信息
        </div>

        <div v-else-if="loading && tasks.length === 0" class="flex justify-center items-center py-16">
            <t-loading text="加载定时任务中..." size="small"></t-loading>
        </div>

        <div
            v-else-if="tasks.length === 0"
            class="flex flex-col items-center justify-center py-16 border-2 border-dashed border-[var(--td-component-border)] rounded-2xl"
        >
            <t-empty class="!bg-transparent" description="暂无可用定时任务，请先在「定时任务」页面创建" />
        </div>

        <div v-else class="flex flex-col gap-3">
            <div class="text-xs text-[var(--td-text-color-secondary)]">
                共 {{ tasks.length }} 个可用任务，本服务器已绑定 {{ boundCount }} 个
            </div>

            <div
                v-for="task in tasks"
                :key="task.id"
                class="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 p-4 rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)]"
            >
                <div class="flex flex-col gap-2 flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                        <span class="text-sm font-semibold truncate max-w-full">{{ task.name }}</span>
                        <t-tag size="small" variant="light" :theme="taskTypeColor(task.type)">
                            <template #icon>
                                <component :is="taskTypeIcon(task.type)" />
                            </template>
                            {{ String(task.type).toUpperCase() }}
                        </t-tag>
                        <t-tag v-if="isBound(task)" size="small" variant="light" theme="success">
                            <template #icon><check-circle-icon /></template>
                            本服务器已绑定
                        </t-tag>
                        <t-tag v-if="!task.enable" size="small" variant="light" theme="default">已暂停</t-tag>
                        <t-tag v-if="isShell(task) && !isAdmin" size="small" variant="light" theme="warning">
                            shell 任务仅管理员可绑定
                        </t-tag>
                    </div>

                    <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[var(--td-text-color-secondary)]">
                        <span class="font-mono">{{ task.cron }}</span>
                        <span>绑定 {{ (task.bindings || []).length }} 台服务器</span>
                        <span v-if="task.payload" class="truncate max-w-full" :title="task.payload">
                            执行参数：{{ task.payload }}
                        </span>
                    </div>

                    <div v-if="(task.bindings || []).length" class="flex flex-wrap gap-1">
                        <t-tag
                            v-for="binding in task.bindings"
                            :key="binding.instanceId"
                            size="small"
                            variant="outline"
                            :theme="Number(binding.instanceId) === sid ? 'primary' : 'default'"
                            :title="`上次执行：${formatDateTime(binding.lastRunTime)}`"
                        >
                            <template #icon><server-icon /></template>
                            {{ instanceName(Number(binding.instanceId)) }}
                            <span v-if="Number(binding.instanceId) === sid">（本服务器）</span>
                        </t-tag>
                    </div>
                </div>

                <div class="flex items-center gap-2 shrink-0 self-end sm:self-center">
                    <t-button
                        v-if="!isBound(task)"
                        theme="primary"
                        size="small"
                        :disabled="!canBind(task)"
                        :loading="busyKey === task.id + ':bind'"
                        :title="canBind(task) ? '将本服务器添加到该任务的绑定' : '仅管理员可将服务器绑定到 shell 类型任务'"
                        @click="bind(task)"
                    >
                        <template #icon><add-icon /></template>
                        绑定
                    </t-button>
                    <t-button
                        v-else
                        theme="danger"
                        variant="outline"
                        size="small"
                        :loading="busyKey === task.id + ':unbind'"
                        title="取消本服务器与该任务的绑定，任务本身和其它服务器不受影响"
                        @click="unbind(task)"
                    >
                        <template #icon><delete-icon /></template>
                        解绑
                    </t-button>
                </div>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { DialogPlugin, MessagePlugin } from 'tdesign-vue-next';
import { AddIcon, CheckCircleIcon, DeleteIcon, RefreshIcon, ServerIcon } from 'tdesign-icons-vue-next';
import { cronTaskApi, instanceApi, type CronTaskDefinition, type InstanceItem } from '../api/pluginCron';
import { formatDateTime, taskTypeColor, taskTypeIcon } from '../utils/cronTaskMeta';

const props = defineProps<{ serverId?: number; instanceId?: number }>();
const emit = defineEmits<{ (e: 'saved'): void }>();

const userStore = (window as any).MSLX_Stores?.getUserStore?.();
const isAdmin = computed(() => !!userStore?.isAdmin);

// 宿主同时传 serverId / instanceId（数值相同）；取任一非空值，兼容不同宿主版本
const sid = computed(() => Number(props.instanceId ?? props.serverId ?? 0));

const loading = ref(false);
const tasks = ref<CronTaskDefinition[]>([]);
const instances = ref<InstanceItem[]>([]);
// 正在请求的任务操作标识（`{taskId}:{bind|unbind}`），避免同一行重复提交
const busyKey = ref('');

const instanceName = (id: number): string => {
    const inst = instances.value.find((i) => Number(i.id) === id);
    return inst ? String(inst.name) : `#${id}`;
};

const currentInstanceName = computed(() => instanceName(sid.value));

const isShell = (task: CronTaskDefinition) => String(task.type).toLowerCase() === 'shell';
const isBound = (task: CronTaskDefinition) => (task.bindings || []).some((b) => Number(b.instanceId) === sid.value);
// shell 任务会执行宿主机命令，普通用户只能取消绑定、不能新增执行服务器（后端同样校验）
const canBind = (task: CronTaskDefinition) => isAdmin.value || !isShell(task);
const boundCount = computed(() => tasks.value.filter((task) => isBound(task)).length);

const load = async () => {
    if (sid.value <= 0) return;
    loading.value = true;
    try {
        const [taskList, instanceList] = await Promise.all([cronTaskApi.list(), instanceApi.list()]);
        tasks.value = taskList || [];
        instances.value = instanceList || [];
    } catch (e: any) {
        MessagePlugin.error('获取定时任务失败: ' + (e?.message || e));
    } finally {
        loading.value = false;
    }
};

const bind = async (task: CronTaskDefinition) => {
    if (sid.value <= 0 || busyKey.value) return;
    const key = `${task.id}:bind`;
    busyKey.value = key;
    try {
        await cronTaskApi.bind(task.id, sid.value);
        MessagePlugin.success(`已将「${currentInstanceName.value}」添加到任务「${task.name}」`);
        await load();
        emit('saved');
    } catch (e: any) {
        MessagePlugin.error('添加绑定失败: ' + (e?.message || e));
    } finally {
        if (busyKey.value === key) busyKey.value = '';
    }
};

const unbind = (task: CronTaskDefinition) => {
    const confirmDlg = DialogPlugin.confirm({
        header: '取消本服务器绑定',
        body: `确定要从任务「${task.name}」中移除服务器「${currentInstanceName.value}」吗？\n\n只取消本服务器的绑定，任务本身和其它服务器不受影响；若这是最后一个绑定，任务将暂停调度但不会被删除。`,
        theme: 'danger',
        confirmBtn: '解绑',
        cancelBtn: '返回',
        onConfirm: async () => {
            confirmDlg.destroy();
            if (busyKey.value) return;
            const key = `${task.id}:unbind`;
            busyKey.value = key;
            try {
                await cronTaskApi.unbind(task.id, sid.value);
                MessagePlugin.success(`已取消服务器「${currentInstanceName.value}」在任务「${task.name}」上的绑定`);
                await load();
                emit('saved');
            } catch (e: any) {
                MessagePlugin.error('取消绑定失败: ' + (e?.message || e));
            } finally {
                if (busyKey.value === key) busyKey.value = '';
            }
        },
    });
};

// 实例设置弹窗切换服务器时组件可能被复用，监听 serverId 变化重新加载
watch(
    sid,
    (value) => {
        if (value > 0) void load();
    },
    { immediate: true },
);
</script>

