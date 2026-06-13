import { useAuth } from '../context/AuthContext';

/**
 * Placeholder landing page. This is where your app's first real feature goes.
 * The user is already authenticated by the time this renders (see App.tsx).
 */
function HomePage() {
  const { user } = useAuth();

  return (
    <div className="home-page">
      <h1>Welcome to Fuel</h1>
      <p>You're signed in as {user?.email}.</p>
      <p className="home-hint">
        This is a placeholder home page. Replace it with your first feature — the
        auth, database, API, settings, notifications, and deploy rails are already
        wired up for you.
      </p>
    </div>
  );
}

export default HomePage;
