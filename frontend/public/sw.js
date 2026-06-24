// Indigo Swallow service worker. Plain JS (served as-is from /public — no TS here).
// Strategy: never touch /api (always network, so auth'd data is never cached/stale);
// network-first for navigations so a new deploy is picked up immediately, falling back to
// the cached shell when offline; cache-first for hashed static assets (immutable, fast).
// Cache name is stamped with the build version (APP_VERSION) at build time — see the
// stamp-service-worker plugin in vite.config.ts. A new version ⇒ a new cache name ⇒ the
// activate handler below deletes the previous cache, so old hashed assets from prior deploys
// don't accumulate (the bug behind "fuel alone had ~150 cached items").
const CACHE = 'indigo-swallow-__APP_VERSION__';
const SHELL = ['/', '/index.html', '/manifest.json', '/indigo-swallow.svg'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL).catch(() => {})));
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    )
  );
  self.clients.claim();
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return; // let cross-origin (fonts, IdP) pass through
  if (url.pathname.startsWith('/api/')) return;     // never cache API / auth responses

  // Navigations / HTML: network-first (fresh deploys win), cached shell as offline fallback.
  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request)
        .then((res) => {
          const copy = res.clone();
          caches.open(CACHE).then((cache) => cache.put('/index.html', copy));
          return res;
        })
        .catch(() => caches.match('/index.html'))
    );
    return;
  }

  // Static assets (hashed, immutable): cache-first, populate on first miss.
  event.respondWith(
    caches.match(request).then(
      (cached) =>
        cached ||
        fetch(request).then((res) => {
          if (res && res.status === 200 && res.type === 'basic') {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(request, copy));
          }
          return res;
        })
    )
  );
});
