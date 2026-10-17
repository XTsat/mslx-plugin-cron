import { h, render } from 'vue';
import { Button } from 'tdesign-vue-next';
import { TimeIcon } from 'tdesign-icons-vue-next';
import TaskListPage from './views/TaskListPage.vue';
import InstanceCronBindTab from './views/InstanceCronBindTab.vue';

export const pluginConfig = {
    name: 'CronBind',
    version: '1.0.21',

    // 注册隐藏路由承接「定时任务」入口；菜单仍显示宿主原「定时任务」项，
    // 进入 /instance/cron 时由下方重定向逻辑无缝跳转到本页（URL 不变语义，无 DOM 接管、不闪烁）。
    routes: [
        {
            parentName: 'instance',
            path: 'cronbind',
            name: 'CronBind',
            component: TaskListPage,
            meta: {
                title: '定时任务绑定',
                icon: 'time',
                roleCode: ['admin', 'user'],
                hidden: true,
                activeMenu: '/instance/cron', // 左侧菜单仍高亮「定时任务」
            },
        },
    ],
    // 服务器设置弹窗左侧新增「定时任务绑定」Tab：以服务器视角展示全部可用多服任务，
    // 支持把本服务器加入任务绑定或取消绑定（反向绑定入口）
    extensions: [
        {
            slot: 'instance-settings-tab',
            component: InstanceCronBindTab,
            label: '定时任务绑定',
            icon: TimeIcon,
        },
    ],
};

// ===== 接管 /instance/cron =====
// 宿主路由先注册、同 path 无法被插件覆盖（先注册者先匹配），且宿主未暴露 router 实例，
// 因此不 DOM 接管（会与宿主响应式渲染竞争导致闪烁），而是在宿主 cron 页面渲染完成前
// 极快地 replace 到插件页面。interval 100ms 保证在宿主懒加载 chunk 完成前完成跳转。
// 模式开关 localStorage['mslx-cronbind-mode']：'host' = 保持宿主原版页（增强页按钮切入），
// 其余值 = 接管（默认）。切到 host 后原版页会注入「定时任务绑定」按钮再切回来。
const MODE_KEY = 'mslx-cronbind-mode';
const MODE_HOST = 'host';
const ACTION_ENHANCED = 'enhanced';

const getMode = () => {
    try {
        return localStorage.getItem(MODE_KEY) || ''; // '' 为空则默认接管
    } catch {
        return '';
    }
};

let lastSeenPath = '';
const tryTick = () => {
    const path = window.location.pathname;
    if (path !== '/instance/cron') {
        lastSeenPath = path;
        return;
    }
    // host 模式：停留原版页并确保「回到增强版」按钮存在（early-return 之前判断，
    // 否则刷新/直入原版页时 lastSeenPath 已等于当前路径，注入永远不会执行）
    if (getMode() === MODE_HOST) {
        lastSeenPath = path;
        ensureBackButton();
        return;
    }
    if (path === lastSeenPath) return;
    lastSeenPath = path;
    window.location.replace('/instance/cronbind');
};

// 宿主原版 cron 页头卡右侧注入「定时任务绑定」按钮（仅注入按钮，不接管内容，无闪烁）。
// 用宿主同款 TDesign TButton 渲染（vue/tdesign-vue-next 由宿主 importmap 提供），
// 视觉与原版按钮完全一致：实心主色、带图标。
let backButtonTried = false;
const ensureBackButton = () => {
    if (backButtonTried) return;
    if (document.querySelector('[data-cronbind-back]')) {
        backButtonTried = true;
        return;
    }
    const h2 = Array.from(document.querySelectorAll('h2')).find((el) => el.textContent?.includes('定时任务'));
    if (!h2) return;
    const headCard = h2.closest('.design-card') || h2.parentElement;
    if (!headCard) return;
    const container = headCard.querySelector('.flex.items-center.gap-3') as HTMLElement | null;
    if (!container) return;

    const host = document.createElement('span');
    host.setAttribute('data-cronbind-back', '1');
    host.style.display = 'inline-flex';
    render(
        h(
            Button,
            {
                theme: 'primary',
                onClick: () => {
                    try {
                        localStorage.setItem(MODE_KEY, ACTION_ENHANCED);
                    } catch { /* ignore */ }
                    window.location.href = '/instance/cronbind';
                },
            },
            {
                icon: () => h(TimeIcon),
                default: () => '定时任务绑定',
            },
        ),
        host,
    );
    container.appendChild(host);
};

// ===== 实例设置 Tab 排序 =====
// 宿主把插件扩展 Tab 统一追加在内置 Tab 之后，而「定时任务绑定」需要紧跟宿主「定时任务」。
// 扩展注册不支持排序，因此设置弹窗渲染后把本插件的 Tab 节点移到「定时任务」后面；
// 只移动本插件自己的节点，宿主按索引切换面板，与 DOM 顺序无关，不影响其点击逻辑。
const reorderInstanceSettingsTab = () => {
    document.querySelectorAll('.settings-dialog').forEach((dialog) => {
        const bindTab = Array.from(dialog.querySelectorAll('.cursor-pointer')).find(
            (el) => (el.textContent?.trim() || '') === '定时任务绑定',
        );
        if (!bindTab) return;

        const list = bindTab.parentElement;
        if (!list) return;

        const hostCronTab = Array.from(list.children).find(
            (el) => (el.textContent?.trim() || '') === '定时任务',
        );
        if (!hostCronTab || hostCronTab.nextElementSibling === bindTab) return;

        list.insertBefore(bindTab, hostCronTab.nextSibling);
    });
};

if (typeof window !== 'undefined') {
    lastSeenPath = window.location.pathname;
    window.setInterval(tryTick, 100);

    // 设置弹窗按需插入 DOM（也可能被 Vue 复用/重排），用 MutationObserver 在渲染后立即纠正顺序
    if (typeof MutationObserver !== 'undefined') {
        let reorderScheduled = false;
        const scheduleTabReorder = () => {
            if (reorderScheduled) return;
            reorderScheduled = true;
            window.requestAnimationFrame(() => {
                reorderScheduled = false;
                reorderInstanceSettingsTab();
            });
        };
        new MutationObserver(scheduleTabReorder).observe(document.body, { childList: true, subtree: true });
        scheduleTabReorder();
    }

    // 刷新时若锚点在 /instance/cron 也立即接管（等路由就绪后 replace）
    if (window.location.pathname === '/instance/cron' && getMode() !== MODE_HOST) {
        window.setTimeout(() => {
            if (window.location.pathname === '/instance/cron' && getMode() !== MODE_HOST) {
                window.location.replace('/instance/cronbind');
            }
        }, 0);
    }
}