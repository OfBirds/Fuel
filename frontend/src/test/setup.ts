import '@testing-library/jest-dom/vitest';

// Polyfill localStorage for Node v26+ where it's experimental and undefined in jsdom.
if (typeof globalThis.localStorage === 'undefined') {
  const store = new Map<string, string>();
  const storage: Record<string, any> = {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => { store.set(key, value); storage[key] = value; },
    removeItem: (key: string) => { store.delete(key); delete storage[key]; },
    clear: () => { store.clear(); for (const k of Object.keys(storage)) { if (typeof storage[k] !== 'function') delete storage[k]; } },
    get length() { return store.size; },
    key: (index: number) => Array.from(store.keys())[index] ?? null,
  };
  globalThis.localStorage = storage as Storage;
}
