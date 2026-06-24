import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './styles/index.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

// Register the PWA service worker only in production builds. In dev the SW's cache-first
// asset strategy serves stale Vite modules across restarts/dep re-optimization, which boots
// to a blank page; prod (hashed, version-stamped cache) is unaffected.
if (import.meta.env.PROD && 'serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch((error) => {
    console.log('ServiceWorker registration failed: ', error);
  });
}
