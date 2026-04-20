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

import { defineConfig } from 'vite';

export default defineConfig({
  root: 'client',
  // Treat large 3D assets as file references, not inlined base64. Vite's
  // default 4 KB inline threshold already bumps anything bigger to
  // wwwroot/assets/; `assetsInclude` adds glob coverage so these types
  // are recognised when referenced via `new URL(..., import.meta.url)`.
  assetsInclude: ['**/*.glb', '**/*.gltf', '**/*.ktx2', '**/*.hdr'],
  build: {
    outDir: '../wwwroot',
    // Clean the output dir on rebuild. Without this, Vite accumulates
    // stale hash-renamed chunks across builds (9+ duplicates of the
    // same 640 KB JS file at one point).
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/viz': { target: 'http://localhost:5000', ws: true },
      '/api': { target: 'http://localhost:5000' },
    },
  },
});
