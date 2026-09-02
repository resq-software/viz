/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ApiHttpError,
  apiGet,
  apiGetJson,
  apiPost,
  apiPostJson,
} from '../api';

const fetchMock = vi.fn<typeof fetch>();

const typedProblem = {
  code: 'authority.notHolder',
  title: 'Request conflicts with current state',
  detail: 'Another console holds the asset.',
  traceId: 'trace-1',
  assetId: 'uav-1',
  errors: [],
};

const fallback502 = {
  success: false,
  error: {
    kind: 'problem',
    problem: {
      status: 502,
      code: 'http.error',
      reasonCode: null,
      title: 'Bad Gateway',
      detail: 'Request failed',
      traceId: null,
      errors: [],
    },
  },
} as const;

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('typed v2 JSON API failures', () => {
  it('retains a typed GET problem and takes status from the response', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(typedProblem), {
      status: 409,
      statusText: 'Conflict',
      headers: { 'Content-Type': 'application/json' },
    }));

    const result = await apiGetJson<unknown>('/api/v2/sim/assets/uav-1/control', {
      retries: 0,
    });

    expect(result).toEqual({
      success: false,
      error: {
        kind: 'problem',
        problem: {
          status: 409,
          code: 'authority.notHolder',
          reasonCode: null,
          title: 'Request conflicts with current state',
          detail: 'Another console holds the asset.',
          traceId: 'trace-1',
          errors: [],
        },
      },
    });
  });

  it.each([
    {
      label: 'missing',
      body: {
        code: typedProblem.code,
        title: typedProblem.title,
        detail: typedProblem.detail,
        traceId: typedProblem.traceId,
      },
    },
    { label: 'null', body: { ...typedProblem, reasonCode: null, errors: null } },
  ])('normalizes $label nullable problem fields', async ({ body }) => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(body), {
      status: 409,
      statusText: 'Conflict',
    }));

    const result = await apiPostJson<unknown>('/api/v2/sim/assets/uav-1/control', {});

    expect(result).toMatchObject({
      success: false,
      error: {
        kind: 'problem',
        problem: { reasonCode: null, errors: [] },
      },
    });
  });

  it('keeps only well-formed field errors', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      ...typedProblem,
      reasonCode: 'control.leaseNotLive',
      errors: [
        { field: 'holderId', code: 'control.holderMissing', message: 'Holder required.' },
        { field: 4, code: 'bad.field', message: 'wrong field type' },
        { field: 'leaseId', code: null, message: 'wrong code type' },
        null,
      ],
    }), { status: 409, statusText: 'Conflict' }));

    const result = await apiPostJson<unknown>('/api/v2/sim/assets/uav-1/control', {});

    expect(result).toMatchObject({
      success: false,
      error: {
        kind: 'problem',
        problem: {
          reasonCode: 'control.leaseNotLive',
          errors: [
            { field: 'holderId', code: 'control.holderMissing', message: 'Holder required.' },
          ],
        },
      },
    });
  });

  it.each(['', '<html>bad gateway</html>'])(
    'falls back exactly for the non-problem body %j',
    async body => {
      fetchMock.mockResolvedValueOnce(new Response(body, {
        status: 502,
        statusText: 'Bad Gateway',
      }));

      const result = await apiPostJson<unknown>('/api/v2/sim/assets', {});

      expect(result).toEqual(fallback502);
    },
  );

  it('keeps a fetch rejection distinct as a network failure', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('offline'));

    const result = await apiGetJson('/api/v2/sim/scenarios', { retries: 0 });

    expect(result).toEqual({
      success: false,
      error: { kind: 'network', message: 'offline' },
    });
  });

  it('keeps an abort distinct as a timeout', async () => {
    fetchMock.mockRejectedValueOnce(new DOMException('aborted', 'AbortError'));

    const result = await apiPostJson('/api/v2/sim/scenarios/flood-response/start');

    expect(result).toEqual({
      success: false,
      error: { kind: 'timeout', message: 'Request timed out' },
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('parses a successful JSON mutation', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{"scenario":"flood-response"}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));

    const result = await apiPostJson<{ scenario: string }>('/start');

    expect(result).toEqual({ success: true, value: { scenario: 'flood-response' } });
  });

  it('retries successful GET recovery after a network rejection', async () => {
    fetchMock
      .mockRejectedValueOnce(new TypeError('offline'))
      .mockResolvedValueOnce(new Response('{"ready":true}', { status: 200 }));

    const result = await apiGetJson<{ ready: boolean }>('/ready', {
      retries: 1,
      retryDelayMs: 0,
      retryJitterMs: 0,
    });

    expect(result).toEqual({ success: true, value: { ready: true } });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('does not retry an authoritative HTTP problem', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify(typedProblem), {
      status: 409,
      statusText: 'Conflict',
    }));

    const result = await apiGetJson('/api/v2/sim/scenarios', {
      retries: 3,
      retryDelayMs: 0,
      retryJitterMs: 0,
    });

    expect(result).toMatchObject({
      success: false,
      error: { kind: 'problem', problem: { status: 409 } },
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the timeout active while the JSON body is being read', async () => {
    vi.useFakeTimers();
    fetchMock.mockImplementationOnce(async (_input, init) => ({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => {
          reject(new DOMException('aborted', 'AbortError'));
        });
      }),
    }) as Response);

    const pending = apiGetJson('/slow', { timeoutMs: 10, retries: 0 });
    await vi.advanceTimersByTimeAsync(11);

    await expect(pending).resolves.toEqual({
      success: false,
      error: { kind: 'timeout', message: 'Request timed out' },
    });
  });

  it.each(['GET', 'POST'] as const)(
    'keeps the legacy %s Result<..., Error> surface and exposes the decoded problem',
    async method => {
      fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(typedProblem), {
        status: 409,
        statusText: 'Conflict',
      }));

      const result = method === 'GET'
        ? await apiGet('/legacy', { retries: 0 })
        : await apiPost('/legacy', {});

      expect(result.success).toBe(false);
      if (result.success) throw new Error('Expected the legacy wrapper to fail.');
      expect(result.error).toBeInstanceOf(ApiHttpError);
      const error = result.error as ApiHttpError;
      expect(error.status).toBe(409);
      expect(error.path).toBe('/legacy');
      expect(error.problem).toEqual({
        status: 409,
        code: 'authority.notHolder',
        reasonCode: null,
        title: 'Request conflicts with current state',
        detail: 'Another console holds the asset.',
        traceId: 'trace-1',
        errors: [],
      });
    },
  );
});
