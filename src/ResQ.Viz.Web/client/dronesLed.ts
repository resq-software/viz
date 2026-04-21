// ResQ Viz - Drone status LED state machine
// SPDX-License-Identifier: Apache-2.0
//
// Table-driven profile for the per-drone status LED. Classifies each drone
// into one of a fixed set of states (battery / mission / detection) and
// drives the `MeshStandardMaterial` emissive colour + intensity pulse from a
// shared simTime. Priority order follows safety-of-flight: critical battery
// and emergency override everything, then mission states, then detection
// beacon, then default flying.

import type * as THREE from 'three';
import type { DroneState } from './types';

export type LEDState =
    | 'CRITICAL'
    | 'EMERGENCY'
    | 'LOW_BATTERY'
    | 'RETURNING'
    | 'DETECTING'
    | 'HOVERING'
    | 'FLYING'
    | 'DISARMED';

export interface LEDProfile {
    readonly color:         number;  // RGB hex — used for both emissive and albedo
    readonly baseIntensity: number;  // sustained emissive intensity
    readonly pulseAmp:      number;  // added to base, scaled by sin(2π·hz·t)
    readonly pulseHz:       number;  // pulse frequency in Hz (0 = static)
}

// Profiles tuned so CRITICAL/EMERGENCY draw the eye from 100+ m away (fast
// pulse, high amplitude); flying/hovering breathe subtly so drones read as
// "alive" rather than static; DETECTING is a bright white flash meant to be
// noticed the moment a drone spots a survivor.
export const LED_PROFILES: Record<LEDState, LEDProfile> = {
    CRITICAL:    { color: 0xff2200, baseIntensity: 2.5, pulseAmp: 1.5, pulseHz: 1.6 },
    EMERGENCY:   { color: 0xff0000, baseIntensity: 3.0, pulseAmp: 2.0, pulseHz: 1.3 },
    LOW_BATTERY: { color: 0xff8800, baseIntensity: 2.0, pulseAmp: 0.0, pulseHz: 0.0 },
    RETURNING:   { color: 0xffaa00, baseIntensity: 1.8, pulseAmp: 0.4, pulseHz: 0.6 },
    DETECTING:   { color: 0xffffff, baseIntensity: 4.0, pulseAmp: 0.0, pulseHz: 0.0 },
    HOVERING:    { color: 0x0088ff, baseIntensity: 1.5, pulseAmp: 0.2, pulseHz: 0.3 },
    FLYING:      { color: 0x00ff44, baseIntensity: 2.0, pulseAmp: 0.3, pulseHz: 0.5 },
    DISARMED:    { color: 0x333333, baseIntensity: 0.1, pulseAmp: 0.0, pulseHz: 0.0 },
};

export interface LEDInputs {
    drone:             DroneState;
    batteryPct:        number;      // normalised 0..1 (backend sends 0..100)
    batteryWarn:       number;      // warn threshold as fraction 0..1
    detectionFlashSec: number;      // seconds remaining on detection flash, ≤ 0 when inactive
}

/**
 * Decide the LED state for one drone. Pure — same inputs → same state.
 * Priority: safety (battery/emergency) > mission (return/hover) > detection > flying.
 */
export function classifyLED(inputs: LEDInputs): LEDState {
    const { drone, batteryPct, batteryWarn, detectionFlashSec } = inputs;

    if (drone.armed === false) return 'DISARMED';

    // Safety-of-flight overrides everything.
    if (batteryPct < batteryWarn * 0.75) return 'CRITICAL';
    const status = drone.status ?? 'flying';
    if (status === 'emergency' || status === 'EMERGENCY') return 'EMERGENCY';
    if (batteryPct < batteryWarn) return 'LOW_BATTERY';

    // Mission states (RTL / landing beat a generic flying state).
    if (status === 'rtl' || status === 'landing' || status === 'RETURNING') return 'RETURNING';

    // Detection beacon — brief flash when the drone reports a new detection.
    if (detectionFlashSec > 0) return 'DETECTING';

    if (status === 'hovering') return 'HOVERING';
    return 'FLYING';
}

/**
 * Drive the given LED material from a profile, advancing the pulse by shared
 * simTime (seconds). Material is mutated in place so the caller keeps its
 * reference.
 */
export function applyLED(
    mat: THREE.MeshStandardMaterial,
    state: LEDState,
    simTimeSec: number,
): void {
    const p = LED_PROFILES[state];
    mat.color.setHex(p.color);
    mat.emissive.setHex(p.color);
    const pulse = p.pulseHz > 0
        ? p.pulseAmp * Math.sin(simTimeSec * p.pulseHz * Math.PI * 2)
        : 0;
    mat.emissiveIntensity = p.baseIntensity + pulse;
}

/** Seconds the detection beacon stays lit after a new detection is reported. */
export const DETECTION_FLASH_DURATION_SEC = 0.45;
