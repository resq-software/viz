// ResQ Viz - SVG instruments for ground and surface vehicles
// SPDX-License-Identifier: Apache-2.0
//
// The air fleet has five flight dials (./instruments.ts); until these landed,
// every other vehicle in the scene had none. Three faces, each built on the same
// primitives and the same factory shape — { el, update(...) } with static
// geometry created once and update() mutating only the live nodes:
//
//   createTiltIndicator  roll / pitch against the stability envelope, plus the
//                        server's rollover-risk advisory  (ground)
//   createCompassRose    heading, course over ground and the drift between them,
//                        plus speed over ground                (surface)
//   createDepthGauge     water column, draft and under-keel clearance (surface)
//
// Two rules hold across all three, because a marine or ground readout that
// guesses is worse than one that abstains:
//   - a value the server did not send renders as UNKNOWN_READOUT, never as 0;
//   - a value the server computed (rollover risk, under-keel clearance) is
//     rendered verbatim and never re-derived here.

import {
	CENTER,
	INSTRUMENT_VIEW,
	type Point,
	type SvgAttrs,
	centredLine,
	clamp,
	DEG_PER_RAD,
	UNKNOWN_READOUT,
	createRoot,
	describeArc,
	isReading,
	labelAttrs,
	linearTicks,
	normalizeHeading,
	polar,
	positiveMod,
	setLine,
	setReadout,
	setShown,
	svgEl,
	svgText,
	toFinite,
	valueToAngle,
} from './instrumentPrimitives';

//#region Depth gauge

/** Format a depth in metres, dropping the decimal once the numbers get big. */
function formatDepthMetres(value: number): string {
	return Math.abs(value) >= 100 ? value.toFixed(0) : value.toFixed(1);
}

/**
 * Screen-reader sentence for the depth tape. Absent fields are spoken as unknown
 * rather than skipped — a silent omission is heard as "fine" — and the shallow
 * advisory leads rather than trails, so an operator knows to distrust the numbers
 * that follow it.
 */
function depthGaugeLabel(depthM: number | null, draftM: number | null, clearanceM: number | null, unsafe: boolean): string {
	const parts = [
		depthM === null ? 'no sounding' : `${formatDepthMetres(depthM)} metres of water`,
		draftM === null ? 'draft unknown' : `draft ${formatDepthMetres(draftM)} metres`,
		clearanceM === null ? 'under-keel clearance unknown' : `under-keel clearance ${formatDepthMetres(clearanceM)} metres`,
	];
	return `${unsafe ? 'Shallow water warning, ' : ''}Depth gauge, ${parts.join(', ')}`;
}

/**
 * Vertical depth tape for a surface vessel: water column, seabed, keel, and the
 * under-keel clearance that keeps the hull off the bottom.
 *
 * A tape rather than a round dial on purpose — depth has a hard zero at the
 * surface and a hard floor at the seabed, and a linear scale shows *where the
 * keel sits between them*, which a wrapping needle cannot.
 */
export interface DepthGaugeInstrument {
	el: HTMLDivElement;
	/**
	 * @param depthM Water column under the vessel (`SurfaceDomainState.waterDepthM`), metres.
	 * @param draftM How deep the hull sits (`SurfaceDomainState.draftM`), metres.
	 * @param underKeelClearanceM The server's `underKeelClearanceM`, metres. Rendered
	 *   verbatim — never re-derived from depth less draft, because the field is
	 *   carried explicitly so a warning never depends on a client subtracting correctly.
	 * @param unsafe The server's `hasUnsafeUnderKeelClearance` advisory.
	 *
	 * Every argument tolerates `null`, `undefined` and `NaN`; each renders as an
	 * explicit unknown rather than a plausible zero.
	 */
	update(depthM: number | null, draftM: number | null, underKeelClearanceM: number | null, unsafe?: boolean): void;
}

export function createDepthGauge(opts: { maxDepthM?: number } = {}): DepthGaugeInstrument {
	const MARK = 'var(--foreground)';
	const HINT = 'var(--hint)';
	const GRID = 'var(--border)';
	const WATER = 'var(--info)';
	const SEABED = 'var(--warning)';
	const DANGER = 'var(--destructive)';
	const BADGE_TEXT = 'var(--primary-foreground)';

	const TAPE_X = 62;
	const TAPE_W = 76;
	const TAPE_TOP = 30;
	const TAPE_H = 126;
	const TAPE_BOTTOM = TAPE_TOP + TAPE_H;
	const TAPE_MID_X = TAPE_X + TAPE_W / 2;
	const TAPE_RIGHT = TAPE_X + TAPE_W;
	const TICK_DIVISIONS = 5;
	const TICK_LABEL_X = TAPE_X - 4;
	const MARKER_HALF = 6;
	const MARKER_GAP = 2;
	const BADGE_X = 150;
	const BADGE_Y = 58;
	const BADGE_W = 44;
	const BADGE_H = 13;
	const LABEL_ROW_Y = 170;
	const VALUE_ROW_Y = 187;
	const DEFAULT_MAX_DEPTH = 50;

	const rawMax = toFinite(opts.maxDepthM, DEFAULT_MAX_DEPTH);
	const max = rawMax > 0 ? rawMax : DEFAULT_MAX_DEPTH;

	// Full scale is fixed at construction, as the altimeter's and airspeed dial's
	// are: the ticks are static geometry, and a scale that tracked the live
	// sounding would redraw the face under the operator on every frame.
	const depthToY = (metres: number): number => TAPE_TOP + (clamp(metres, 0, max) / max) * TAPE_H;

	const { el, svg } = createRoot('depth', depthGaugeLabel(null, null, null, false));

	// Header: instrument source on the left, units on the right.
	svg.appendChild(svgText({ ...labelAttrs(8, 'start', 6, 13), fill: HINT, 'letter-spacing': 0.5 }, 'SOUNDER'));
	svg.appendChild(svgText({ ...labelAttrs(8, 'end', 194, 13), fill: HINT, 'letter-spacing': 0.5 }, 'METRES'));

	// Water column, then the seabed band below it. Both are sized in update();
	// with no sounding the column has zero height and the band is hidden, so an
	// empty tape can never be mistaken for a full one.
	const waterRect = svgEl('rect', { fill: WATER, 'fill-opacity': 0.18, height: 0, width: TAPE_W, x: TAPE_X, y: TAPE_TOP });
	const seabedRect = svgEl('rect', { fill: SEABED, 'fill-opacity': 0.35, height: 0, width: TAPE_W, x: TAPE_X, y: TAPE_BOTTOM });
	svg.appendChild(waterRect);
	svg.appendChild(seabedRect);

	// Depth scale — static, since the full scale is fixed at construction.
	for (const tickValue of linearTicks(0, max, TICK_DIVISIONS)) {
		const y = depthToY(tickValue);
		svg.appendChild(svgEl('line', { stroke: GRID, 'stroke-width': 1, x1: TAPE_X, x2: TAPE_RIGHT, y1: y, y2: y }));
		svg.appendChild(svgText({ ...labelAttrs(8, 'end', TICK_LABEL_X, y), fill: HINT }, tickValue.toFixed(0)));
	}

	// Tape frame: solid while a sounding is coming in, dashed when it is not.
	const frame = svgEl('rect', { fill: 'none', height: TAPE_H, stroke: GRID, 'stroke-dasharray': '3 3', 'stroke-width': 1, width: TAPE_W, x: TAPE_X, y: TAPE_TOP });
	svg.appendChild(frame);

	// Surface reference.
	svg.appendChild(svgEl('line', { stroke: MARK, 'stroke-width': 1.6, x1: TAPE_X, x2: TAPE_RIGHT, y1: TAPE_TOP, y2: TAPE_TOP }));

	// Shown across an empty tape when the sounder gives us nothing.
	const noSounding = svgText({ ...labelAttrs(9, 'middle', TAPE_MID_X, TAPE_TOP + TAPE_H / 2), fill: HINT, 'letter-spacing': 0.5 }, 'NO SOUNDING');
	svg.appendChild(noSounding);

	// Keel, on the readout side: where the hull's bottom sits in the column.
	const keelGroup = svgEl('g', {});
	const keelLine = svgEl('line', { stroke: MARK, 'stroke-width': 2, x1: TAPE_X, x2: TAPE_RIGHT, y1: TAPE_TOP, y2: TAPE_TOP });
	const keelMark = svgEl('polygon', { fill: MARK, points: `${TAPE_RIGHT},${TAPE_TOP} ${TAPE_RIGHT + MARKER_HALF + MARKER_GAP},${TAPE_TOP - MARKER_HALF} ${TAPE_RIGHT + MARKER_HALF + MARKER_GAP},${TAPE_TOP + MARKER_HALF}` });
	keelGroup.appendChild(keelLine);
	keelGroup.appendChild(keelMark);
	svg.appendChild(keelGroup);

	// Shallow-water badge. Deliberately a static colour-and-badge change rather
	// than a blink: it reads identically under `prefers-reduced-motion`, so there
	// is no motion to suppress and no alarm lost by suppressing it.
	const badge = svgEl('g', {});
	badge.appendChild(svgEl('rect', { fill: DANGER, height: BADGE_H, rx: 3, width: BADGE_W, x: BADGE_X, y: BADGE_Y }));
	badge.appendChild(svgText({ ...labelAttrs(8, 'middle', BADGE_X + BADGE_W / 2, BADGE_Y + BADGE_H / 2), fill: BADGE_TEXT, 'letter-spacing': 0.6 }, 'SHALLOW'));
	svg.appendChild(badge);

	// Readouts: sounding, draft, and the server's clearance.
	svg.appendChild(svgText({ ...labelAttrs(8, 'start', 8, LABEL_ROW_Y), fill: HINT }, 'DEPTH'));
	svg.appendChild(svgText({ ...labelAttrs(8, 'middle', CENTER, LABEL_ROW_Y), fill: HINT }, 'DRAFT'));
	svg.appendChild(svgText({ ...labelAttrs(8, 'end', 192, LABEL_ROW_Y), fill: HINT }, 'CLEARANCE'));
	const depthValue = svgText({ ...labelAttrs(15, 'start', 8, VALUE_ROW_Y), fill: HINT }, UNKNOWN_READOUT);
	const draftValue = svgText({ ...labelAttrs(15, 'middle', CENTER, VALUE_ROW_Y), fill: HINT }, UNKNOWN_READOUT);
	const clearanceValue = svgText({ ...labelAttrs(15, 'end', 192, VALUE_ROW_Y), fill: HINT }, UNKNOWN_READOUT);
	svg.appendChild(depthValue);
	svg.appendChild(draftValue);
	svg.appendChild(clearanceValue);

	function update(depthM: number | null, draftM: number | null, underKeelClearanceM: number | null, unsafe?: boolean): void {
		const depth = isReading(depthM) ? depthM : null;
		const draft = isReading(draftM) ? draftM : null;
		const clearance = isReading(underKeelClearanceM) ? underKeelClearanceM : null;
		// The advisory is the server's to raise, and is honoured even when the
		// clearance figure itself is missing: a warning shown without its number
		// is recoverable, a warning dropped because the number was absent is not.
		const shallow = unsafe === true;

		const seabedY = depth === null ? TAPE_BOTTOM : depthToY(depth);
		waterRect.setAttribute('height', String(depth === null ? 0 : seabedY - TAPE_TOP));
		seabedRect.setAttribute('y', String(seabedY));
		seabedRect.setAttribute('height', String(TAPE_BOTTOM - seabedY));
		setShown(seabedRect, depth !== null);
		frame.setAttribute('stroke-dasharray', depth === null ? '3 3' : 'none');
		setShown(noSounding, depth === null);

		const keelY = draft === null ? TAPE_TOP : depthToY(draft);
		const keelColor = shallow ? DANGER : MARK;
		keelLine.setAttribute('y1', String(keelY));
		keelLine.setAttribute('y2', String(keelY));
		keelLine.setAttribute('stroke', keelColor);
		keelMark.setAttribute('fill', keelColor);
		keelMark.setAttribute('points', `${TAPE_RIGHT},${keelY} ${TAPE_RIGHT + MARKER_HALF + MARKER_GAP},${keelY - MARKER_HALF} ${TAPE_RIGHT + MARKER_HALF + MARKER_GAP},${keelY + MARKER_HALF}`);
		setShown(keelGroup, draft !== null);
		setShown(badge, shallow);

		setReadout(depthValue, depth === null ? UNKNOWN_READOUT : formatDepthMetres(depth), depth === null ? HINT : MARK);
		setReadout(draftValue, draft === null ? UNKNOWN_READOUT : formatDepthMetres(draft), draft === null ? HINT : MARK);
		setReadout(clearanceValue, clearance === null ? UNKNOWN_READOUT : formatDepthMetres(clearance), shallow ? DANGER : clearance === null ? HINT : MARK);
		el.setAttribute('aria-label', depthGaugeLabel(depth, draft, clearance, shallow));
	}

	// Starts unknown, not zero: an instrument that has never been fed telemetry
	// has no sounding, and must not claim the vessel is sitting in 0 m of water.
	update(null, null, null, false);
	return { el, update };
}

//#endregion

//#region Tilt indicator (ground vehicles)

/** Percent scale for the advisory readout. */
const TILT_PERCENT = 100;

/** Signed whole degrees with an explicit sign, or an unmistakable dash when absent. */
function formatTiltDegrees(radians: number | null): string {
	if (radians === null) return `${UNKNOWN_READOUT}°`;
	const rounded = Math.round(radians * DEG_PER_RAD);
	return `${rounded > 0 ? '+' : ''}${rounded}°`;
}

/**
 * Footer readout. Always carries ADVISORY: `rolloverRisk` is decision support
 * from the simulation's stability model, not a measured limit, and the face
 * should never let an operator forget that.
 */
function formatTiltRisk(risk: number | null): string {
	const percent = risk === null ? UNKNOWN_READOUT : String(Math.round(risk * TILT_PERCENT));
	return `RISK ${percent}% · ADVISORY`;
}

/** Screen-reader sentence; absent inputs are spoken as unavailable, never as zero. */
function formatTiltLabel(rollRad: number | null, pitchRad: number | null, risk: number | null): string {
	let attitude = 'attitude unavailable';
	if (rollRad !== null && pitchRad !== null) {
		const r = Math.round(rollRad * DEG_PER_RAD);
		const p = Math.round(pitchRad * DEG_PER_RAD);
		const rollPart = r === 0 ? 'roll level' : `roll ${Math.abs(r)} degrees ${r > 0 ? 'right' : 'left'}`;
		const pitchPart = p === 0 ? 'pitch level' : `pitch ${Math.abs(p)} degrees ${p > 0 ? 'up' : 'down'}`;
		attitude = `${rollPart}, ${pitchPart}`;
	}
	const advisory =
		risk === null
			? 'advisory rollover risk unavailable'
			: `advisory rollover risk ${Math.round(risk * TILT_PERCENT)} percent of the static stability limit`;
	return `Tilt indicator, ${attitude}, ${advisory}`;
}

/**
 * Ground-vehicle inclinometer with an advisory rollover-margin ring.
 *
 * `update()` mirrors `GroundDomainState`: `roll` / `pitch` are `rollRad` /
 * `pitchRad` (radians, positive = right-side-down / nose-up) and `rolloverRisk`
 * is the simulation's own 0–1 proximity to the static stability limit. Any
 * argument may be `null`, `undefined` or `NaN`; each renders as an explicitly
 * unknown state rather than a plausible zero.
 */
export interface TiltInstrument {
	el: HTMLDivElement;
	update(roll: number | null, pitch: number | null, rolloverRisk?: number | null): void;
}

/**
 * Build a tilt / rollover-margin indicator.
 *
 * Two independent things are drawn, and keeping them separate is the point:
 *
 * - The **dot** plots measured attitude, normalised per axis against
 *   `rollLimitRad` / `pitchLimitRad`, so combined tilt (the case that actually
 *   tips a vehicle) reads at a glance against the dashed limit ring.
 * - The **ring** fills with `rolloverRisk` exactly as the simulation reported
 *   it, and is the only thing that escalates the colour. It is *not* recomputed
 *   here, so the dot and the ring can disagree — see the note on the bands.
 *
 * The bands are not free parameters. The server scales risk against an inferred
 * static stability angle that is the platform's declared cross-slope limit
 * divided by a fixed operational margin, so risk reaches exactly that margin at
 * the declared limit — the same instant the server raises a critical fault,
 * emits an alert and cuts the speed ceiling. CAUTION_RISK is therefore that
 * margin and nothing else: a lower value cries wolf inside the envelope, a
 * higher one stays green while the vehicle is already being derated.
 * ALERT_RISK is 1.0 because that is where risk saturates — the inferred tipping
 * angle itself. A contract test pins both against the server constants.
 *
 * Escalation is colour (success → warning → destructive), ring fill, and at or
 * past the limit a halo around the dot. The halo pulses only inside a
 * `prefers-reduced-motion: no-preference` query, so a reduce-motion operator
 * gets the same steady ring with no JS branch to keep in sync.
 *
 * @param opts.rollLimitRad Roll treated as the rollover limit. Defaults to 30°.
 * @param opts.pitchLimitRad Pitch treated as the pitchover limit. Defaults to 30°.
 */
export function createTiltIndicator(opts: { rollLimitRad?: number; pitchLimitRad?: number } = {}): TiltInstrument {
	const MARK = 'var(--foreground)';
	const HINT = 'var(--hint)';
	const GRID = 'var(--border)';
	const PANEL = 'var(--card)';
	const SAFE = 'var(--success)';
	const CAUTION = 'var(--warning)';
	const DANGER = 'var(--destructive)';

	/** Vertical centre of the plot, lifted to leave room for the footer readout. */
	const PLOT_CENTER_Y = 104;
	/** Radius at which the combined-tilt fraction equals 1.0 (the limit ring). */
	const LIMIT_RADIUS = 52;
	const LIMIT_CIRCUMFERENCE = 2 * Math.PI * LIMIT_RADIUS;
	/** Fraction beyond the limit that still renders inside the box. */
	const MAX_FRACTION = 1.4;
	/**
	 * Risk at which the face turns amber.
	 *
	 * This is the server's `GroundContactGeometry.OperationalCrossSlopeMargin`,
	 * not a taste decision. Risk is `|crossSlope| / (declaredLimit / margin)`, so
	 * at the declared limit it is exactly `margin` — and that is the tick on
	 * which the server raises a critical rollover fault, emits an alert event and
	 * derates the speed ceiling to a quarter. Any higher value leaves the gauge
	 * green through a band where the vehicle is already being slowed for the
	 * reason this instrument exists to show.
	 */
	const CAUTION_RISK = 0.6;
	/**
	 * Risk at which the face turns red and the halo appears.
	 *
	 * 1.0 is where the server clamps: the inferred static tipping angle. Past
	 * this the vehicle is not being advised, it is over.
	 */
	const ALERT_RISK = 1;
	const CROSSHAIR = 68;
	const DOT_RADIUS = 6;
	const HALO_RADIUS = 10;
	const ORIGIN_RADIUS = 2;
	const RISK_ARC_WIDTH = 3.2;
	/** 30° per axis — the conventional default static-stability envelope. */
	const DEFAULT_LIMIT_RAD = Math.PI / 6;
	/** 90°: past this, tilt is no longer a display problem. */
	const DISPLAY_LIMIT_RAD = Math.PI / 2;

	const rawRollLimit = toFinite(opts.rollLimitRad, DEFAULT_LIMIT_RAD);
	const rollMaxRad = rawRollLimit > 0 ? rawRollLimit : DEFAULT_LIMIT_RAD;
	const rawPitchLimit = toFinite(opts.pitchLimitRad, DEFAULT_LIMIT_RAD);
	const pitchMaxRad = rawPitchLimit > 0 ? rawPitchLimit : DEFAULT_LIMIT_RAD;

	/** Token for the advisory risk. Unknown is drawn neutral — never as "safe". */
	const riskColor = (risk: number | null): string => {
		if (risk === null) return HINT;
		if (risk >= ALERT_RISK) return DANGER;
		if (risk >= CAUTION_RISK) return CAUTION;
		return SAFE;
	};

	const { el, svg } = createRoot('tilt', formatTiltLabel(null, null, null));

	// The `.tilt-alert` pulse lives in ui/cockpit.css, including its reduced-motion
	// gate. An SVG <style> is document-scoped, so injecting it here would restate
	// the same rules once per mounted instrument.

	// Static plot frame: crosshair, caution ring, dashed stability-limit ring.
	svg.appendChild(svgEl('line', { stroke: GRID, 'stroke-width': 1, x1: CENTER - CROSSHAIR, x2: CENTER + CROSSHAIR, y1: PLOT_CENTER_Y, y2: PLOT_CENTER_Y }));
	svg.appendChild(svgEl('line', { stroke: GRID, 'stroke-width': 1, x1: CENTER, x2: CENTER, y1: PLOT_CENTER_Y - CROSSHAIR, y2: PLOT_CENTER_Y + CROSSHAIR }));
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: PLOT_CENTER_Y, fill: 'none', r: LIMIT_RADIUS * CAUTION_RISK, stroke: GRID, 'stroke-width': 1 }));
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: PLOT_CENTER_Y, fill: 'none', r: LIMIT_RADIUS, stroke: CAUTION, 'stroke-dasharray': '4 3', 'stroke-width': 1.4 }));

	// Per-axis limit annotations (depend only on the configured envelope).
	svg.appendChild(svgText({ ...labelAttrs(7, 'start', CENTER + LIMIT_RADIUS + 4, PLOT_CENTER_Y - 7), fill: HINT }, `${Math.round(rollMaxRad * DEG_PER_RAD)}°`));
	svg.appendChild(svgText({ ...labelAttrs(7, 'middle', CENTER + 13, PLOT_CENTER_Y - LIMIT_RADIUS - 5), fill: HINT }, `${Math.round(pitchMaxRad * DEG_PER_RAD)}°`));

	// Advisory risk ring: a progress sweep over the limit ring, clockwise from
	// 12 o'clock. Drawn with stroke-dasharray so it needs no arc geometry.
	const riskArc = svgEl('circle', {
		cx: CENTER,
		cy: PLOT_CENTER_Y,
		fill: 'none',
		r: LIMIT_RADIUS,
		stroke: HINT,
		'stroke-dasharray': `0 ${LIMIT_CIRCUMFERENCE}`,
		'stroke-width': RISK_ARC_WIDTH,
		transform: `rotate(-90 ${CENTER} ${PLOT_CENTER_Y})`,
	});
	svg.appendChild(riskArc);

	// Digital readouts, pinned to the free corners.
	svg.appendChild(svgText({ ...labelAttrs(8, 'start', 12, 20), fill: HINT }, 'ROLL'));
	const rollReadout = svgText({ ...labelAttrs(15, 'start', 12, 36), fill: HINT }, formatTiltDegrees(null));
	svg.appendChild(rollReadout);
	svg.appendChild(svgText({ ...labelAttrs(8, 'end', 188, 20), fill: HINT }, 'PITCH'));
	const pitchReadout = svgText({ ...labelAttrs(15, 'end', 188, 36), fill: HINT }, formatTiltDegrees(null));
	svg.appendChild(pitchReadout);

	// Envelope usage — the number that matters on a slope.
	const riskReadout = svgText({ ...labelAttrs(9, 'middle', CENTER, 186), fill: HINT, 'letter-spacing': 0.5 }, formatTiltRisk(null));
	svg.appendChild(riskReadout);

	// Live tilt vector, alert halo, plotted attitude, plot origin.
	const vector = centredLine(HINT, 2);
	vector.setAttribute('stroke-opacity', '0.55');
	svg.appendChild(vector);
	const halo = svgEl('circle', { class: 'tilt-alert', cx: CENTER, cy: PLOT_CENTER_Y, fill: 'none', r: HALO_RADIUS, stroke: DANGER, 'stroke-width': 1.6 });
	svg.appendChild(halo);
	const dot = svgEl('circle', { cx: CENTER, cy: PLOT_CENTER_Y, fill: HINT, r: DOT_RADIUS });
	svg.appendChild(dot);
	const origin = svgEl('circle', { cx: CENTER, cy: PLOT_CENTER_Y, fill: GRID, r: ORIGIN_RADIUS });
	svg.appendChild(origin);

	// Absent attitude: the plot is emptied and says so, rather than parking the
	// dot at the origin where it would read as "level".
	const noAttitude = svgEl('g', {});
	noAttitude.appendChild(svgEl('rect', { fill: PANEL, height: 18, rx: 3, width: 84, x: CENTER - 42, y: PLOT_CENTER_Y - 9 }));
	noAttitude.appendChild(svgText({ ...labelAttrs(10, 'middle', CENTER, PLOT_CENTER_Y), fill: CAUTION, 'letter-spacing': 1 }, 'NO ATTITUDE'));
	svg.appendChild(noAttitude);

	/**
	 * Coarse state for CSS, tests and anything aggregating alarms.
	 *
	 * Driven by the RISK alone. Attitude going absent must not downgrade a live
	 * alarm: the two arrive independently, and a vehicle at 150% of its stability
	 * limit is at 150% whether or not its roll and pitch are also being reported.
	 * Keying this on attitude made `update(null, null, 1.5)` paint a destructive
	 * ring, read "RISK 150% · ADVISORY" and still publish `data-state="unknown"`,
	 * so anything watching the attribute saw no alarm at all — the sibling depth
	 * gauge states the rule this violates: a warning shown without its number is
	 * recoverable, a warning dropped because a number was absent is not.
	 */
	const stateName = (risk: number | null): string => {
		if (risk === null) return 'unknown';
		if (risk >= ALERT_RISK) return 'limit';
		if (risk >= CAUTION_RISK) return 'caution';
		return 'nominal';
	};

	function update(roll: number | null, pitch: number | null, rolloverRisk?: number | null): void {
		const rollRad = isReading(roll) ? clamp(roll, -DISPLAY_LIMIT_RAD, DISPLAY_LIMIT_RAD) : null;
		const pitchRad = isReading(pitch) ? clamp(pitch, -DISPLAY_LIMIT_RAD, DISPLAY_LIMIT_RAD) : null;
		const hasAttitude = rollRad !== null && pitchRad !== null;
		// Reported, not derived. An overshoot past 1 is a real alarm and is shown at
		// its true value; a NEGATIVE figure is not a safe vehicle, it is a broken
		// feed, so it reads as unknown rather than being clamped to zero. Clamping
		// rendered a corrupt value as "RISK 0%" in the success colour with
		// `data-state="nominal"` — the most reassuring reading on the dial, which is
		// the plausible-zero failure the rest of these factories take care to avoid.
		const risk = isReading(rolloverRisk) && rolloverRisk >= 0 ? rolloverRisk : null;
		const status = riskColor(risk);

		const sweep = (risk === null ? 0 : clamp(risk, 0, 1)) * LIMIT_CIRCUMFERENCE;
		riskArc.setAttribute('stroke', status);
		riskArc.setAttribute('stroke-dasharray', `${sweep} ${LIMIT_CIRCUMFERENCE}`);

		rollReadout.textContent = formatTiltDegrees(rollRad);
		rollReadout.setAttribute('fill', rollRad === null ? HINT : MARK);
		pitchReadout.textContent = formatTiltDegrees(pitchRad);
		pitchReadout.setAttribute('fill', pitchRad === null ? HINT : MARK);
		riskReadout.textContent = formatTiltRisk(risk);
		riskReadout.setAttribute('fill', status);

		if (hasAttitude) {
			const rollFraction = rollRad / rollMaxRad;
			const pitchFraction = pitchRad / pitchMaxRad;
			const magnitude = Math.hypot(rollFraction, pitchFraction);
			// Squash the point back inside the box without distorting its bearing.
			const squash = magnitude > MAX_FRACTION ? MAX_FRACTION / magnitude : 1;
			const tip = {
				x: CENTER + rollFraction * squash * LIMIT_RADIUS,
				y: PLOT_CENTER_Y - pitchFraction * squash * LIMIT_RADIUS,
			};
			setLine(vector, { x: CENTER, y: PLOT_CENTER_Y }, tip);
			vector.setAttribute('stroke', status);
			dot.setAttribute('cx', String(tip.x));
			dot.setAttribute('cy', String(tip.y));
			dot.setAttribute('fill', status);
			halo.setAttribute('cx', String(tip.x));
			halo.setAttribute('cy', String(tip.y));
		}

		setShown(vector, hasAttitude);
		setShown(dot, hasAttitude);
		setShown(origin, hasAttitude);
		setShown(noAttitude, !hasAttitude);
		setShown(halo, hasAttitude && risk !== null && risk >= ALERT_RISK);

		el.setAttribute('data-state', stateName(risk));
		el.setAttribute('aria-label', formatTiltLabel(rollRad, pitchRad, risk));
	}

	// Opens unknown, not level — nothing has been reported yet.
	update(null, null, null);
	return { el, update };
}

//#endregion

//#region Compass rose

/** Divergence below which heading and course over ground count as aligned, in degrees. */
const ROSE_DRIFT_DEADBAND_DEG = 0.5;

/** Cached `prefers-reduced-motion` answer; `null` until first asked. */
let _roseMotionReduced: boolean | null = null;

/**
 * Whether the operator has asked for reduced motion. Cached on first use and
 * kept current by the media query's own change event, so toggling the OS setting
 * mid-session is picked up on the next frame. An environment that cannot answer
 * — no `matchMedia`, or a server-side render — counts as "reduce": when the
 * preference is unknown the safe default is to hold still.
 */
function roseReducedMotion(): boolean {
	if (_roseMotionReduced !== null) return _roseMotionReduced;
	if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
		_roseMotionReduced = true;
		return true;
	}
	const query = window.matchMedia('(prefers-reduced-motion: reduce)');
	_roseMotionReduced = query.matches;
	query.addEventListener('change', (event) => { _roseMotionReduced = event.matches; });
	return _roseMotionReduced;
}

/** Wrap a bearing difference into (−180, 180]; positive is starboard, negative port. */
function normalizeRoseDelta(value: number): number {
	const wrapped = normalizeHeading(value + 180) - 180;
	// The range is (−180, 180], so an exact half turn is starboard, not port.
	return wrapped === -180 ? 180 : wrapped;
}

/** Three-digit marine bearing, so 7° reads as `007`. */
function formatRoseBearing(deg: number): string {
	return String(Math.round(normalizeHeading(deg)) % 360).padStart(3, '0');
}

/**
 * Screen-reader sentence describing the navigational picture. Missing fields are
 * spoken as unknown rather than dropped: a label that simply omits the course
 * reads as though the vessel were tracking its heading exactly, which is the
 * claim the instrument is there to test.
 */
function compassRoseLabel(
	headingDeg: number | null,
	courseDeg: number | null,
	knots: number | null,
	driftDeg: number | null,
): string {
	if (headingDeg === null && courseDeg === null) return 'Compass rose, no heading or course data';
	const parts = [
		headingDeg === null ? 'heading unknown' : `heading ${formatRoseBearing(headingDeg)} degrees`,
		courseDeg === null ? 'course over ground unknown' : `course over ground ${formatRoseBearing(courseDeg)} degrees`,
		knots === null ? 'speed over ground unknown' : `speed over ground ${knots.toFixed(1)} knots`,
	];
	if (driftDeg !== null) {
		parts.push(
			Math.abs(driftDeg) < ROSE_DRIFT_DEADBAND_DEG
				? 'no drift'
				: `${Math.abs(Math.round(driftDeg))} degrees ${driftDeg > 0 ? 'starboard' : 'port'} drift`,
		);
	}
	return `Compass rose, ${parts.join(', ')}`;
}

/**
 * Marine north-up compass rose: where the bow points, where the vessel is
 * actually going, and the angle between the two.
 *
 * The card deliberately does **not** rotate, unlike the aviation heading
 * indicator further up this file. A fixed north-up rose lets the hull symbol
 * (heading) and the course-over-ground arm be drawn as two separate arms, so the
 * crab angle that set and drift produce is visible as a shape rather than as the
 * difference between two numbers. Collapsing them into a single needle would
 * hide the one thing this instrument exists to show.
 */
export interface CompassRoseInstrument {
	el: HTMLDivElement;
	/**
	 * @param headingRad `SurfaceDomainState.headingRad` — the direction the bow
	 *   points, radians clockwise from true north.
	 * @param courseRad `SurfaceDomainState.courseOverGroundRad`, same convention —
	 *   the track the vessel is making good over the seabed.
	 * @param speedMps `SurfaceDomainState.speedOverGroundMps`. Read out in knots
	 *   and used to scale the course arm's length.
	 *
	 * Every argument tolerates `null`, `undefined` and `NaN`. A missing bearing
	 * blanks its symbol and reads `—`; it is never drawn pointing north, which
	 * would be a plausible and wrong reading rather than an obviously absent one.
	 */
	update(headingRad: number | null, courseRad: number | null, speedMps: number | null): void;
}

export function createCompassRose(): CompassRoseInstrument {
	const MARK = 'var(--foreground)';
	const HINT = 'var(--hint)';
	const GRID = 'var(--border)';
	const HULL = 'var(--foreground)';
	const COURSE = 'var(--info)';
	const ALERT = 'var(--warning)';

	/** Knots per metre-per-second — the rose reads in knots, telemetry carries m/s. */
	const KNOTS_PER_MPS = 1.943_844_492_440_605;
	const TICK_STEP = 10;
	const TICK_OUTER = 92;
	const TICK_MINOR_INNER = 85;
	const TICK_MAJOR_INNER = 78;
	const CARDINAL_RADIUS = 68;
	const HULL_LENGTH = 40;
	const HULL_HALF_BEAM = 12;
	const HULL_STERN = 16;
	const HULL_TRANSOM = 10;
	const COURSE_MIN = 30;
	const COURSE_MAX = 74;
	/** Speed, in knots, at which the course arm reaches its full length. */
	const SPEED_FULL_SCALE_KT = 12;
	/** Arm length used when the course is known but the speed is not. */
	const UNKNOWN_SPEED_FRACTION = 0.5;
	const HUB_RADIUS = 2.5;
	const TIP_RADIUS = 3.4;
	const SMOOTHING_MS = 160;
	const CARDINALS: ReadonlyArray<{ bearing: number; text: string }> = [
		{ bearing: 0, text: 'N' }, { bearing: 45, text: 'NE' }, { bearing: 90, text: 'E' }, { bearing: 135, text: 'SE' },
		{ bearing: 180, text: 'S' }, { bearing: 225, text: 'SW' }, { bearing: 270, text: 'W' }, { bearing: 315, text: 'NW' },
	];

	const { el, svg } = createRoot('compass-rose', compassRoseLabel(null, null, null, null));

	// Ten-degree tick ring; every third tick is a major. Static — the rose is
	// north-up, so nothing about the card moves.
	for (let bearing = 0; bearing < 360; bearing += TICK_STEP) {
		const major = bearing % 30 === 0;
		const outer = polar(bearing, TICK_OUTER);
		const inner = polar(bearing, major ? TICK_MAJOR_INNER : TICK_MINOR_INNER);
		svg.appendChild(svgEl('line', { stroke: GRID, 'stroke-linecap': 'round', 'stroke-width': major ? 1.6 : 0.9, x1: outer.x, x2: inner.x, y1: outer.y, y2: inner.y }));
	}
	for (const { bearing, text } of CARDINALS) {
		const cardinal = bearing % 90 === 0;
		const point = polar(bearing, CARDINAL_RADIUS);
		svg.appendChild(svgText({ ...labelAttrs(cardinal ? 12 : 8, 'middle', point.x, point.y), fill: cardinal ? MARK : HINT }, text));
	}

	// Course-over-ground arm, drawn due north inside a group that rotates it onto
	// the true bearing; its length tracks speed. Dashed, because the track made
	// good is inferred from successive fixes rather than sensed like the heading.
	const courseGroup = svgEl('g', { transform: `rotate(0 ${CENTER} ${CENTER})` });
	const courseArm = svgEl('line', { stroke: COURSE, 'stroke-dasharray': '5 3', 'stroke-linecap': 'round', 'stroke-width': 2.4, x1: CENTER, x2: CENTER, y1: CENTER, y2: CENTER });
	const courseTip = svgEl('circle', { cx: CENTER, cy: CENTER, fill: COURSE, r: TIP_RADIUS, stroke: 'none', 'stroke-width': 1.4 });
	courseGroup.appendChild(courseArm);
	courseGroup.appendChild(courseTip);
	svg.appendChild(courseGroup);

	// Hull symbol, bow at the top of its own local frame, rotated onto the heading.
	const hull = svgEl('polygon', {
		fill: HULL,
		points: `${CENTER},${CENTER - HULL_LENGTH} ${CENTER + HULL_HALF_BEAM},${CENTER + HULL_STERN} ${CENTER},${CENTER + HULL_TRANSOM} ${CENTER - HULL_HALF_BEAM},${CENTER + HULL_STERN}`,
		transform: `rotate(0 ${CENTER} ${CENTER})`,
	});
	svg.appendChild(hull);
	svg.appendChild(svgEl('circle', { cx: CENTER, cy: CENTER, fill: GRID, r: HUB_RADIUS }));

	// Shown across the middle when neither bearing is coming in, so an operator
	// glancing at a bare rose is told it is empty rather than left to infer it.
	const noData = svgText({ ...labelAttrs(10, 'middle', CENTER, CENTER + 24), fill: ALERT, 'letter-spacing': 1 }, 'NO DATA');
	svg.appendChild(noData);

	/** One corner readout: dim caption above, value below. Returns the value node. */
	function corner(anchor: string, x: number, captionY: number, valueY: number, caption: string): SVGTextElement {
		svg.appendChild(svgText({ ...labelAttrs(8, anchor, x, captionY), fill: HINT, 'letter-spacing': 0.5 }, caption));
		const value = svgText({ ...labelAttrs(14, anchor, x, valueY), fill: HINT }, UNKNOWN_READOUT);
		svg.appendChild(value);
		return value;
	}

	// Readouts, pinned to the corners the rose leaves free.
	const headingValue = corner('start', 8, 15, 28, 'HDG');
	const courseValue = corner('end', 192, 15, 28, 'COG');
	const speedValue = corner('start', 8, 177, 189, 'SOG');
	const driftValue = corner('end', 192, 177, 189, 'DRIFT');

	// Rotations accumulate instead of wrapping: handing the DOM 361° rather than
	// 1° keeps a 359°→1° step a two-degree move, which matters once a transition
	// is interpolating the number — wrapping would sweep it the long way round.
	let hullTurn = 0;
	let courseTurn = 0;
	let appliedTransition: string | null = null;

	// The bearing is written as a transform attribute, so the commanded value is
	// in the DOM whether or not the browser animates it: a browser that ignores
	// the transition still shows the truth, just without the tween. The
	// transition is pure enhancement — it smooths the step between 10 Hz
	// telemetry frames — and is withheld entirely under `prefers-reduced-motion`,
	// re-checked each update because the setting can be toggled mid-session.
	function applyMotionPreference(): void {
		const transition = roseReducedMotion() ? 'none' : `transform ${SMOOTHING_MS}ms linear`;
		if (transition === appliedTransition) return;
		appliedTransition = transition;
		hull.style.transition = transition;
		courseGroup.style.transition = transition;
	}

	function update(headingRad: number | null, courseRad: number | null, speedMps: number | null): void {
		const headingDeg = isReading(headingRad) ? normalizeHeading(headingRad * DEG_PER_RAD) : null;
		const courseDeg = isReading(courseRad) ? normalizeHeading(courseRad * DEG_PER_RAD) : null;
		const knots = isReading(speedMps) ? Math.abs(speedMps * KNOTS_PER_MPS) : null;
		// Drift is the whole point of the instrument, and it only exists when both
		// bearings do — one of them missing yields no angle, never a reassuring 0.
		const drift = headingDeg === null || courseDeg === null ? null : normalizeRoseDelta(courseDeg - headingDeg);
		const drifting = drift !== null && Math.abs(drift) >= ROSE_DRIFT_DEADBAND_DEG;

		applyMotionPreference();

		// An absent bearing blanks its symbol outright. A hull left pointing north
		// or an arm collapsed onto the hub would both read as real data.
		if (headingDeg !== null) {
			hullTurn += normalizeRoseDelta(headingDeg - hullTurn);
			hull.setAttribute('transform', `rotate(${hullTurn} ${CENTER} ${CENTER})`);
		}
		if (courseDeg !== null) {
			courseTurn += normalizeRoseDelta(courseDeg - courseTurn);
			courseGroup.setAttribute('transform', `rotate(${courseTurn} ${CENTER} ${CENTER})`);
		}
		setShown(hull, headingDeg !== null);
		setShown(courseGroup, courseDeg !== null);
		setShown(noData, headingDeg === null && courseDeg === null);

		// An unknown speed still has a direction worth drawing, so the arm keeps a
		// nominal length and swaps its filled tip for a hollow one: the solid disc
		// is what says "this length is a measurement".
		const fraction = knots === null ? UNKNOWN_SPEED_FRACTION : Math.min(1, knots / SPEED_FULL_SCALE_KT);
		const tip = polar(0, COURSE_MIN + fraction * (COURSE_MAX - COURSE_MIN));
		setLine(courseArm, { x: CENTER, y: CENTER }, tip);
		courseTip.setAttribute('cx', String(tip.x));
		courseTip.setAttribute('cy', String(tip.y));
		courseTip.setAttribute('fill', knots === null ? 'none' : COURSE);
		courseTip.setAttribute('stroke', knots === null ? HINT : 'none');

		setReadout(headingValue, headingDeg === null ? UNKNOWN_READOUT : formatRoseBearing(headingDeg), headingDeg === null ? HINT : MARK);
		setReadout(courseValue, courseDeg === null ? UNKNOWN_READOUT : formatRoseBearing(courseDeg), courseDeg === null ? HINT : COURSE);
		setReadout(speedValue, knots === null ? UNKNOWN_READOUT : knots.toFixed(1), knots === null ? HINT : MARK);
		setReadout(driftValue, drift === null ? UNKNOWN_READOUT : `${drift > 0 ? '+' : ''}${Math.round(drift)}°`, drifting ? ALERT : HINT);
		el.setAttribute('aria-label', compassRoseLabel(headingDeg, courseDeg, knots, drift));
	}

	// Starts unknown, not north: an instrument that has never been fed telemetry
	// must not claim the bow is pointing anywhere.
	update(null, null, null);
	return { el, update };
}

//#endregion
