// ResQ Viz - Clean-room SVG flight instruments (imperative, dependency-free)
// SPDX-License-Identifier: Apache-2.0
//
// Vanilla-TypeScript port of the five @resq-systems/ui React (.tsx) flight
// instruments: attitude indicator, heading indicator, altimeter, airspeed
// indicator, and vertical-speed indicator. This is a mechanical translation —
// every geometry constant, angle, radius, tick, and color token matches the
// React sources exactly; only the rendering strategy changed.
//
// Each instrument is a factory returning { el, update(...) }. Static geometry
// (ticks, numbers, fixed reference marks, bands) is created ONCE per instance;
// update() mutates only the dynamic nodes (transform attributes, hand/needle
// endpoints, digital text, and the live aria-label). All colors stay design
// tokens from styles/tokens.css; sizing/positioning is left to external CSS.

//#region Shared dial geometry (ported from lib/instrument-dial)

/** Square SVG user-space edge shared by every instrument. */
const INSTRUMENT_VIEW = 200;
/** Centre of the instrument in user space. */
const INSTRUMENT_CENTER = INSTRUMENT_VIEW / 2;
/** Local alias for the shared centre. */
const CENTER = INSTRUMENT_CENTER;

/** A point in instrument user space. */
interface Point {
	readonly x: number;
	readonly y: number;
}

/** Coerce a possibly-undefined / non-finite input to a finite number. */
function toFinite(value: number | undefined, fallback = 0): number {
	return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

/** Clamp `value` into the inclusive `[min, max]` range. */
function clamp(value: number, min: number, max: number): number {
	return Math.min(max, Math.max(min, value));
}

/** Point on a circle centred on the instrument, at `angleDeg` clockwise from top. */
function polar(angleDeg: number, radius: number): Point {
	const rad = (angleDeg * Math.PI) / 180;
	return { x: CENTER + radius * Math.sin(rad), y: CENTER - radius * Math.cos(rad) };
}

/** SVG path string for an arc between two angles at a given radius. */
function describeArc(radius: number, startAngle: number, endAngle: number): string {
	const start = polar(startAngle, radius);
	const end = polar(endAngle, radius);
	const largeArc = Math.abs(endAngle - startAngle) > 180 ? 1 : 0;
	const sweep = endAngle >= startAngle ? 1 : 0;
	return `M ${start.x} ${start.y} A ${radius} ${radius} 0 ${largeArc} ${sweep} ${end.x} ${end.y}`;
}

/** Map a scale `value` (clamped to `[min, max]`) to an angle over `sweep` degrees. */
function valueToAngle(value: number, min: number, max: number, startAngle: number, sweep: number): number {
	const span = max - min;
	const fraction = span === 0 ? 0 : (clamp(value, min, max) - min) / span;
	return startAngle + fraction * sweep;
}

/** Evenly spaced scale values from `min` to `max` inclusive across `divisions` intervals. */
function linearTicks(min: number, max: number, divisions: number): number[] {
	const safe = Math.max(1, Math.round(divisions));
	return Array.from({ length: safe + 1 }, (_unused, index) => min + ((max - min) * index) / safe);
}

//#endregion

//#region SVG DOM helpers

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Attribute bag for {@link svgEl}; values are stringified via `setAttribute`. */
type SvgAttrs = Record<string, string | number>;

/** Create a namespaced SVG element and apply the given attributes. */
function svgEl<K extends keyof SVGElementTagNameMap>(tag: K, attrs: SvgAttrs): SVGElementTagNameMap[K] {
	const el = document.createElementNS(SVG_NS, tag);
	for (const [name, value] of Object.entries(attrs)) el.setAttribute(name, String(value));
	return el;
}

/** Create an SVG `<text>` node with `content` as its text child. */
function svgText(attrs: SvgAttrs, content: string): SVGTextElement {
	const el = svgEl('text', attrs);
	el.textContent = content;
	return el;
}

/** Shared attributes for every monospace dial label / number. */
function labelAttrs(size: number, anchor: string, x: number, y: number): SvgAttrs {
	return { class: 'font-mono', 'dominant-baseline': 'middle', 'font-size': size, 'text-anchor': anchor, x, y };
}

/** Mutate a `<line>`'s endpoints in place (dynamic hand / needle updates). */
function setLine(el: SVGLineElement, from: Point, to: Point): void {
	el.setAttribute('x1', String(from.x));
	el.setAttribute('y1', String(from.y));
	el.setAttribute('x2', String(to.x));
	el.setAttribute('y2', String(to.y));
}

/** A stationary hub/needle line anchored at the centre, endpoints set later. */
function centredLine(stroke: string, width: number): SVGLineElement {
	return svgEl('line', { stroke, 'stroke-linecap': 'round', 'stroke-width': width, x1: CENTER, x2: CENTER, y1: CENTER, y2: CENTER });
}

/** Build the wrapper `<div class="instrument instrument--*">` plus its `<svg>`. */
function createRoot(modifier: string, ariaLabel: string): { el: HTMLDivElement; svg: SVGSVGElement } {
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

//#region Label builders (ported aria-label sentences)

function formatAttitudeLabel(pitch: number, roll: number): string {
	const p = Math.round(pitch);
	const r = Math.round(roll);
	const pitchPart = p === 0 ? 'level' : `${Math.abs(p)} degrees ${p > 0 ? 'nose up' : 'nose down'}`;
	const rollPart = r === 0 ? 'wings level' : `${Math.abs(r)} degrees ${r > 0 ? 'right' : 'left'} bank`;
	return `Attitude indicator, pitch ${pitchPart}, ${rollPart}`;
}

function headingLabel(headingDeg: number): string {
	return `Heading indicator, ${Math.round(headingDeg) % 360} degrees`;
}

function altimeterLabel(feet: number, unit: string): string {
	return `Altimeter, ${Math.round(feet)} ${unit === 'ft' ? 'feet' : unit}`;
}

function airspeedLabel(value: number, unit: string): string {
	return `Airspeed indicator, ${Math.round(value)} ${unit}`;
}

function vsiLabel(rounded: number): string {
	return rounded === 0
		? 'Vertical speed indicator, level'
		: `Vertical speed indicator, ${Math.abs(rounded)} feet per minute ${rounded > 0 ? 'climb' : 'descent'}`;
}

//#endregion

//#region Instrument-specific helpers

/** Wrap a roll angle into the (−180, 180] range so 190° reads as −170°. */
function normalizeRoll(value: number): number {
	return ((((value + 180) % 360) + 360) % 360) - 180;
}

/** Wrap a heading into the [0, 360) range. */
function normalizeHeading(value: number): number {
	return ((value % 360) + 360) % 360;
}

/** Non-negative remainder, so negative altitudes still map onto the dial. */
function positiveMod(value: number, modulus: number): number {
	return ((value % modulus) + modulus) % modulus;
}

/** Format a thousands label without a trailing `.0`. */
function thousandsLabel(value: number): string {
	const thousands = Math.abs(value) / 1000;
	return Number.isInteger(thousands) ? String(thousands) : thousands.toFixed(1);
}

//#endregion

//#region Attitude indicator

/** Artificial-horizon attitude instrument. */
export interface AttitudeInstrument {
	el: HTMLDivElement;
	update(pitch: number, roll: number): void;
}

export function createAttitudeIndicator(): AttitudeInstrument {
	const MARK = 'var(--primary-foreground)';
	const SKY = 'var(--info)';
	const GROUND = 'var(--warning)';
	const AIRCRAFT_ACCENT = 'var(--warning)';

	const PIXELS_PER_DEGREE = 2.5;
	const PITCH_DISPLAY_LIMIT = 90;
	const HALF_TURN = 180;
	const MAJOR_HALF_WIDTH = 22;
	const MINOR_HALF_WIDTH = 11;
	const LADDER_LABEL_GAP = 5;
	const BANK_TICK_OUTER = 96;
	const BANK_TICK_INNER = 88;
	const BANK_TICK_INNER_LONG = 83;
	const PITCH_MAJORS = [10, 20, 30];
	const PITCH_MINORS = [5, 15, 25];
	const BANK_ANGLES = [0, 10, -10, 20, -20, 30, -30, 45, -45, 60, -60];
	const FIELD_X = -CENTER * 3;
	const FIELD_W = INSTRUMENT_VIEW * 4;
	const FIELD_H = 700;

	const { el, svg } = createRoot('attitude', formatAttitudeLabel(0, 0));

	// Moving horizon: roll rotates the disc, pitch slides it vertically.
	const horizonRollGroup = svgEl('g', { transform: `rotate(0 ${CENTER} ${CENTER})` });
	const pitchGroup = svgEl('g', { transform: 'translate(0 0)' });
	pitchGroup.appendChild(svgEl('rect', { fill: SKY, height: FIELD_H, width: FIELD_W, x: FIELD_X, y: CENTER - FIELD_H }));
	pitchGroup.appendChild(svgEl('rect', { fill: GROUND, height: FIELD_H, width: FIELD_W, x: FIELD_X, y: CENTER }));
	pitchGroup.appendChild(svgEl('line', { stroke: MARK, 'stroke-width': 1.6, x1: FIELD_X, x2: FIELD_X + FIELD_W, y1: CENTER, y2: CENTER }));
	for (const angle of PITCH_MINORS.flatMap((a) => [a, -a])) {
		const y = CENTER - angle * PIXELS_PER_DEGREE;
		pitchGroup.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-opacity': 0.75, 'stroke-width': 1, x1: CENTER - MINOR_HALF_WIDTH, x2: CENTER + MINOR_HALF_WIDTH, y1: y, y2: y }));
	}
	for (const angle of PITCH_MAJORS.flatMap((a) => [a, -a])) {
		const y = CENTER - angle * PIXELS_PER_DEGREE;
		const t = String(Math.abs(angle));
		pitchGroup.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 1.4, x1: CENTER - MAJOR_HALF_WIDTH, x2: CENTER + MAJOR_HALF_WIDTH, y1: y, y2: y }));
		pitchGroup.appendChild(svgText({ ...labelAttrs(9, 'end', CENTER - MAJOR_HALF_WIDTH - LADDER_LABEL_GAP, y), fill: MARK }, t));
		pitchGroup.appendChild(svgText({ ...labelAttrs(9, 'start', CENTER + MAJOR_HALF_WIDTH + LADDER_LABEL_GAP, y), fill: MARK }, t));
	}
	horizonRollGroup.appendChild(pitchGroup);
	svg.appendChild(horizonRollGroup);

	// Bank scale: rotates with roll but is unaffected by pitch.
	const bankRollGroup = svgEl('g', { transform: `rotate(0 ${CENTER} ${CENTER})` });
	for (const bank of BANK_ANGLES) {
		const rad = (bank * Math.PI) / HALF_TURN;
		const sin = Math.sin(rad);
		const cos = Math.cos(rad);
		const inner = bank === 0 || Math.abs(bank) === 30 || Math.abs(bank) === 60 ? BANK_TICK_INNER_LONG : BANK_TICK_INNER;
		bankRollGroup.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 1.2, x1: CENTER + BANK_TICK_OUTER * sin, x2: CENTER + inner * sin, y1: CENTER - BANK_TICK_OUTER * cos, y2: CENTER - inner * cos }));
	}
	svg.appendChild(bankRollGroup);

	// Fixed reference: sky pointer + miniature aircraft.
	const ref = svgEl('g', {});
	ref.appendChild(svgEl('polygon', { fill: MARK, points: `${CENTER},13 ${CENTER - 6},25 ${CENTER + 6},25` }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 3, x1: 62, x2: 90, y1: CENTER, y2: CENTER }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 3, x1: 90, x2: 90, y1: CENTER, y2: CENTER + 6 }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 3, x1: 110, x2: 138, y1: CENTER, y2: CENTER }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 3, x1: 110, x2: 110, y1: CENTER, y2: CENTER + 6 }));
	ref.appendChild(svgEl('circle', { cx: CENTER, cy: CENTER, fill: AIRCRAFT_ACCENT, r: 3.2 }));
	svg.appendChild(ref);

	function update(pitch: number, roll: number): void {
		const pitchDeg = clamp(toFinite(pitch), -PITCH_DISPLAY_LIMIT, PITCH_DISPLAY_LIMIT);
		const rollDeg = normalizeRoll(toFinite(roll));
		const rollTransform = `rotate(${-rollDeg} ${CENTER} ${CENTER})`;
		horizonRollGroup.setAttribute('transform', rollTransform);
		bankRollGroup.setAttribute('transform', rollTransform);
		pitchGroup.setAttribute('transform', `translate(0 ${pitchDeg * PIXELS_PER_DEGREE})`);
		el.setAttribute('aria-label', formatAttitudeLabel(pitchDeg, rollDeg));
	}

	update(0, 0);
	return { el, update };
}

//#endregion

//#region Heading indicator

/** Directional-gyro heading instrument. */
export interface HeadingInstrument {
	el: HTMLDivElement;
	update(heading: number): void;
}

export function createHeadingIndicator(): HeadingInstrument {
	const MARK = 'var(--foreground)';
	const ACCENT = 'var(--warning)';
	const TICK_STEP = 5;
	const TICK_OUTER = 96;
	const TICK_MAJOR_INNER = 82;
	const TICK_MEDIUM_INNER = 87;
	const TICK_MINOR_INNER = 91;
	const LABEL_RADIUS = 72;
	const CARDINALS: Record<number, string> = { 0: 'N', 90: 'E', 180: 'S', 270: 'W' };

	const { el, svg } = createRoot('heading', headingLabel(0));

	// Rotating compass card: brings the current heading to the top.
	const card = svgEl('g', { transform: `rotate(0 ${CENTER} ${CENTER})` });
	for (let index = 0; index < 360 / TICK_STEP; index++) {
		const deg = index * TICK_STEP;
		const major = deg % 30 === 0;
		const medium = !major && deg % 10 === 0;
		const inner = major ? TICK_MAJOR_INNER : medium ? TICK_MEDIUM_INNER : TICK_MINOR_INNER;
		const width = major ? 1.8 : medium ? 1.2 : 0.8;
		const outer = polar(deg, TICK_OUTER);
		const end = polar(deg, inner);
		card.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': width, x1: outer.x, x2: end.x, y1: outer.y, y2: end.y }));
	}
	for (let index = 0; index < 360 / 30; index++) {
		const deg = index * 30;
		const size = CARDINALS[deg] ? 12 : 9;
		const text = CARDINALS[deg] ?? String(deg / 10);
		const point = polar(deg, LABEL_RADIUS);
		card.appendChild(svgText({ ...labelAttrs(size, 'middle', point.x, point.y), fill: MARK, transform: `rotate(${deg} ${point.x} ${point.y})` }, text));
	}
	card.appendChild(svgEl('polygon', { fill: ACCENT, points: `${CENTER},${CENTER - 80} ${CENTER - 4},${CENTER - 90} ${CENTER + 4},${CENTER - 90}` }));
	svg.appendChild(card);

	// Fixed reference: lubber line + aircraft symbol.
	const ref = svgEl('g', {});
	ref.appendChild(svgEl('polygon', { fill: ACCENT, points: `${CENTER - 5},4 ${CENTER + 5},4 ${CENTER},16` }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 2.2, x1: CENTER, x2: CENTER, y1: 86, y2: 116 }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 2.2, x1: 86, x2: 114, y1: 102, y2: 102 }));
	ref.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': 2.2, x1: 94, x2: 106, y1: 112, y2: 112 }));
	svg.appendChild(ref);

	function update(heading: number): void {
		const headingDeg = normalizeHeading(toFinite(heading));
		card.setAttribute('transform', `rotate(${-headingDeg} ${CENTER} ${CENTER})`);
		el.setAttribute('aria-label', headingLabel(headingDeg));
	}

	update(0);
	return { el, update };
}

//#endregion

//#region Altimeter

/** Two-hand sensitive altimeter. */
export interface AltimeterInstrument {
	el: HTMLDivElement;
	update(altitude: number): void;
}

export function createAltimeter(opts: { unit?: string } = {}): AltimeterInstrument {
	const unit = opts.unit ?? 'ft';
	const MARK = 'var(--foreground)';
	const HUB = 'var(--warning)';
	const HUNDREDS_PER_REV = 1000;
	const THOUSANDS_PER_REV = 10000;
	const TICK_OUTER = 96;
	const TICK_MAJOR_INNER = 84;
	const TICK_MINOR_INNER = 90;
	const LABEL_RADIUS = 70;
	const HUNDREDS_HAND = 86;
	const THOUSANDS_HAND = 54;
	const HUB_RADIUS = 5;
	const MINOR_TICKS = 50;

	const { el, svg } = createRoot('altimeter', altimeterLabel(0, unit));

	// Ticks: 50 minor (every 20 ft), major every 100 ft.
	for (let index = 0; index < MINOR_TICKS; index++) {
		const angle = (index / MINOR_TICKS) * 360;
		const major = index % 5 === 0;
		const outer = polar(angle, TICK_OUTER);
		const inner = polar(angle, major ? TICK_MAJOR_INNER : TICK_MINOR_INNER);
		svg.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': major ? 1.8 : 0.8, x1: outer.x, x2: inner.x, y1: outer.y, y2: inner.y }));
	}
	// 0–9 numbers.
	for (let n = 0; n < 10; n++) {
		const point = polar(n * 36, LABEL_RADIUS);
		svg.appendChild(svgText({ ...labelAttrs(12, 'middle', point.x, point.y), fill: MARK }, String(n)));
	}
	// Digital counter.
	svg.appendChild(svgEl('rect', { fill: 'var(--background)', height: 17, rx: 3, stroke: 'var(--border)', width: 46, x: CENTER - 23, y: CENTER + 30 }));
	const counter = svgText({ ...labelAttrs(12, 'middle', CENTER, CENTER + 39), fill: MARK }, '0');
	svg.appendChild(counter);

	// Thousands hand (short, wide), then hundreds hand (long, thin).
	const thousandsHand = centredLine(MARK, 5);
	const hundredsHand = centredLine(MARK, 2.4);
	svg.appendChild(thousandsHand);
	svg.appendChild(hundredsHand);
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: CENTER, fill: HUB, r: HUB_RADIUS }));

	function update(altitude: number): void {
		const feet = toFinite(altitude);
		const hundredsAngle = valueToAngle(positiveMod(feet, HUNDREDS_PER_REV), 0, HUNDREDS_PER_REV, 0, 360);
		const thousandsAngle = valueToAngle(positiveMod(feet, THOUSANDS_PER_REV), 0, THOUSANDS_PER_REV, 0, 360);
		setLine(hundredsHand, polar(hundredsAngle + 180, 14), polar(hundredsAngle, HUNDREDS_HAND));
		setLine(thousandsHand, polar(thousandsAngle + 180, 12), polar(thousandsAngle, THOUSANDS_HAND));
		counter.textContent = String(Math.round(feet));
		el.setAttribute('aria-label', altimeterLabel(feet, unit));
	}

	update(0);
	return { el, update };
}

//#endregion

//#region Airspeed indicator

export type SpeedBandTone = 'normal' | 'caution' | 'danger';

export interface SpeedBand {
	/** Range start, in scale units. */
	from: number;
	/** Range end, in scale units. */
	to: number;
	/** Semantic tone → green / amber / red arc. */
	tone: SpeedBandTone;
}

/** Round pointer gauge (airspeed / generic telemetry dial). */
export interface AirspeedInstrument {
	el: HTMLDivElement;
	update(speed: number): void;
}

export function createAirspeedIndicator(
	opts: { maxSpeed?: number; unit?: string; bands?: SpeedBand[]; redline?: number } = {},
): AirspeedInstrument {
	const MARK = 'var(--foreground)';
	const HUB = 'var(--warning)';
	const TONE: Record<SpeedBandTone, string> = {
		caution: 'var(--warning)',
		danger: 'var(--destructive)',
		normal: 'var(--success)',
	};
	const START_ANGLE = 210;
	const SWEEP = 300;
	const TICK_OUTER = 96;
	const TICK_MAJOR_INNER = 84;
	const TICK_MINOR_INNER = 89;
	const LABEL_RADIUS = 71;
	const BAND_RADIUS = 90;
	const NEEDLE_TIP = 80;
	const NEEDLE_TAIL = 18;
	const HUB_RADIUS = 4.5;
	const MINOR_DIVISIONS = 20;
	const DEFAULT_MAX = 200;

	const unit = opts.unit ?? 'kt';
	const rawMax = toFinite(opts.maxSpeed, DEFAULT_MAX);
	const max = rawMax > 0 ? rawMax : DEFAULT_MAX;
	const angleForValue = (value: number): number => valueToAngle(value, 0, max, START_ANGLE, SWEEP);

	const { el, svg } = createRoot('airspeed', airspeedLabel(0, unit));

	// Colored operating-range arcs.
	for (const band of (opts.bands ?? []).filter((b) => b.to > b.from)) {
		svg.appendChild(svgEl('path', { d: describeArc(BAND_RADIUS, angleForValue(band.from), angleForValue(band.to)), fill: 'none', stroke: TONE[band.tone], 'stroke-width': 6 }));
	}

	// Dial face — ticks + labels (depend only on maxSpeed).
	for (let index = 0; index <= MINOR_DIVISIONS; index++) {
		const tickValue = (index / MINOR_DIVISIONS) * max;
		const major = index % 2 === 0;
		const angle = angleForValue(tickValue);
		const outer = polar(angle, TICK_OUTER);
		const inner = polar(angle, major ? TICK_MAJOR_INNER : TICK_MINOR_INNER);
		svg.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': major ? 1.8 : 0.9, x1: outer.x, x2: inner.x, y1: outer.y, y2: inner.y }));
		if (major) {
			const point = polar(angle, LABEL_RADIUS);
			svg.appendChild(svgText({ ...labelAttrs(9, 'middle', point.x, point.y), fill: MARK }, String(Math.round(tickValue))));
		}
	}

	// Redline marker.
	const redlineValue = opts.redline == null ? null : clamp(toFinite(opts.redline), 0, max);
	if (redlineValue != null) {
		const angle = angleForValue(redlineValue);
		const outer = polar(angle, TICK_OUTER);
		const inner = polar(angle, TICK_MAJOR_INNER - 2);
		svg.appendChild(svgEl('line', { stroke: TONE.danger, 'stroke-linecap': 'round', 'stroke-width': 2.5, x1: outer.x, x2: inner.x, y1: outer.y, y2: inner.y }));
	}

	// Digital readout (value dynamic; unit static).
	const readout = svgText({ ...labelAttrs(16, 'middle', CENTER, CENTER + 44), fill: MARK }, '0');
	svg.appendChild(readout);
	svg.appendChild(svgText({ ...labelAttrs(7, 'middle', CENTER, CENTER + 58), fill: 'var(--hint)', 'letter-spacing': 1 }, unit.toUpperCase()));

	// Needle.
	const needle = centredLine(MARK, 2.6);
	svg.appendChild(needle);
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: CENTER, fill: HUB, r: HUB_RADIUS }));

	function update(speed: number): void {
		const value = clamp(toFinite(speed), 0, max);
		const angle = angleForValue(value);
		setLine(needle, polar(angle + 180, NEEDLE_TAIL), polar(angle, NEEDLE_TIP));
		readout.textContent = String(Math.round(value));
		el.setAttribute('aria-label', airspeedLabel(value, unit));
	}

	update(0);
	return { el, update };
}

//#endregion

//#region Vertical speed indicator

/** Vertical speed indicator (climb / descent rate). */
export interface VsiInstrument {
	el: HTMLDivElement;
	update(verticalSpeed: number): void;
}

export function createVerticalSpeedIndicator(opts: { maxRate?: number } = {}): VsiInstrument {
	const MARK = 'var(--foreground)';
	const HUB = 'var(--warning)';
	const ZERO_ANGLE = 270;
	const HALF_SWEEP = 160;
	const DEFAULT_MAX_RATE = 2000;
	const DIVISIONS = 8;
	const TICK_OUTER = 96;
	const TICK_MAJOR_INNER = 83;
	const TICK_MINOR_INNER = 90;
	const LABEL_RADIUS = 70;
	const NEEDLE_TIP = 86;
	const NEEDLE_TAIL = 16;
	const HUB_RADIUS = 4.5;

	const rawMax = toFinite(opts.maxRate, DEFAULT_MAX_RATE);
	const max = rawMax > 0 ? rawMax : DEFAULT_MAX_RATE;
	const rateToAngle = (rate: number): number => valueToAngle(rate, -max, max, ZERO_ANGLE - HALF_SWEEP, 2 * HALF_SWEEP);

	const { el, svg } = createRoot('vsi', vsiLabel(0));

	// Dial face — ticks + labels (depend only on maxRate).
	linearTicks(-max, max, DIVISIONS).forEach((tickValue, index) => {
		const major = index % 2 === 0;
		const angle = rateToAngle(tickValue);
		const outer = polar(angle, TICK_OUTER);
		const inner = polar(angle, major ? TICK_MAJOR_INNER : TICK_MINOR_INNER);
		svg.appendChild(svgEl('line', { stroke: MARK, 'stroke-linecap': 'round', 'stroke-width': major ? 1.8 : 0.9, x1: outer.x, x2: inner.x, y1: outer.y, y2: inner.y }));
		if (major) {
			const point = polar(angle, LABEL_RADIUS);
			svg.appendChild(svgText({ ...labelAttrs(11, 'middle', point.x, point.y), fill: MARK }, thousandsLabel(tickValue)));
		}
	});

	// Climb / descent hints.
	const upPoint = polar(330, 46);
	const dnPoint = polar(210, 46);
	svg.appendChild(svgText({ ...labelAttrs(7, 'middle', upPoint.x, upPoint.y), fill: 'var(--hint)' }, 'UP'));
	svg.appendChild(svgText({ ...labelAttrs(7, 'middle', dnPoint.x, dnPoint.y), fill: 'var(--hint)' }, 'DN'));

	// Digital readout.
	const readout = svgText({ ...labelAttrs(13, 'middle', CENTER, CENTER + 42), fill: MARK }, '0');
	svg.appendChild(readout);

	// Needle.
	const needle = centredLine(MARK, 2.6);
	svg.appendChild(needle);
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: CENTER, fill: HUB, r: HUB_RADIUS }));

	function update(verticalSpeed: number): void {
		const rate = clamp(toFinite(verticalSpeed), -max, max);
		const rounded = Math.round(rate);
		const angle = rateToAngle(rate);
		setLine(needle, polar(angle + 180, NEEDLE_TAIL), polar(angle, NEEDLE_TIP));
		readout.textContent = `${rounded > 0 ? '+' : ''}${rounded}`;
		el.setAttribute('aria-label', vsiLabel(rounded));
	}

	update(0);
	return { el, update };
}

//#endregion
