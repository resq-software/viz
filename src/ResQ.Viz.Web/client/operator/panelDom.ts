// ResQ Viz - the small DOM vocabulary the Advanced/Safety panels share
// SPDX-License-Identifier: Apache-2.0
//
// Four panels that each build a card of labelled rows, buttons and a status
// line. These are the writes they have in common, and they are written once
// because a per-panel copy is a per-panel chance to forget the paired
// `aria-disabled`, or to write `hidden` without also writing the text.
//
// Every setter is idempotent: it compares before it writes, so a 10 Hz frame
// that changed nothing produces no DOM mutation and no accessibility-tree churn.

/** Sets text only when it differs. */
export function setText(node: Node, value: string): void {
  if (node.textContent !== value) node.textContent = value;
}

/** Hides or shows an element. Paired with an `[hidden]` rule in the stylesheet,
 *  because a class that sets `display` outranks the UA's `[hidden]` rule and
 *  both branches would then render. */
export function setHidden(element: HTMLElement, hidden: boolean): void {
  if (element.hidden !== hidden) element.hidden = hidden;
}

/** Disables a control and mirrors it for assistive technology. */
export function setDisabled(button: HTMLButtonElement, disabled: boolean): void {
  if (button.disabled !== disabled) button.disabled = disabled;
  setAttribute(button, 'aria-disabled', String(disabled));
}

function setAttribute(element: Element, name: string, value: string): void {
  if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

/** A button addressed by `data-action`, which is also how tests reach it. */
export function actionButton(action: string, label: string): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'btn';
  button.dataset['action'] = action;
  button.textContent = label;
  return button;
}

/** A read-only labelled row: `<dt>` label, `<dd data-field>` value. */
export function readout(field: string, label: string): {
  readonly row: DocumentFragment;
  readonly value: HTMLElement;
} {
  const row = document.createDocumentFragment();
  const term = document.createElement('dt');
  term.textContent = label;
  const value = document.createElement('dd');
  value.className = 'advanced-value';
  value.dataset['field'] = field;
  row.append(term, value);
  return { row, value };
}

/** A labelled text input inside its own `<label>`, addressed by `data-field`. */
export function textField(
  field: string,
  label: string,
  initial = '',
): { readonly wrapper: HTMLLabelElement; readonly input: HTMLInputElement } {
  const wrapper = document.createElement('label');
  wrapper.className = 'advanced-field';
  const caption = document.createElement('span');
  caption.textContent = label;
  const input = document.createElement('input');
  input.type = 'text';
  input.dataset['field'] = field;
  input.value = initial;
  wrapper.append(caption, input);
  return { wrapper, input };
}

/** A labelled `<select>` over numeric wire values, addressed by `data-field`. */
export function selectField(
  field: string,
  label: string,
  options: readonly (readonly [number, string])[],
  initial: number,
): { readonly wrapper: HTMLLabelElement; readonly select: HTMLSelectElement } {
  const wrapper = document.createElement('label');
  wrapper.className = 'advanced-field';
  const caption = document.createElement('span');
  caption.textContent = label;
  const select = document.createElement('select');
  select.dataset['field'] = field;
  for (const [value, text] of options) {
    const option = document.createElement('option');
    option.value = String(value);
    option.textContent = text;
    select.append(option);
  }
  select.value = String(initial);
  wrapper.append(caption, select);
  return { wrapper, select };
}

/** The card every panel renders into: kicker, blurb, and a body to fill. */
export function panelCard(
  mount: HTMLElement,
  panel: string,
  heading: string,
  blurb: string,
): { readonly root: HTMLElement; readonly body: HTMLElement; readonly status: HTMLElement } {
  const root = document.createElement('section');
  root.className = 'operator-card advanced-panel';
  root.dataset['panel'] = panel;

  const kicker = document.createElement('span');
  kicker.className = 'operator-section-kicker';
  kicker.textContent = heading;

  const note = document.createElement('p');
  note.className = 'advanced-blurb';
  note.textContent = blurb;

  const body = document.createElement('div');
  body.className = 'advanced-body';

  const status = document.createElement('p');
  status.className = 'advanced-status';
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  status.hidden = true;

  root.append(kicker, note, body, status);
  mount.append(root);
  return { root, body, status };
}

/** One short phrase for a failure. A problem is named by its stable code — the
 *  contract — with the server's prose after it; nothing parses either. */
export function failureText(failure: import('../api').ApiFailure): string {
  return failure.kind === 'problem'
    ? `${failure.problem.reasonCode ?? failure.problem.code} · ${failure.problem.detail}`
    : failure.message;
}

/** The stable code a failure carries, for the authority invalidation gate. */
export function failureCode(failure: import('../api').ApiFailure): string {
  return failure.kind === 'problem'
    ? (failure.problem.reasonCode ?? failure.problem.code)
    : failure.kind;
}
