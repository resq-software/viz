// ResQ Viz - Canvas-rendered tree billboard sprites
// SPDX-License-Identifier: Apache-2.0
//
// Generates high-quality tree textures at runtime using the Canvas 2D API,
// then builds a cross-billboard geometry (two perpendicular quads) for use
// with InstancedMesh.  Cross-billboards look correct from any azimuth without
// camera-facing math, matching how open-world games render vegetation.

import * as THREE from 'three';

// ── Cross-billboard geometry ──────────────────────────────────────────────────
//
//   Two perpendicular unit quads (width=0.6, height=1.0) centred at x=0,z=0
//   with y=0 at the base (ground level) and y=1 at the top.
//   Instance matrices scale and translate this to the actual tree footprint.
//   Both faces render (DoubleSide) so winding order doesn't matter.

export function buildCrossGeo(): THREE.BufferGeometry {
    const hw = 0.30;  // half-width
    const geo = new THREE.BufferGeometry();

    // prettier-ignore
    const pos = new Float32Array([
        // Quad 1: in the XY plane (faces ±Z)
        -hw, 0, 0,    hw, 0, 0,    hw, 1, 0,   -hw, 1, 0,
        // Quad 2: in the ZY plane (faces ±X)
         0, 0, -hw,   0, 0,  hw,   0, 1,  hw,   0, 1, -hw,
    ]);
    // prettier-ignore
    const uvs = new Float32Array([
        0,0, 1,0, 1,1, 0,1,
        0,0, 1,0, 1,1, 0,1,
    ]);
    const idx = new Uint16Array([0,1,2, 0,2,3, 4,5,6, 4,6,7]);

    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('uv',       new THREE.BufferAttribute(uvs, 2));
    geo.setIndex(new THREE.BufferAttribute(idx, 1));
    geo.computeVertexNormals();
    return geo;
}

/** MeshStandardMaterial configured for alpha-cut billboard rendering. */
export function buildBillboardMaterial(tex: THREE.CanvasTexture): THREE.MeshStandardMaterial {
    const mat = new THREE.MeshStandardMaterial({
        map:       tex,
        alphaTest: 0.40,        // crisp silhouette cut
        side:      THREE.DoubleSide,
        roughness: 0.92,
        metalness: 0.0,
        // Emissive tint compensates for billboard receiving less env light
        emissive:  new THREE.Color(0.04, 0.06, 0.02),
    });
    return mat;
}

// ── Pine tree texture ─────────────────────────────────────────────────────────
//   Tiered-cone silhouette with subtle gradient shading per tier.
//   Canvas y=0 is top, y=H is bottom.  Texture.flipY=true (Three default)
//   maps canvas-top → texture-V=0 → mesh-bottom, so we draw the trunk at
//   CANVAS-TOP and the crown pointing DOWN in canvas space — Three.js flips it.

export function buildPineTexture(): THREE.CanvasTexture {
    const W = 64, H = 192;
    const cx = W / 2;
    const canvas = document.createElement('canvas');
    canvas.width  = W;
    canvas.height = H;
    const ctx = canvas.getContext('2d')!;

    // ── In canvas coords: trunk at top (y near 0), crown below (y near H)
    //    Three's flipY will invert this so trunk appears at bottom on screen.

    // Crown tiers (drawn bottom-to-top in canvas = top-to-bottom on screen after flip)
    //   Each tier: triangle from a wide base up to a narrower apex
    //   Overlap between tiers for a dense, layered look
    const tiers = [
        // { apexY, baseY, baseHalfW, fill, shadow }
        { aY: 155, bY: H,   hw: 28, fill: '#14320f', shadow: '#0b200a' },
        { aY: 110, bY: 165, hw: 22, fill: '#1a3d14', shadow: '#0f2a0d' },
        { aY:  72, bY: 118, hw: 17, fill: '#204819', shadow: '#133011' },
        { aY:  40, bY:  80, hw: 13, fill: '#265221', shadow: '#183814' },
        { aY:  14, bY:  48, hw:  9, fill: '#2b5a26', shadow: '#1c3e19' },
    ];

    for (const t of tiers) {
        // Base shadow pass
        ctx.beginPath();
        ctx.moveTo(cx, t.aY);
        ctx.lineTo(cx - t.hw - 2, t.bY);
        ctx.lineTo(cx + t.hw + 2, t.bY);
        ctx.closePath();
        ctx.fillStyle = t.shadow;
        ctx.fill();

        // Main tier (slightly smaller = inner highlight)
        const grad = ctx.createLinearGradient(cx - t.hw, 0, cx + t.hw, 0);
        grad.addColorStop(0.0, t.shadow);
        grad.addColorStop(0.3, t.fill);
        grad.addColorStop(0.7, t.fill);
        grad.addColorStop(1.0, t.shadow);
        ctx.beginPath();
        ctx.moveTo(cx, t.aY);
        ctx.lineTo(cx - t.hw, t.bY);
        ctx.lineTo(cx + t.hw, t.bY);
        ctx.closePath();
        ctx.fillStyle = grad;
        ctx.fill();

        // Sun-hit highlight streak (left-centre vertical)
        const hl = ctx.createLinearGradient(cx - t.hw * 0.2, t.aY, cx + t.hw * 0.15, t.bY);
        hl.addColorStop(0,   'rgba(90,140,60,0)');
        hl.addColorStop(0.4, 'rgba(90,140,60,0.22)');
        hl.addColorStop(1.0, 'rgba(90,140,60,0)');
        ctx.fillStyle = hl;
        ctx.fill();   // same path still active
    }

    // Trunk (canvas-top = screen-bottom after flip)
    const tw = 7;
    ctx.fillStyle = '#3d2010';
    ctx.beginPath();
    ctx.rect(cx - tw / 2, 0, tw, tiers[0]!.bY - tiers[0]!.aY + 10);
    ctx.fill();

    return _makeTexture(canvas);
}

// ── Deciduous tree texture ────────────────────────────────────────────────────
//   Rounded canopy composed of overlapping circles with radial gradient,
//   plus sub-lump circles for an organic silhouette.
//   Drawn the same way (trunk at canvas-top, crown down — flipped by Three).

export function buildDeciduousTexture(): THREE.CanvasTexture {
    const W = 64, H = 128;
    const cx = W / 2;
    const canvas = document.createElement('canvas');
    canvas.width  = W;
    canvas.height = H;
    const ctx = canvas.getContext('2d')!;

    // Trunk strip at canvas-top
    const tunkH = 22;
    ctx.fillStyle = '#4a3020';
    ctx.beginPath();
    ctx.rect(cx - 4, 0, 8, tunkH + 8);
    ctx.fill();

    // Main canopy blob: centred low in canvas (will appear high on screen)
    const canopyCY = H * 0.68;
    const R        = 26;

    // Background shadow disc
    ctx.beginPath();
    ctx.arc(cx, canopyCY + 3, R + 4, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(10,30,5,0.55)';
    ctx.fill();

    // Main radial gradient
    const grad = ctx.createRadialGradient(cx - R * 0.25, canopyCY - R * 0.25, R * 0.05, cx, canopyCY, R);
    grad.addColorStop(0.00, '#5ba030');
    grad.addColorStop(0.40, '#3a7020');
    grad.addColorStop(0.75, '#264d14');
    grad.addColorStop(1.00, '#172e0c');
    ctx.beginPath();
    ctx.arc(cx, canopyCY, R, 0, Math.PI * 2);
    ctx.fillStyle = grad;
    ctx.fill();

    // Organic sub-lumps for silhouette variety
    const lumps: [number, number, number, string][] = [
        [cx - 18, canopyCY + 4,  16, 'rgba(42,80,22,0.75)'],
        [cx + 16, canopyCY + 2,  14, 'rgba(38,72,20,0.70)'],
        [cx -  6, canopyCY - R + 8, 13, 'rgba(70,120,38,0.60)'],
        [cx + 10, canopyCY - 12, 11, 'rgba(60,105,30,0.55)'],
        [cx - 14, canopyCY - 14, 10, 'rgba(55, 95,28,0.50)'],
    ];
    for (const [lx, ly, lr, lc] of lumps) {
        ctx.beginPath();
        ctx.arc(lx, ly, lr, 0, Math.PI * 2);
        ctx.fillStyle = lc;
        ctx.fill();
    }

    // Light fringe on top-left (sun side)
    const fring = ctx.createRadialGradient(
        cx - R * 0.5, canopyCY - R * 0.5, 0,
        cx - R * 0.5, canopyCY - R * 0.5, R * 0.65,
    );
    fring.addColorStop(0.0, 'rgba(130,200,70,0.28)');
    fring.addColorStop(1.0, 'rgba(130,200,70,0)');
    ctx.beginPath();
    ctx.arc(cx, canopyCY, R, 0, Math.PI * 2);
    ctx.fillStyle = fring;
    ctx.fill();

    return _makeTexture(canvas);
}

// ── Shared helpers ─────────────────────────────────────────────────────────────

function _makeTexture(canvas: HTMLCanvasElement): THREE.CanvasTexture {
    const tex        = new THREE.CanvasTexture(canvas);
    tex.minFilter    = THREE.LinearMipmapLinearFilter;
    tex.magFilter    = THREE.LinearFilter;
    tex.generateMipmaps = true;
    tex.needsUpdate  = true;
    return tex;
}
