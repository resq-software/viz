// ResQ Viz - operator shell presentation contracts
// SPDX-License-Identifier: Apache-2.0

/** Which mutually exclusive shell branch is visible. */
export type OperatorMode = 'booting' | 'v2' | 'legacy';

/** What the boot branch tells the operator about the current connection attempt. */
export type OperatorBootStatus = 'connecting' | 'error';

/** Stable mount points consumed by lazy operator surfaces. */
export interface OperatorMounts {
  readonly mission: HTMLElement;
  readonly filter: HTMLElement;
  readonly roster: HTMLElement;
  readonly advancedSafety: HTMLElement;
  readonly context: HTMLElement;
  readonly modal: HTMLElement;
  readonly editor: HTMLElement;
}
