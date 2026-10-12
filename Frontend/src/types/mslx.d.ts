declare module 'mslx-request' {
    export interface RequestOptions {
        url: string;
        data?: any;
        params?: any;
        timeout?: number;
        [key: string]: any;
    }
    /** 宿主 mslx-request 的请求选项（节点路由/凭据），作为 get/post 的第二参数传入 */
    export interface RequestMeta {
        /**
         * 是否发送到子节点（宿主 mslx-request 支持）：true 强制当前选中子节点，
         * false 强制主节点，缺省按 URL 正则 auto 判定（/api/instance 等前缀走子节点）。
         */
        requestToSlaveNode?: boolean | 'auto';
        withToken?: boolean;
        [key: string]: any;
    }
    export interface RequestInstance {
        get<T = unknown>(options: RequestOptions, requestOptions?: RequestMeta): Promise<T>;
        post<T = unknown>(options: RequestOptions, requestOptions?: RequestMeta): Promise<T>;
        put<T = unknown>(options: RequestOptions, requestOptions?: RequestMeta): Promise<T>;
        delete<T = unknown>(options: RequestOptions, requestOptions?: RequestMeta): Promise<T>;
    }
    const request: RequestInstance;
    export default request;
}

declare global {
    interface Window {
        MSLX_Stores: any;
        MSLX_API: any;
        mslxRequest: any;
    }
}

declare module '*.vue' {
    import type { DefineComponent } from 'vue';
    const component: DefineComponent<{}, {}, any>;
    export default component;
}