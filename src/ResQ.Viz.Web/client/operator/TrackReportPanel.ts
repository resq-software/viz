// ResQ Viz - operator-entered external contacts, labelled as what they are
// SPDX-License-Identifier: Apache-2.0
//
// A track is something a sensor or a feed reported. It carries no capabilities,
// no control authority and no command endpoint, and this form adds none: it
// posts an observation and nothing else. Contacts stay read-only after ingest.
//
// The report a console types is not a sensor reading, so the form says so —
// `Simulation-only external report` — and stamps it with the **simulation**
// clock rather than a wall clock. The store ages contacts against simulation
// time, so a wall-clock stamp would be wrong by every pause and by the speed
// multiplier, and a recording replayed twice would age its contacts differently
// each time.

import { CoordinateFrame, TrackClassification, TrackSourceKind } from '../assets/types';
import type { TrackReportRequest } from './types';
import {
  actionButton, panelCard, readout, selectField, setDisabled, setHidden, setText, textField,
} from './panelDom';

/** How a report from this console names its source on the wire. */
export const TRACK_SOURCE_ID = 'operator-console';
/** Confidence an operator-entered contact claims, and the quality of the
 *  "source" behind it. One number, stated once: a typed contact is exactly as
 *  good as the person typing it, and pretending to 1.0 would make it outrank
 *  every modelled sensor in the fusion. */
export const TRACK_CONFIDENCE = 0.9;

const CLASSIFICATIONS: readonly (readonly [number, string])[] = [
  [TrackClassification.Unknown, 'Unknown'],
  [TrackClassification.Unclassified, 'Unclassified'],
  [TrackClassification.Aircraft, 'Aircraft'],
  [TrackClassification.Rotorcraft, 'Rotorcraft'],
  [TrackClassification.SmallUnmannedAircraft, 'Small unmanned aircraft'],
  [TrackClassification.Vessel, 'Vessel'],
  [TrackClassification.GroundVehicle, 'Ground vehicle'],
  [TrackClassification.Person, 'Person'],
  [TrackClassification.Obstacle, 'Obstacle'],
  [TrackClassification.Other, 'Other'],
];

export interface TrackPanelState {
  readonly simulationTimeSeconds: number;
  readonly mutationsEnabled: boolean;
  readonly blockedReason: string | null;
}

export interface TrackPanelOptions {
  readonly mount: HTMLElement;
  readonly onReport: (request: TrackReportRequest) => void;
}

/** The simulation-only external-contact ingest form. */
export class TrackReportPanel {
  private readonly _options: TrackPanelOptions;
  private readonly _stamp: HTMLElement;
  private readonly _status: HTMLElement;
  private readonly _report: HTMLButtonElement;
  private readonly _id: HTMLInputElement;
  private readonly _label: HTMLInputElement;
  private readonly _classification: HTMLSelectElement;
  private readonly _x: HTMLInputElement;
  private readonly _y: HTMLInputElement;
  private readonly _z: HTMLInputElement;

  private _state: TrackPanelState = {
    simulationTimeSeconds: 0, mutationsEnabled: true, blockedReason: null,
  };
  private _busy = false;
  private _message = '';
  private _isError = false;

  constructor(options: TrackPanelOptions) {
    this._options = options;
    const card = panelCard(
      options.mount,
      'track',
      'Simulation-only external report',
      'Puts a contact into the picture that the simulation does not generate. It is '
      + 'an observation, not a command: contacts cannot be tasked, and nothing here '
      + 'reflects a real sensor.',
    );

    const list = document.createElement('dl');
    list.className = 'advanced-readout';
    const stamp = readout('track-stamp', 'Observed at');
    const frame = readout('track-frame', 'Frame');
    list.append(stamp.row, frame.row);
    this._stamp = stamp.value;
    setText(frame.value, 'LocalEus (scene frame): +X east, +Y up, +Z south');
    this._status = card.status;

    const id = textField('track-id', 'Track identifier');
    const label = textField('track-label', 'Label (display only)');
    const classification = selectField(
      'track-classification', 'Classification', CLASSIFICATIONS, TrackClassification.Unknown,
    );
    const x = textField('track-x', 'East (m)', '0');
    const y = textField('track-y', 'Up (m)', '0');
    const z = textField('track-z', 'South (m)', '0');
    this._id = id.input;
    this._label = label.input;
    this._classification = classification.select;
    this._x = x.input;
    this._y = y.input;
    this._z = z.input;

    const grid = document.createElement('div');
    grid.className = 'advanced-grid';
    grid.append(
      id.wrapper, label.wrapper, classification.wrapper,
      x.wrapper, y.wrapper, z.wrapper,
    );

    this._report = actionButton('report', 'Report contact');
    const actions = document.createElement('div');
    actions.className = 'advanced-actions';
    actions.append(this._report);

    card.body.append(list, grid, actions);
    this._report.addEventListener('click', () => this._submit());
    this._render();
  }

  render(state: TrackPanelState): void {
    this._state = state;
    this._render();
  }

  setBusy(busy: boolean): void {
    this._busy = busy;
    this._render();
  }

  setStatus(message: string | null, isError = false): void {
    this._message = message ?? '';
    this._isError = isError;
    this._render();
  }

  private _submit(): void {
    const trackId = this._id.value.trim();
    if (trackId === '') {
      this.setStatus('A track identifier is required.', true);
      return;
    }
    const position = {
      x: Number.parseFloat(this._x.value),
      y: Number.parseFloat(this._y.value),
      z: Number.parseFloat(this._z.value),
    };
    if (!Number.isFinite(position.x) || !Number.isFinite(position.y)
      || !Number.isFinite(position.z)) {
      this.setStatus('Every position component must be a finite number of metres.', true);
      return;
    }
    const label = this._label.value.trim();
    this._options.onReport({
      trackId,
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position,
        // The all-zero quaternion: no attitude was declared, and an operator
        // reporting a contact has none to declare.
        orientation: { x: 0, y: 0, z: 0, w: 0 },
      },
      twist: null,
      classification: Number.parseInt(this._classification.value, 10) as TrackClassification,
      sourceId: TRACK_SOURCE_ID,
      sourceKind: TrackSourceKind.OperatorEntered,
      sourceQuality: TRACK_CONFIDENCE,
      confidence: TRACK_CONFIDENCE,
      observedAtSimulationTimeSeconds: this._state.simulationTimeSeconds,
      // Absent, not zero: a consumer told 0 m draws a point where it should
      // draw a circle.
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      label: label === '' ? null : label,
      transponder: null,
    });
  }

  private _render(): void {
    setText(
      this._stamp,
      `${this._state.simulationTimeSeconds.toFixed(1)}s simulation time`,
    );
    const enabled = this._state.mutationsEnabled && !this._busy;
    setDisabled(this._report, !enabled);
    for (const input of [this._id, this._label, this._x, this._y, this._z]) {
      input.disabled = !this._state.mutationsEnabled;
    }
    this._classification.disabled = !this._state.mutationsEnabled;

    const message = this._message !== ''
      ? this._message
      : this._state.blockedReason ?? '';
    setHidden(this._status, message === '');
    setText(this._status, message);
    this._status.setAttribute('role', this._isError && this._message !== '' ? 'alert' : 'status');
    this._status.classList.toggle('is-error', this._isError && this._message !== '');
  }
}
