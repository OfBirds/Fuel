import { describe, it, expect, afterEach, vi } from 'vitest';

// loadRuntimeConfig keeps a module-level cache; reset modules between tests for isolation.
async function freshModule() {
  vi.resetModules();
  return import('./oidc');
}

const okResponse = (body: unknown) =>
  ({ ok: true, status: 200, json: async () => body }) as unknown as Response;

describe('loadRuntimeConfig', () => {
  afterEach(() => vi.restoreAllMocks());

  it('caches a successful config (one fetch across repeated calls)', async () => {
    const { loadRuntimeConfig } = await freshModule();
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      okResponse({ oidcEnabled: true, oidcOnline: true, oidcAuthority: 'https://cr/realms/x', oidcClientId: 'fuel' }),
    );
    const a = await loadRuntimeConfig();
    const b = await loadRuntimeConfig();
    expect(a.oidcOnline).toBe(true);
    expect(b).toEqual(a);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does NOT cache a failure — a later call retries and can succeed', async () => {
    const { loadRuntimeConfig } = await freshModule();
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('offline'));
    const first = await loadRuntimeConfig(); // 3 attempts, all fail
    expect(first).toEqual({ oidcEnabled: false });
    expect(fetchMock).toHaveBeenCalledTimes(3);

    fetchMock.mockResolvedValue(okResponse({ oidcEnabled: true, oidcOnline: true, oidcAuthority: 'a', oidcClientId: 'fuel' }));
    const second = await loadRuntimeConfig(); // retries because the failure was never cached
    expect(second.oidcOnline).toBe(true);
  });

  it('retries a transient failure then succeeds', async () => {
    const { loadRuntimeConfig } = await freshModule();
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockRejectedValueOnce(new Error('flaky'))
      .mockResolvedValueOnce(okResponse({ oidcEnabled: true, oidcOnline: true, oidcAuthority: 'a', oidcClientId: 'fuel' }));
    const cfg = await loadRuntimeConfig();
    expect(cfg.oidcOnline).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('refreshRuntimeConfig bypasses the cached value (re-checks CR after recovery)', async () => {
    const { loadRuntimeConfig, refreshRuntimeConfig } = await freshModule();
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(okResponse({ oidcEnabled: true, oidcOnline: false, oidcAuthority: 'a', oidcClientId: 'fuel' }))
      .mockResolvedValueOnce(okResponse({ oidcEnabled: true, oidcOnline: true, oidcAuthority: 'a', oidcClientId: 'fuel' }));
    const down = await loadRuntimeConfig();
    expect(down.oidcOnline).toBe(false);
    const up = await refreshRuntimeConfig();
    expect(up.oidcOnline).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
