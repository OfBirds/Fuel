// The OIDC runtime config + UserManager engine now lives in @bearsoft/auth-core; re-exported here so
// existing `../lib/oidc` imports keep working. See github.com/Trifunovich/auth-core.
export { loadRuntimeConfig, getUserManager } from '@bearsoft/auth-core';
export type { RuntimeConfig } from '@bearsoft/auth-core';
