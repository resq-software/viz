// ResQ Viz - Onboard camera picture-in-picture (FPV / depth / segmentation)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import * as THREE from 'three';
import type { SelectionStore } from '../editor/selection';
import { GLOBAL_SHORTCUTS } from '../ui/globalShortcuts';
import { shouldIgnoreGlobalShortcut } from '../ui/hotkeys';

/** A WebGL scissor/viewport rect (CSS px, bottom-left origin). */
export interface ScissorRect {
    x: number;
    y: number;
    width: number;
    height: number;
}

/**
 * Convert a DOM rect (top-left origin, CSS px) into a WebGL scissor rect
 * (bottom-left origin). Pure — unit-tested; this Y-flip is the one fiddly bit
 * of aligning the canvas render with the DOM frame.
 */
export function rectToScissor(
    rect: { left: number; top: number; width: number; height: number },
    canvasHeightPx: number,
): ScissorRect {
    return {
        x: Math.round(rect.left),
        y: Math.round(canvasHeightPx - rect.top - rect.height),
        width: Math.round(rect.width),
        height: Math.round(rect.height),
    };
}

/** Sensor image modes the onboard camera can render (AirSim-style). */
export type PipMode = 'scene' | 'depth' | 'segmentation';

/** Cycle order for the `O` key. */
export const PIP_MODES: readonly PipMode[] = ['scene', 'depth', 'segmentation'];

/** Next mode in the cycle after `current` (wraps); unknown → first. Pure. */
export function nextPipMode(current: PipMode): PipMode {
    const i = PIP_MODES.indexOf(current);
    return PIP_MODES[(i + 1) % PIP_MODES.length] ?? 'scene';
}

/** Short window-label prefix for a mode. Pure. */
export function pipModeLabel(mode: PipMode): string {
    return mode === 'depth' ? 'DEPTH' : mode === 'segmentation' ? 'SEG' : 'FPV';
}

/** Deterministic hue in [0,1) from a string (djb2). Pure — seeds segmentation colours. */
export function hashHue(s: string): number {
    let h = 5381;
    for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0;
    return (h % 360) / 360;
}

export interface OnboardPipOptions {
    scene: THREE.Scene;
    renderer: THREE.WebGLRenderer;
    store: SelectionStore;
    /** The selected drone's group (read for FPV pose), or null. */
    getSelectedGroup: () => THREE.Object3D | null;
    /** The selected drone's id (for the window label), or null. */
    getSelectedId: () => string | null;
    /** Register a callback that runs after the main composer render. */
    addPostRender: (fn: () => void) => void;
}

/** FPV gimbal: how far ahead and how far below to aim the onboard camera. */
const LOOK_AHEAD = 60;
const LOOK_BELOW = 25;
/** Tightened far plane for the depth pass so the near scene shows a gradient. */
const DEPTH_FAR = 400;

/**
 * Onboard picture-in-picture for the selected drone — the AirSim-signature
 * sub-window. A second camera rides the drone; after the main composer render
 * each frame, the scene is drawn again from it into a scissored corner of the
 * canvas. A DOM frame (transparent body) supplies the border + label and, via
 * its `getBoundingClientRect`, the scissor rect — so CSS owns placement and the
 * canvas render stays aligned.
 *
 * `P` toggles the window; `O` cycles the image mode (FPV → depth → segmentation,
 * mirroring AirSim's scene/depth/segmentation cameras). Depth overrides the
 * scene material; segmentation swaps each object to a flat per-instance colour.
 */
export class OnboardPip {
    private readonly _camera = new THREE.PerspectiveCamera(70, 16 / 9, 0.5, 40000);
    private readonly _root: HTMLElement;
    private readonly _label: HTMLElement;
    private readonly _view: HTMLElement;
    private readonly _opts: OnboardPipOptions;
    private _enabled = true;
    private _mode: PipMode = 'scene';
    private readonly _fwd = new THREE.Vector3();
    private readonly _target = new THREE.Vector3();

    // Depth pass: one shared override material.
    private readonly _depthMat = new THREE.MeshDepthMaterial();
    // Segmentation: flat materials keyed by colour hex; colours keyed by the
    // mesh's top-level scene ancestor so an object's parts share one colour.
    private readonly _segMats = new Map<number, THREE.MeshBasicMaterial>();
    private readonly _rootColors = new Map<string, THREE.Color>();
    private readonly _segSwap = new Map<THREE.Mesh, THREE.Material | THREE.Material[]>();

    constructor(opts: OnboardPipOptions) {
        this._opts = opts;
        const built = this._build();
        this._root = built.root;
        this._label = built.label;
        this._view = built.view;

        opts.store.subscribe(() => this._sync());
        opts.addPostRender(() => this._render());
        this._bindKeyboard();
    }

    private _isDroneSelected(): boolean {
        return this._opts.store.current?.kind === 'drone';
    }

    private _sync(): void {
        const show = this._enabled && this._isDroneSelected();
        this._root.hidden = !show;
        this._root.setAttribute('aria-hidden', String(!show));
        if (show) this._label.textContent = `${pipModeLabel(this._mode)} · ${this._opts.getSelectedId() ?? ''}`;
    }

    /** Draw the onboard view into the canvas corner. Must restore renderer state. */
    private _render(): void {
        if (this._root.hidden) return;
        const group = this._opts.getSelectedGroup();
        if (!group) return;

        const rect = this._view.getBoundingClientRect();
        if (rect.width < 4 || rect.height < 4) return;

        // FPV pose: ride the drone, aim forward and down (SAR ground view).
        this._fwd.set(0, 0, -1).applyQuaternion(group.quaternion);
        this._camera.position.copy(group.position);
        this._target.copy(group.position).addScaledVector(this._fwd, LOOK_AHEAD);
        this._target.y -= LOOK_BELOW;
        this._camera.lookAt(this._target);

        const renderer = this._opts.renderer;
        const scene = this._opts.scene;
        const s = rectToScissor(rect, renderer.domElement.clientHeight);
        this._camera.aspect = s.width / s.height;
        this._camera.updateProjectionMatrix();

        // Hide the drone's own body so the FPV isn't clipped by it at the origin.
        const wasVisible = group.visible;
        const prevAutoClear = renderer.autoClear;
        group.visible = false;
        renderer.autoClear = false;
        renderer.setScissorTest(true);
        renderer.setViewport(s.x, s.y, s.width, s.height);
        renderer.setScissor(s.x, s.y, s.width, s.height);
        renderer.clear(true, true, false);

        try {
            if (this._mode === 'depth') this._renderDepth(renderer, scene);
            else if (this._mode === 'segmentation') this._renderSegmentation(renderer, scene);
            else renderer.render(scene, this._camera);
        } finally {
            // Restore ALL renderer/scene state on every path, including a throw —
            // this callback runs AFTER the composer, so a leaked scissor/viewport
            // would clip the entire main view on the NEXT frame.
            renderer.setScissorTest(false);
            renderer.setViewport(0, 0, renderer.domElement.clientWidth, renderer.domElement.clientHeight);
            renderer.setScissor(0, 0, renderer.domElement.clientWidth, renderer.domElement.clientHeight);
            renderer.autoClear = prevAutoClear;
            group.visible = wasVisible;
        }
    }

    private _renderDepth(renderer: THREE.WebGLRenderer, scene: THREE.Scene): void {
        const farWas = this._camera.far;
        this._camera.far = DEPTH_FAR; // bias the gradient toward the near scene
        this._camera.updateProjectionMatrix();
        scene.overrideMaterial = this._depthMat;
        try {
            renderer.render(scene, this._camera);
        } finally {
            // overrideMaterial is GLOBAL scene state — must be cleared even on a
            // throw, or the main composer would paint the whole world with depth.
            scene.overrideMaterial = null;
            this._camera.far = farWas;
            this._camera.updateProjectionMatrix();
        }
    }

    private _renderSegmentation(renderer: THREE.WebGLRenderer, scene: THREE.Scene): void {
        // Swap every mesh to a flat per-instance colour, render, then restore —
        // same shape as the bloom pass's darken/restore, so nothing leaks.
        this._segSwap.clear();
        scene.traverse((obj) => {
            const mesh = obj as THREE.Mesh;
            if (!mesh.isMesh) return;
            this._segSwap.set(mesh, mesh.material);
            mesh.material = this._segMaterialFor(mesh, scene);
        });
        try {
            renderer.render(scene, this._camera);
        } finally {
            // Restore every swapped material even on a throw, or the main view
            // would be left painted in flat segmentation colours.
            for (const [mesh, mat] of this._segSwap) mesh.material = mat;
            this._segSwap.clear();
        }
    }

    private _segMaterialFor(mesh: THREE.Mesh, scene: THREE.Scene): THREE.MeshBasicMaterial {
        // Walk up to the top-level scene child so an object's parts share a colour.
        let root: THREE.Object3D = mesh;
        while (root.parent && root.parent !== scene) root = root.parent;
        let color = this._rootColors.get(root.uuid);
        if (!color) {
            color = new THREE.Color().setHSL(hashHue(root.uuid), 0.65, 0.55);
            this._rootColors.set(root.uuid, color);
        }
        const hex = color.getHex();
        let mat = this._segMats.get(hex);
        if (!mat) {
            mat = new THREE.MeshBasicMaterial({ color, fog: false });
            this._segMats.set(hex, mat);
        }
        return mat;
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', (e: KeyboardEvent) => {
            if (shouldIgnoreGlobalShortcut(e)) return;
            if (e.code === GLOBAL_SHORTCUTS.onboardPip) {
                e.preventDefault();
                this._enabled = !this._enabled;
                this._sync();
            } else if (e.code === GLOBAL_SHORTCUTS.onboardPipMode) {
                e.preventDefault();
                this._mode = nextPipMode(this._mode);
                this._sync();
            }
        });
    }

    private _build(): { root: HTMLElement; label: HTMLElement; view: HTMLElement } {
        const root = document.createElement('aside');
        root.className = 'resq-pip';
        root.hidden = true;
        root.setAttribute('aria-hidden', 'true');
        root.setAttribute('aria-label', 'Onboard camera');

        const label = document.createElement('div');
        label.className = 'pip-label';

        const view = document.createElement('div');
        view.className = 'pip-view';

        root.append(label, view);
        document.body.appendChild(root);
        return { root, label, view };
    }
}
