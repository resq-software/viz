/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

// TypeScript 6 requires explicit type declarations for side-effect imports of
// non-TS modules. Vite ships ambient declarations for `*.css`, asset URLs,
// `import.meta.env`, etc.; this triple-slash reference pulls them into the
// project so `import './styles/main.css'` (and friends) typecheck.
/// <reference types="vite/client" />

// TypeScript 6 lib.dom ships most WebGPU types (GPUDevice, GPUAdapter,
// GPUCanvasContext, etc.) but is missing the runtime usage-flag constants
// and the HTMLCanvasElement.getContext('webgpu') overload. Patch those in
// here until lib.dom catches up. Used by client/webgpu/*.
declare const GPUBufferUsage: {
    readonly MAP_READ: GPUFlagsConstant;
    readonly MAP_WRITE: GPUFlagsConstant;
    readonly COPY_SRC: GPUFlagsConstant;
    readonly COPY_DST: GPUFlagsConstant;
    readonly INDEX: GPUFlagsConstant;
    readonly VERTEX: GPUFlagsConstant;
    readonly UNIFORM: GPUFlagsConstant;
    readonly STORAGE: GPUFlagsConstant;
    readonly INDIRECT: GPUFlagsConstant;
    readonly QUERY_RESOLVE: GPUFlagsConstant;
};

declare const GPUTextureUsage: {
    readonly COPY_SRC: GPUFlagsConstant;
    readonly COPY_DST: GPUFlagsConstant;
    readonly TEXTURE_BINDING: GPUFlagsConstant;
    readonly STORAGE_BINDING: GPUFlagsConstant;
    readonly RENDER_ATTACHMENT: GPUFlagsConstant;
};

declare const GPUMapMode: {
    readonly READ: GPUFlagsConstant;
    readonly WRITE: GPUFlagsConstant;
};

interface HTMLCanvasElement {
    getContext(contextId: 'webgpu'): GPUCanvasContext | null;
}
