// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  RetryScheduler,
  type RetrySchedulerDependencies,
} from '../operator/RetryScheduler';

function harness() {
  const retry = vi.fn<RetrySchedulerDependencies['retry']>();
  const schedule = vi.fn<RetrySchedulerDependencies['schedule']>(
    (callback, ms) => window.setTimeout(callback, ms),
  );
  const cancel = vi.fn<RetrySchedulerDependencies['cancel']>(
    id => window.clearTimeout(id),
  );
  const scheduler = new RetryScheduler({ retry, schedule, cancel });
  return { scheduler, retry, schedule, cancel };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('RetryScheduler', () => {
  it('deduplicates callers into one retry after five seconds', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.scheduler.request();
    h.scheduler.request();
    h.scheduler.request();
    expect(h.schedule).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(4_999);
    expect(h.retry).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);

    expect(h.retry).toHaveBeenCalledTimes(1);
  });

  it('allows another retry after the pending attempt fires', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.scheduler.request();
    await vi.advanceTimersByTimeAsync(5_000);
    h.scheduler.request();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.retry).toHaveBeenCalledTimes(2);
    expect(h.schedule).toHaveBeenCalledTimes(2);
  });

  it('cancels a pending retry when a start begins', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.scheduler.request();
    h.scheduler.cancel();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.cancel).toHaveBeenCalledTimes(1);
    expect(h.retry).not.toHaveBeenCalled();
  });

  it('disposal cancels pending work and rejects later requests', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.scheduler.request();
    h.scheduler.dispose();
    h.scheduler.request();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.cancel).toHaveBeenCalledTimes(1);
    expect(h.schedule).toHaveBeenCalledTimes(1);
    expect(h.retry).not.toHaveBeenCalled();
  });
});
