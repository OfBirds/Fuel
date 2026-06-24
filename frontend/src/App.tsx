import { useState, useEffect, useRef } from 'react';
import { apiFetch } from './lib/api';
import { BrowserRouter, Routes, Route, NavLink, useLocation } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ThemeProvider, useTheme } from './context/ThemeContext';
import { getFontScale, saveFontScale, getOnboardingCompleted, saveOnboardingCompleted } from './lib/storage';
import LoginPage from './pages/LoginPage';
import HomePage from './pages/HomePage';
import SettingsPage from './pages/SettingsPage';
import EntryFormPage from './pages/EntryFormPage';
import AiEntryPage from './pages/AiEntryPage';
import CataloguePage from './pages/CataloguePage';
import WeightPage from './pages/WeightPage';
import StatsPage from './pages/StatsPage';
import OnboardingPage from './pages/OnboardingPage';
import AuthCallbackPage from './pages/AuthCallbackPage';
import './styles/app.css';

const FONT_MIN = 40;
const FONT_MAX = 200;
const FONT_STEP = 20;

function applyFontScale(pct: number) {
  document.documentElement.style.setProperty('--font-scale', String(pct / 100));
}

function ThemeToggle() {
  const { theme, toggleTheme } = useTheme();
  return (
    <button
      className="theme-toggle"
      onClick={toggleTheme}
      aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
      title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
    >
      {theme === 'dark' ? '☀️' : '🌙'}
    </button>
  );
}

function FontSizeControl() {
  const [scale, setScale] = useState(getFontScale);

  useEffect(() => {
    applyFontScale(scale);
    saveFontScale(scale);
  }, [scale]);

  const adjust = (delta: number) =>
    setScale(s => Math.min(FONT_MAX, Math.max(FONT_MIN, s + delta)));

  return (
    <div className="font-control" title="Text size">
      <button onClick={() => adjust(-FONT_STEP)} disabled={scale <= FONT_MIN} aria-label="Smaller text">A−</button>
      <span className="font-control-value">{scale}%</span>
      <button onClick={() => adjust(FONT_STEP)} disabled={scale >= FONT_MAX} aria-label="Larger text">A+</button>
    </div>
  );
}

// Primary navigation now lives inside the user-name dropdown. "Home" is surfaced both as
// the far-left house icon and, labelled "Diary", as the first item in the menu.
const NAV_ITEMS = [
  { to: '/', label: 'Diary', end: true },
  { to: '/catalogue', label: 'Catalogue', end: false },
  { to: '/weight', label: 'Weight', end: false },
  { to: '/stats', label: 'Stats', end: false },
  { to: '/settings', label: 'Settings', end: false },
];

function HomeIcon() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M3 10.5 12 3l9 7.5" />
      <path d="M5 9.25V21h14V9.25" />
    </svg>
  );
}

// The user's name doubles as the navigation menu: clicking it opens a dropdown with the
// nav destinations plus Logout. Closes on outside click, Escape, or selecting an item.
function UserMenu() {
  const { user, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  if (!user) return null;

  return (
    <div className="user-menu" ref={ref}>
      <button
        type="button"
        className="user-menu-trigger"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
      >
        <span className="user-menu-name">{user.name || user.email}</span>
        <span className="user-menu-caret" aria-hidden="true">▾</span>
      </button>
      {open && (
        <div className="user-menu-dropdown" role="menu">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              role="menuitem"
              className={({ isActive }) => (isActive ? 'user-menu-item active' : 'user-menu-item')}
              onClick={() => setOpen(false)}
            >
              {item.label}
            </NavLink>
          ))}
          <div className="user-menu-divider" role="separator" />
          <button
            type="button"
            className="user-menu-item user-menu-logout"
            role="menuitem"
            onClick={() => { setOpen(false); logout(); }}
          >
            Logout
          </button>
        </div>
      )}
    </div>
  );
}

function AppContent() {
  const { user } = useAuth();
  const location = useLocation();
  const [profileChecked, setProfileChecked] = useState(false);
  const [needsOnboarding, setNeedsOnboarding] = useState(false);

  // Check if onboarding is needed (first login, no profile, not explicitly skipped)
  useEffect(() => {
    if (!user) return;
    let alive = true;
    (async () => {
      try {
        const res = await apiFetch(`/api/user/${user.id}/profile`);
        if (alive && res.ok) {
          const p = await res.json();
          const skipped = getOnboardingCompleted();
          setNeedsOnboarding(p.height == null && !skipped);
        }
      } catch { /* if this fails, don't block — let them in */ }
      finally { if (alive) setProfileChecked(true); }
    })();
    return () => { alive = false; };
  }, [user]);

  const handleOnboardingDone = () => {
    saveOnboardingCompleted();
    setNeedsOnboarding(false);
  };

  // The OIDC redirect lands here with no session yet — handle it before the login guard.
  // (Must come after all hooks so the hook order stays stable across renders.)
  if (location.pathname === '/auth/callback') {
    return <AuthCallbackPage />;
  }

  if (!user) {
    return <LoginPage onLoginSuccess={() => {}} />;
  }

  if (!profileChecked) {
    return <div className="app"><main className="app-main"><p className="settings-muted" style={{ textAlign: 'center', marginTop: '4rem' }}>Loading…</p></main></div>;
  }

  if (needsOnboarding) {
    return <OnboardingPage onComplete={handleOnboardingDone} />;
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-header-left">
          <NavLink
            to="/"
            end
            className={({ isActive }) => (isActive ? 'home-link active' : 'home-link')}
            aria-label="Diary (home)"
            title="Diary"
          >
            <HomeIcon />
          </NavLink>
        </div>
        <div className="app-header-right">
          <FontSizeControl />
          <ThemeToggle />
          <UserMenu />
        </div>
      </header>
      <main className="app-main">
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/catalogue" element={<CataloguePage />} />
          <Route path="/weight" element={<WeightPage />} />
          <Route path="/stats" element={<StatsPage />} />
          <Route path="/entry/new" element={<EntryFormPage />} />
          <Route path="/entry/ai" element={<AiEntryPage />} />
          <Route path="/entry/:entryId/edit" element={<EntryFormPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </main>
      <footer className="app-footer">
        <span className="app-brand">Indigo Swallow</span>
      </footer>
    </div>
  );
}

function App() {
  useEffect(() => {
    applyFontScale(getFontScale());
  }, []);

  return (
    <ThemeProvider>
      <AuthProvider>
        <BrowserRouter>
          <AppContent />
        </BrowserRouter>
      </AuthProvider>
    </ThemeProvider>
  );
}

export default App;
