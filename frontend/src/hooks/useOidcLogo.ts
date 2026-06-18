import { useState, useEffect } from 'react';
import { loadRuntimeConfig } from '../lib/oidc';

// CrimsonRaven's current logo, themed (light/dark), pulled live from the IdP via /api/config
// (the backend scrapes it from CR's login page — single source). Returns undefined until
// loaded or when SSO isn't configured.
export function useOidcLogo(): string | undefined {
  const [logo, setLogo] = useState<{ light?: string; dark?: string }>({});
  useEffect(() => {
    loadRuntimeConfig()
      .then((c) => setLogo({ light: c.oidcLogoUrl, dark: c.oidcLogoUrlDark }))
      .catch(() => {});
  }, []);
  const dark = document.documentElement.getAttribute('data-theme') === 'dark';
  return (dark ? logo.dark : logo.light) || logo.light || logo.dark;
}
