declare module 'mslx-request' {
    export interface RequestOptions {
        url: string;
        data?: any;
        params?: any;
        timeout?: number;
        [key: string]: any;
    }
    export interface RequestInstance {
        get<T = unknown>(options: RequestOptions): Promise<T>;
        post<T = unknown>(options: RequestOptions): Promise<T>;
        put<T = unknown>(options: RequestOptions): Promise<T>;
        delete<T = unknown>(options: RequestOptions): Promise<T>;
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