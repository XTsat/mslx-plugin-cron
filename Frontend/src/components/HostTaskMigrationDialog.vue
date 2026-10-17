<template>
    <t-dialog
        v-model:visible="visible"
        header="宿主任务迁移"
        width="min(960px, 92vw)"
        placement="center"
        attach="body"
        :close-on-overlay-click="false"
    >
        <div class="flex flex-col gap-4 text-[var(--td-text-color-primary)]">
            <div class="flex flex-col gap-1">
                <div class="flex items-center gap-2">
                    <div class="w-1.5 h-5 bg-[var(--color-primary)] rounded-full shadow-[0_0_8px_var(--color-primary-light)] opacity-90"></div>
                    <p class="text-sm font-semibold m-0">选择要导入的宿主任务</p>
                </div>
                <p class="text-xs text-[var(--td-text-color-secondary)] m-0">
                    导入后宿主任务会被禁用、由本插件接管调度；已导入的任务只作状态回显，不会重复导入。
                </p>
            </div>

            <div class="grid grid-cols-3 gap-2 sm:gap-3">
                <div
                    class="flex flex-col gap-0.5 px-3 py-2.5 rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)]"
                >
                    <span class="text-lg font-semibold leading-tight">{{ totalCount }}</span>
                    <span class="text-xs text-[var(--td-text-color-secondary)]">宿主任务</span>
                </div>
                <div
                    class="flex flex-col gap-0.5 px-3 py-2.5 rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)]"
                >
                    <span class="text-lg font-semibold leading-tight text-[var(--color-primary)]">{{ pendingCount }}</span>
                    <span class="text-xs text-[var(--td-text-color-secondary)]">待导入</span>
                </div>
                <div
                    class="flex flex-col gap-0.5 px-3 py-2.5 rounded-xl border border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)]"
                >
                    <span class="text-lg font-semibold leading-tight text-[var(--td-success-color)]">{{ importedCount }}</span>
                    <span class="text-xs text-[var(--td-text-color-secondary)]">已导入</span>
                </div>
            </div>

            <div v-if="loading" class="flex justify-center items-center py-16">
                <t-loading text="正在读取宿主任务..." size="small"></t-loading>
            </div>

            <div
                v-else-if="items.length === 0"
                class="flex flex-col items-center justify-center py-14 border-2 border-dashed border-[var(--td-component-border)] rounded-2xl"
            >
                <t-empty class="!bg-transparent" :description="emptyReason" />
            </div>

            <template v-else>
                <div class="flex flex-wrap items-center justify-between gap-2">
                    <t-checkbox
                        :checked="allPendingSelected"
                        :indeterminate="somePendingSelected && !allPendingSelected"
                        :disabled="pendingCount === 0"
                        @change="toggleAllPending"
                    >
                        全选待导入任务
                    </t-checkbox>
                    <span class="text-xs text-[var(--td-text-color-secondary)]">
                        已选 {{ pendingSelectedCount }} / {{ pendingCount }} 个待导入任务
                    </span>
                </div>

                <t-checkbox-group
                    v-model="selectedIds"
                    class="flex flex-col flex-nowrap gap-2 max-h-[44vh] sm:max-h-[50vh] overflow-y-auto pr-1"
                >
                    <div
                        v-for="item in items"
                        :key="item.hostTaskId"
                        class="flex items-start gap-3 p-3 rounded-xl border transition-colors"
                        :class="
                            item.imported
                                ? 'border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)] opacity-80'
                                : 'border-[var(--td-component-border)] bg-[var(--td-bg-color-secondarycontainer)] hover:border-[var(--color-primary)] cursor-pointer'
                        "
                        @click="toggleItem(item)"
                    >
                        <t-checkbox
                            :value="item.hostTaskId"
                            :disabled="item.imported"
                            class="mt-0.5 shrink-0 pointer-events-none"
                        />
                        <div class="flex flex-col gap-1.5 flex-1 min-w-0">
                            <div class="flex flex-wrap items-center gap-2">
                                <span class="text-sm font-semibold truncate max-w-full">{{ item.name }}</span>
                                <t-tag size="small" variant="light" :theme="taskTypeColor(item.type)">
                                    <template #icon>
                                        <component :is="taskTypeIcon(item.type)" />
                                    </template>
                                    {{ String(item.type).toUpperCase() }}
                                </t-tag>
                                <t-tag v-if="item.imported" size="small" variant="light" theme="success">
                                    <template #icon><check-circle-icon /></template>
                                    已导入
                                </t-tag>
                                <t-tag v-else-if="item.merged" size="small" variant="light" theme="primary">
                                    并入「{{ item.targetName }}」
                                </t-tag>
                                <t-tag v-else size="small" variant="light" theme="warning">新建多服任务</t-tag>
                                <t-tag v-if="!item.imported && !item.originalEnable" size="small" variant="light" theme="default">
                                    导入后暂停
                                </t-tag>
                            </div>

                            <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[var(--td-text-color-secondary)]">
                                <span class="font-mono">{{ item.cron }}</span>
                                <span class="inline-flex items-center gap-1">
                                    <server-icon class="text-[var(--color-primary)] opacity-80" />
                                    {{ instanceName(item.instanceId) }}
                                </span>
                                <span v-if="item.payload" class="truncate max-w-full" :title="item.payload">
                                    执行参数：{{ item.payload }}
                                </span>
                                <span v-else class="text-[var(--td-text-color-placeholder)]">无执行参数</span>
                            </div>
                        </div>
                    </div>
                </t-checkbox-group>
            </template>
        </div>

        <template #footer>
            <div class="flex !flex-col sm:!flex-row !items-stretch sm:!items-center !justify-between gap-3 w-full">
                <div class="flex flex-col sm:flex-row !items-start sm:items-center gap-2 w-full sm:w-auto">
                    <t-button
                        theme="warning"
                        variant="outline"
                        :loading="restoring"
                        :disabled="restorableCount === 0 || loading || importing"
                        @click="doRestore"
                    >
                        <template #icon><rollback-icon /></template>
                        还原任务
                    </t-button>
                </div>
                <div class="flex items-center justify-end gap-2 w-full sm:w-auto">
                    <t-button variant="outline" :disabled="importing || restoring" @click="close">取消</t-button>
                    <t-button
                        theme="primary"
                        :loading="importing"
                        :disabled="loading || pendingSelectedCount === 0 || restoring"
                        @click="doImport"
                    >
                        <template #icon><swap-icon /></template>
                        确认导入{{ pendingSelectedCount > 0 ? `（${pendingSelectedCount}）` : '' }}
                    </t-button>
                </div>
            </div>
        </template>
    </t-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { DialogPlugin, MessagePlugin } from 'tdesign-vue-next';
import { CheckCircleIcon, RollbackIcon, ServerIcon, SwapIcon } from 'tdesign-icons-vue-next';
import {
    cronTaskApi,
    type InstanceItem,
    type MigrationPreviewItem,
} from '../api/pluginCron';
import { taskTypeColor, taskTypeIcon } from '../utils/cronTaskMeta';

const props = defineProps<{ visible: boolean; instances: InstanceItem[] }>();
const emit = defineEmits<{ (e: 'update:visible', value: boolean): void; (e: 'changed'): void }>();

const visible = computed({
    get: () => props.visible,
    set: (value: boolean) => emit('update:visible', value),
});

const loading = ref(false);
const importing = ref(false);
const restoring = ref(false);
const items = ref<MigrationPreviewItem[]>([]);
const selectedIds = ref<string[]>([]);
const restorableCount = ref(0);
const emptyReason = ref('宿主没有定时任务可导入');

const totalCount = computed(() => items.value.length);
const pendingCount = computed(() => items.value.filter((item) => !item.imported).length);
const importedCount = computed(() => items.value.filter((item) => item.imported).length);
const pendingIds = computed(() => items.value.filter((item) => !item.imported).map((item) => item.hostTaskId));
const importedIdSet = computed(() => new Set(items.value.filter((item) => item.imported).map((item) => item.hostTaskId)));
const pendingSelectedCount = computed(
    () => selectedIds.value.filter((id) => !importedIdSet.value.has(id)).length,
);
const allPendingSelected = computed(
    () => pendingIds.value.length > 0 && pendingIds.value.every((id) => selectedIds.value.includes(id)),
);
const somePendingSelected = computed(() => selectedIds.value.some((id) => !importedIdSet.value.has(id)));

const instanceName = (id: number): string => {
    const inst = props.instances.find((item) => Number(item.id) === id);
    return inst ? String(inst.name) : `#${id}`;
};

// 勾选状态初始化：待导入默认全选（保持面板一打开即可确认导入的旧体验），已导入固定选中
const resetSelection = () => {
    selectedIds.value = items.value.map((item) => item.hostTaskId);
};

const loadPreview = async () => {
    loading.value = true;
    // 先清空上一次的清单与勾选，避免加载期间用旧数据触发导入/还原
    items.value = [];
    selectedIds.value = [];
    restorableCount.value = 0;
    try {
        const preview = await cronTaskApi.migratePreview();
        if (preview?.skipped) {
            restorableCount.value = preview?.restorableCount ?? 0;
            emptyReason.value = preview.reason || '宿主没有定时任务可导入';
            return;
        }
        items.value = preview?.items || [];
        restorableCount.value = preview?.restorableCount ?? 0;
        emptyReason.value = '宿主没有定时任务可导入';
        resetSelection();
    } catch (e: any) {
        emptyReason.value = '读取宿主任务失败';
        MessagePlugin.error('读取宿主任务失败: ' + (e?.message || e));
    } finally {
        loading.value = false;
    }
};

const toggleItem = (item: MigrationPreviewItem) => {
    if (item.imported) return;
    selectedIds.value = selectedIds.value.includes(item.hostTaskId)
        ? selectedIds.value.filter((id) => id !== item.hostTaskId)
        : [...selectedIds.value, item.hostTaskId];
};

const toggleAllPending = () => {
    const importedIds = items.value.filter((item) => item.imported).map((item) => item.hostTaskId);
    const allIds = pendingIds.value;
    const pendingAllSelected = allIds.every((id) => selectedIds.value.includes(id));
    selectedIds.value = pendingAllSelected ? importedIds : [...importedIds, ...allIds];
};

const close = () => {
    visible.value = false;
};

const doImport = async () => {
    if (importing.value || restoring.value || loading.value) return;
    const ids = selectedIds.value.filter((id) => !importedIdSet.value.has(id));
    if (ids.length === 0) {
        MessagePlugin.warning('请选择要导入的宿主任务');
        return;
    }

    importing.value = true;
    try {
        const result = await cronTaskApi.migrate(ids);
        if (result?.skipped) {
            MessagePlugin.info(result.reason || '没有需要导入的宿主任务');
        } else if (result?.failedCount > 0) {
            MessagePlugin.warning(
                `已导入 ${result.migratedCount} 个宿主任务，${result.failedCount} 个禁用失败，可稍后再次尝试`,
            );
        } else {
            MessagePlugin.success(`已导入 ${result?.migratedCount ?? ids.length} 个宿主任务`);
        }
        emit('changed');
        // 重新读取：新导入的任务转为「已导入」置后展示，选中状态随之刷新
        await loadPreview();
    } catch (e: any) {
        MessagePlugin.error('导入失败: ' + (e?.message || e));
    } finally {
        importing.value = false;
    }
};

const doRestore = () => {
    if (restoring.value || importing.value || loading.value) return;
    const confirmDlg = DialogPlugin.confirm({
        header: '还原宿主任务',
        body: '将按迁移记录把之前禁用的宿主「定时任务」恢复为原有启用状态，并停止插件调度、清空所有多服任务的服务器绑定。\n\n任务定义会保留，但不再有任何服务器绑定可执行；绑定列表和最后执行时间不可恢复。\n\n请仅在卸载插件或计划不再使用本插件时还原。还原后迁移记录保留，已导入任务不会再次出现在待导入清单中。',
        theme: 'warning',
        confirmBtn: '确认还原',
        cancelBtn: '取消',
        onConfirm: async () => {
            confirmDlg.destroy();
            restoring.value = true;
            try {
                await cronTaskApi.migrateRestore();
                MessagePlugin.success('已还原宿主任务，插件调度已停止，服务器绑定已清空');
                emit('changed');
                visible.value = false;
            } catch (e: any) {
                MessagePlugin.error('还原失败: ' + (e?.message || e));
            } finally {
                restoring.value = false;
            }
        },
    });
};

watch(
    () => props.visible,
    (value) => {
        if (value) {
            void loadPreview();
        }
    },
);
</script>
