// Auth now lives in the shared @bearsoft/auth-core engine (one implementation across all apps).
// This module re-exports the React adapter so existing `../context/AuthContext` imports keep working.
// See github.com/Trifunovich/auth-core.
export { AuthProvider, useAuth } from '@bearsoft/auth-core/react';
