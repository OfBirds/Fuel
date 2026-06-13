import { useState, useEffect } from 'react';
import { BrowserRouter, Routes, Route, NavLink } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ThemeProvider, useTheme } from './context/ThemeContext';
import { getFontScale, saveFontScale } from './lib/storage';
import LoginPage from './pages/LoginPage';
import HomePage from './pages/HomePage';
import SettingsPage from './pages/SettingsPage';
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

function AppContent() {
  const { user, logout } = useAuth();

  if (!user) {
    return <LoginPage onLoginSuccess={() => {}} />;
  }

  return (
    <div className="app">
      <header className="app-header">
        <FontSizeControl />
        <nav className="app-nav">
          <NavLink to="/" end className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>Home</NavLink>
          <NavLink to="/settings" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>Settings</NavLink>
        </nav>
        <div className="app-header-right">
          <span className="app-header-email">{user.email}</span>
          <ThemeToggle />
          <button className="logout-button" onClick={logout}>Logout</button>
        </div>
      </header>
      <main className="app-main">
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </main>
      <footer className="app-footer">
        <span className="app-brand">Fuel</span>
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
