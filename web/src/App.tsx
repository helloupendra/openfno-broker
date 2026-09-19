import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { adminKey } from './lib/api';
import { AccountDetail } from './pages/AccountDetail';
import { Accounts } from './pages/Accounts';
import { Activity } from './pages/Activity';
import { Overview } from './pages/Overview';
import { Rules } from './pages/Rules';
import { SignIn } from './pages/SignIn';
import { Terminal } from './pages/Terminal';

/** The back office needs the admin key; without one, every page sends you to sign in. */
function Guard({ children }: { children: ReactNode }) {
  const location = useLocation();
  return adminKey.get() ? children : <Navigate to="/signin" replace state={{ from: location.pathname }} />;
}

export function App() {
  return (
    <Routes>
      <Route path="/signin" element={<SignIn />} />
      <Route path="/" element={<Guard><Overview /></Guard>} />
      <Route path="/accounts" element={<Guard><Accounts /></Guard>} />
      <Route path="/accounts/:clientId" element={<Guard><AccountDetail /></Guard>} />
      <Route path="/activity" element={<Guard><Activity /></Guard>} />
      <Route path="/terminal" element={<Guard><Terminal /></Guard>} />
      <Route path="/rules" element={<Guard><Rules /></Guard>} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
