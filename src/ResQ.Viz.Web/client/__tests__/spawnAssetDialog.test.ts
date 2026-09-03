// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The spawn form's whole job is to never offer an action the server would
// refuse. Three refusals live on the host and nowhere in the type system:
// `POST /api/v2/sim/assets` rejects `displayName`, `model`, `agencyId` and
// `fleetId` on an air asset outright rather than dropping them; the spawnable
// class list is deployment-derived, so reserved classes must come from
// discovery rather than a hard-coded list here; and heading arrives as a full
// EUS-from-FLU orientation, not a yaw scalar, so a pure Y-axis quaternion
// silently spawns assets facing the wrong way. Each is asserted below against
// the payload that actually leaves the browser.

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import * as THREE from 'three';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SpawnAssetDialog, headingToEusQuaternion } from '../operator/SpawnAssetDialog';
import { AssetDomain, CoordinateFrame, VehicleClass } from '../assets/types';
import type { ApiFailure, ApiProblem, Result } from '../api';
import type {
  AssetProfileCatalogResponse,
  AssetSpawnResponse,
} from '../operator/types';
import type { ResourceState } from '../operator/ConsoleResources';

/** Exactly what `GET /api/v2/sim/asset-profiles` publishes for this deployment. */
const discovered: AssetProfileCatalogResponse = {
  profiles: [
    {
      vehicleClass: VehicleClass.Multirotor,
      domain: AssetDomain.Air,
      displayName: 'Multirotor',
      headingApplies: false,
    },
    {
      vehicleClass: VehicleClass.AckermannRover,
      domain: AssetDomain.Ground,
      displayName: 'Ackermann rover',
      headingApplies: true,
    },
    {
      vehicleClass: VehicleClass.SurfaceVessel,
      domain: AssetDomain.Surface,
      displayName: 'Surface vessel',
      headingApplies: true,
    },
  ],
};

const AIR_UNSUPPORTED = ['displayName', 'model', 'agencyId', 'fleetId'] as const;

beforeEach(() => document.body.replaceChildren());

function accepted(assetId: string): Result<AssetSpawnResponse, ApiFailure> {
  return {
    success: true,
    value: { assetId, descriptor: { assetId } } as unknown as AssetSpawnResponse,
  };
}

function fieldProblem(): Result<AssetSpawnResponse, ApiFailure> {
  const problem: ApiProblem = {
    status: 400,
    code: 'asset.requestInvalid',
    reasonCode: 'asset.assetIdInvalid',
    title: 'Asset not spawned',
    detail: "An asset id must be 1-64 characters of letters, digits, '-', '_' or '.'.",
    traceId: 'trace-9',
    errors: [{ field: 'assetId', code: 'assetId.invalid', message: 'Remove the slash.' }],
  };
  return { success: false, error: { kind: 'problem', problem } };
}

function harness(
  overrides: Partial<ConstructorParameters<typeof SpawnAssetDialog>[0]> = {},
  profileState: ResourceState<AssetProfileCatalogResponse> =
    { status: 'ready', value: discovered },
) {
  const mount = document.createElement('div');
  const trigger = document.createElement('button');
  trigger.textContent = 'Spawn asset';
  const fallbackFocus = document.createElement('h2');
  fallbackFocus.tabIndex = -1;
  document.body.append(trigger, fallbackFocus, mount);

  let profiles = profileState;
  const spawn = vi.fn().mockResolvedValue(accepted('usv-new'));
  const onRetryProfiles = vi.fn();
  const onAccepted = vi.fn();
  const onClose = vi.fn();
  const dialog = new SpawnAssetDialog({
    mount,
    trigger,
    fallbackFocus,
    profiles: () => profiles,
    spawn,
    onRetryProfiles,
    onAccepted,
    onClose,
    ...overrides,
  });
  return {
    dialog,
    mount,
    trigger,
    fallbackFocus,
    spawn,
    onRetryProfiles,
    onAccepted,
    onClose,
    setProfiles(next: ResourceState<AssetProfileCatalogResponse>) {
      profiles = next;
      dialog.refresh();
    },
  };
}

const row = (mount: HTMLElement, name: string): HTMLElement =>
  mount.querySelector<HTMLElement>(`[data-field="${name}"]`)!;

const control = (mount: HTMLElement, name: string): HTMLInputElement =>
  mount.querySelector<HTMLInputElement>(`[name="${name}"]`)!;

function chooseClass(mount: HTMLElement, vehicleClass: number): void {
  const select = mount.querySelector<HTMLSelectElement>('[name="vehicleClass"]')!;
  select.value = String(vehicleClass);
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

function type(mount: HTMLElement, name: string, value: string): void {
  const element = control(mount, name);
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
}

function submit(mount: HTMLElement): void {
  mount.querySelector<HTMLButtonElement>('[data-action="spawn"]')!.click();
}

describe('headingToEusQuaternion', () => {
  // The server builds this same basis in CoordinateFrames.HeadingToEusOrientation;
  // a yaw-only quaternion about +Y agrees with it on the forward axis and
  // disagrees on up, which is why up is asserted on every heading too.
  const cardinals: ReadonlyArray<readonly [number, readonly [number, number, number]]> = [
    [0, [0, 0, -1]],     // north is -Z in the EUS scene frame
    [90, [1, 0, 0]],     // east
    [180, [0, 0, 1]],    // south
    [270, [-1, 0, 0]],   // west
  ];

  it.each(cardinals)('points FLU forward down heading %i°', (heading, expected) => {
    const wire = headingToEusQuaternion(heading as number);
    const rotation = new THREE.Quaternion(wire.x, wire.y, wire.z, wire.w);

    const forward = new THREE.Vector3(1, 0, 0).applyQuaternion(rotation);
    expect(forward.x).toBeCloseTo(expected[0]!, 6);
    expect(forward.y).toBeCloseTo(expected[1]!, 6);
    expect(forward.z).toBeCloseTo(expected[2]!, 6);

    // FLU body up is +Z, and it must land on scene up whatever the heading.
    const up = new THREE.Vector3(0, 0, 1).applyQuaternion(rotation);
    expect(up.x).toBeCloseTo(0, 6);
    expect(up.y).toBeCloseTo(1, 6);
    expect(up.z).toBeCloseTo(0, 6);
  });

  it('serialises as named components, never as an array', () => {
    // QuaternionJsonConverter reads {x,y,z,w}; a Three.js instance or a tuple
    // both bind to the all-zero "no attitude declared" quaternion instead.
    const quaternion = headingToEusQuaternion(270);
    const wire: unknown = JSON.parse(JSON.stringify(quaternion));

    expect(Array.isArray(wire)).toBe(false);
    expect(wire).toEqual({
      x: quaternion.x,
      y: quaternion.y,
      z: quaternion.z,
      w: quaternion.w,
    });
    expect(Object.keys(wire as object).sort()).toEqual(['w', 'x', 'y', 'z']);
  });
});

describe('SpawnAssetDialog', () => {
  it('rides the shared lazy dialog stylesheet and never writes to fleet UI', () => {
    const source = readFileSync(
      resolve(process.cwd(), 'client/operator/SpawnAssetDialog.ts'), 'utf8',
    );

    expect(source).toContain("import '../styles/operator-dialogs.css'");
    // Streamed state is the only thing that may add an asset to the roster.
    expect(source).not.toMatch(/from '\.\.\/assets\/fleetUi'/);
    expect(source).not.toMatch(/from '\.\/AssetRoster'/);
    expect(source).not.toMatch(/from '\.\.\/assets\/AssetPanel'/);
  });

  it('offers only discovered profiles and shows the domain as derived truth', () => {
    const h = harness();
    h.dialog.open();

    const options = [...h.mount.querySelectorAll<HTMLOptionElement>('[name="vehicleClass"] option')];
    expect(options.map(option => option.textContent)).toEqual([
      'Multirotor', 'Ackermann rover', 'Surface vessel',
    ]);
    expect(options.map(option => option.value)).toEqual([
      String(VehicleClass.Multirotor),
      String(VehicleClass.AckermannRover),
      String(VehicleClass.SurfaceVessel),
    ]);
    // Reserved subsurface classes are never spawnable and never discovered.
    expect(h.mount.textContent).not.toContain('Rov');
    expect(h.mount.textContent).not.toContain('Auv');

    // Domain is read-only: it is derived from the chosen class, never chosen.
    expect(h.mount.querySelector('select[name="domain"], input[name="domain"]')).toBeNull();
    expect(row(h.mount, 'domain').textContent).toContain('Air');
    chooseClass(h.mount, VehicleClass.SurfaceVessel);
    expect(row(h.mount, 'domain').textContent).toContain('Surface');
  });

  it('hides heading and the four metadata fields an air spawn is refused', () => {
    const h = harness();
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.Multirotor);

    expect(row(h.mount, 'heading').hidden).toBe(true);
    for (const field of AIR_UNSUPPORTED) expect(row(h.mount, field).hidden).toBe(true);
    // Only the two an air spawn actually accepts stay offered.
    expect(row(h.mount, 'assetId').hidden).toBe(false);
    expect(row(h.mount, 'vendor').hidden).toBe(false);
  });

  it('shows heading and every metadata field for a surface profile', () => {
    const h = harness();
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.SurfaceVessel);

    expect(row(h.mount, 'heading').hidden).toBe(false);
    for (const field of [...AIR_UNSUPPORTED, 'assetId', 'vendor']) {
      expect(row(h.mount, field).hidden).toBe(false);
    }
  });

  it('sends the exact frame-qualified payload the surface endpoint accepts', async () => {
    const expectedHeadingQuaternion = headingToEusQuaternion(270);
    const h = harness();
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.SurfaceVessel);
    type(h.mount, 'positionX', '10');
    type(h.mount, 'positionY', '-3');
    type(h.mount, 'positionZ', '20');
    type(h.mount, 'heading', '270');
    type(h.mount, 'assetId', 'usv-new');
    type(h.mount, 'displayName', 'Relief Ferry');
    type(h.mount, 'agencyId', 'agency-1');
    type(h.mount, 'fleetId', 'relief');
    submit(h.mount);

    await vi.waitFor(() => expect(h.spawn).toHaveBeenCalledTimes(1));
    expect(h.spawn).toHaveBeenCalledWith({
      vehicleClass: VehicleClass.SurfaceVessel,
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position: { x: 10, y: -3, z: 20 },
        orientation: expectedHeadingQuaternion,
      },
      assetId: 'usv-new',
      displayName: 'Relief Ferry',
      vendor: null,
      model: null,
      agencyId: 'agency-1',
      fleetId: 'relief',
    });
  });

  it('omits the air-unsupported metadata and declares no attitude', async () => {
    const h = harness();
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.Multirotor);
    type(h.mount, 'positionX', '0');
    type(h.mount, 'positionY', '25');
    type(h.mount, 'positionZ', '-5');
    type(h.mount, 'vendor', 'skydio');
    submit(h.mount);

    await vi.waitFor(() => expect(h.spawn).toHaveBeenCalledTimes(1));
    const request = h.spawn.mock.calls[0]![0] as Record<string, unknown>;
    // Absent, not null: the host refuses these outright on an air spawn.
    for (const field of AIR_UNSUPPORTED) {
      expect(Object.prototype.hasOwnProperty.call(request, field)).toBe(false);
    }
    expect(request).toMatchObject({
      vehicleClass: VehicleClass.Multirotor,
      assetId: null,
      vendor: 'skydio',
    });
    // The all-zero quaternion is "no attitude declared", not a rotation.
    expect((request['pose'] as { orientation: unknown }).orientation)
      .toEqual({ x: 0, y: 0, z: 0, w: 0 });
  });

  it('reports the accepted id and leaves the roster to the stream', async () => {
    const h = harness();
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.SurfaceVessel);
    submit(h.mount);

    await vi.waitFor(() => expect(h.onAccepted).toHaveBeenCalledWith('usv-new'));
    expect(h.mount.textContent).toContain('Awaiting streamed asset state');
    // Nothing here inserts a row; the dialog owns no roster seam at all.
    expect(h.mount.querySelector('[data-asset-id]')).toBeNull();
  });

  it('shows the typed problem the endpoint returned and marks the field', async () => {
    const h = harness({ spawn: vi.fn().mockResolvedValue(fieldProblem()) });
    h.dialog.open();
    chooseClass(h.mount, VehicleClass.SurfaceVessel);
    type(h.mount, 'assetId', 'usv/new');
    submit(h.mount);

    const error = h.mount.querySelector<HTMLElement>('.operator-dialog-error')!;
    await vi.waitFor(() => expect(error.hidden).toBe(false));
    expect(error.textContent).toContain('asset.assetIdInvalid');
    expect(error.textContent).toContain(
      "An asset id must be 1-64 characters of letters, digits, '-', '_' or '.'.",
    );
    expect(error.textContent).toContain('Remove the slash.');
    expect(control(h.mount, 'assetId').getAttribute('aria-invalid')).toBe('true');
    // Still open and still dismissible after a refusal.
    expect(h.dialog.isOpen).toBe(true);
  });

  it('disables the trigger and offers Retry while profiles are unavailable', () => {
    const h = harness({}, { status: 'error', failure: { kind: 'network', message: 'offline' } });

    expect(h.trigger.disabled).toBe(true);
    const retry = document.querySelector<HTMLButtonElement>('[data-action="retry-profiles"]')!;
    expect(retry).not.toBeNull();
    expect(retry.hidden).toBe(false);
    retry.click();
    expect(h.onRetryProfiles).toHaveBeenCalledOnce();

    h.dialog.open();
    expect(h.mount.textContent).toContain('offline');
    expect(h.mount.querySelector<HTMLButtonElement>('[data-action="spawn"]')!.disabled).toBe(true);

    h.setProfiles({ status: 'ready', value: discovered });
    expect(h.trigger.disabled).toBe(false);
    expect(retry.hidden).toBe(true);
    expect(h.mount.querySelector<HTMLButtonElement>('[data-action="spawn"]')!.disabled).toBe(false);
  });

  it('closes on Escape and hands focus back to the trigger', () => {
    const h = harness();
    h.dialog.open();
    expect(h.dialog.isOpen).toBe(true);

    h.mount.querySelector('dialog')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );

    expect(h.dialog.isOpen).toBe(false);
    expect(document.activeElement).toBe(h.trigger);
    expect(h.onClose).toHaveBeenCalledOnce();
    expect(h.trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('falls back off a disabled trigger when it closes', () => {
    const h = harness();
    h.dialog.open();
    h.trigger.disabled = true;
    h.dialog.close();

    expect(document.activeElement).toBe(h.fallbackFocus);
  });
});
