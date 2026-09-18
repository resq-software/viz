// ResQ Viz - Shared SVG instrument primitives
// SPDX-License-Identifier: Apache-2.0
//
// Geometry, DOM helpers and readout conventions common to every instrument in
// ui/instruments.ts (the flight dials) and ui/vehicleInstruments.ts (the ground
// and surface faces). Split out when the second family landed: the two were
// otherwise going to keep their own copies of `setShown`, `setReadout` and a
// degrees-per-radian constant, and a helper that exists twice is a helper that
// gets fixed once.
//
// Every instrument shares one 200-unit square viewBox, so a font-size here is
// in user units, not pixels — the rendered size is `size * boxWidthPx / 200`.
// Sizing and placement of the box itself belong to ui/cockpit.css.

//#region Shared dial geometry (ported from lib/instrument-dial)

/** Square SVG user-space edge shared by every instrument. */
export const INSTRUMENT_VIEW = 200;
/** Centre of the instrument in user space. */
export const INSTRUMENT_CENTER = INSTRUMENT_VIEW / 2;
/** Local alias for the shared centre. */
export const CENTER = INSTRUMENT_CENTER;

/** A point in instrument user space. */
export interface Point {
	readonly x: number;
	readonly y: number;
}

/** Coerce a possibly-undefined / non-finite input to a finite number. */
export function toFinite(value: number | undefined, fallback = 0): number {
	return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

/** Clamp `value` into the inclusive `[min, max]` range. */
export function clamp(value: number, min: number, max: number): number {
	return Math.min(max, Math.max(min, value));
}

/** Point on a circle centred on the instrument, at `angleDeg` clockwise from top. */
export function polar(angleDeg: number, radius: number): Point {
	const rad = (angleDeg * Math.PI) / 180;
	return { x: CENTER + radius * Math.sin(rad), y: CENTER - radius * Math.cos(rad) };
}

/** SVG path string for an arc between two angles at a given radius. */
export function describeArc(radius: number, startAngle: number, endAngle: number): string {
	const start = polar(startAngle, radius);
	const end = polar(endAngle, radius);
	const largeArc = Math.abs(endAngle - startAngle) > 180 ? 1 : 0;
	const sweep = endAngle >= startAngle ? 1 : 0;
	return `M ${start.x} ${start.y} A ${radius} ${radius} 0 ${largeArc} ${sweep} ${end.x} ${end.y}`;
}

/** Map a scale `value` (clamped to `[min, max]`) to an angle over `sweep` degrees. */
export function valueToAngle(value: number, min: number, max: number, startAngle: number, sweep: number): number {
	const span = max - min;
	const fraction = span === 0 ? 0 : (clamp(value, min, max) - min) / span;
	return startAngle + fraction * sweep;
}

/** Evenly spaced scale values from `min` to `max` inclusive across `divisions` intervals. */
export function linearTicks(min: number, max: number, divisions: number): number[] {
	const safe = Math.max(1, Math.round(divisions));
	return Array.from({ length: safe + 1 }, (_unused, index) => min + ((max - min) * index) / safe);
}

//#endregion

//#region SVG DOM helpers

export const SVG_NS = 'http://www.w3.org/2000/svg';

/** Attribute bag for {@link svgEl}; values are stringified via `setAttribute`. */
export type SvgAttrs = Record<string, string | number>;

/** Create a namespaced SVG element and apply the given attributes. */
export function svgEl<K extends keyof SVGElementTagNameMap>(tag: K, attrs: SvgAttrs): SVGElementTagNameMap[K] {
	const el = document.createElementNS(SVG_NS, tag);
	for (const [name, value] of Object.entries(attrs)) el.setAttribute(name, String(value));
	return el;
}

/** Create an SVG `<text>` node with `content` as its text child. */
export function svgText(attrs: SvgAttrs, content: string): SVGTextElement {
	const el = svgEl('text', attrs);
	el.textContent = content;
	return el;
}

/** Shared attributes for every monospace dial label / number. */
export function labelAttrs(size: number, anchor: string, x: number, y: number): SvgAttrs {
	return { class: 'font-mono', 'dominant-baseline': 'middle', 'font-size': size, 'text-anchor': anchor, x, y };
}

/** Mutate a `<line>`'s endpoints in place (dynamic hand / needle updates). */
export function setLine(el: SVGLineElement, from: Point, to: Point): void {
	el.setAttribute('x1', String(from.x));
	el.setAttribute('y1', String(from.y));
	el.setAttribute('x2', String(to.x));
	el.setAttribute('y2', String(to.y));
}

/** A stationary hub/needle line anchored at the centre, endpoints set later. */
export function centredLine(stroke: string, width: number): SVGLineElement {
	return svgEl('line', { stroke, 'stroke-linecap': 'round', 'stroke-width': width, x1: CENTER, x2: CENTER, y1: CENTER, y2: CENTER });
}

/** Build the wrapper `<div class="instrument instrument--*">` plus its `<svg>`. */
export function createRoot(modifier: string, ariaLabel: string): { el: HTMLDivElement; svg: SVGSVGElement } {
	const el = document.createElement('div');
	el.className = `instrument instrument--${modifier}`;
	el.setAttribute('role', 'img');
	el.setAttribute('aria-label', ariaLabel);
	const svg = svgEl('svg', { viewBox: `0 0 ${INSTRUMENT_VIEW} ${INSTRUMENT_VIEW}` });
	svg.setAttribute('aria-hidden', 'true');
	svg.style.width = '100%';
	svg.style.height = '100%';
	el.appendChild(svg);
	return { el, svg };
}

//#endregion

//#region Readout conventions

/** Degrees per radian, for instruments whose telemetry arrives in radians. */
export const DEG_PER_RAD = 180 / Math.PI;

/**
 * The glyph every instrument shows in place of a value it does not have. One
 * constant rather than one per face: an operator scanning a row should not have
 * to learn that `--` and `—` mean the same thing.
 */
export const UNKNOWN_READOUT = '—';

/**
 * Whether a telemetry field carries a usable reading. `null`, `undefined` and
 * `NaN` do not. The distinction is the point: a depth tape reading 0 m when the
 * sounder has failed is worse than one reading nothing at all, and the same goes
 * for a rollover risk of 0 % on a vehicle whose attitude has dropped out.
 */
export function isReading(value: number | null | undefined): value is number {
	return typeof value === 'number' && Number.isFinite(value);
}

/** Toggle a node's presence without disturbing its other attributes. */
export function setShown(node: SVGElement, shown: boolean): void {
	if (shown) node.removeAttribute('display');
	else node.setAttribute('display', 'none');
}

/** Set a readout's text and colour together — the pair is always updated as one. */
export function setReadout(node: SVGTextElement, text: string, fill: string): void {
	node.textContent = text;
	node.setAttribute('fill', fill);
}

/** Wrap a heading into the [0, 360) range. */
export function normalizeHeading(value: number): number {
	return ((value % 360) + 360) % 360;
}

/** Non-negative remainder, so negative altitudes still map onto the dial. */
export function positiveMod(value: number, modulus: number): number {
	return ((value % modulus) + modulus) % modulus;
}

//#endregion
