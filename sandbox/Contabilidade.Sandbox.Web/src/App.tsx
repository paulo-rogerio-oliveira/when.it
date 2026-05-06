import { Routes, Route, Navigate } from 'react-router-dom';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import Empresas from './pages/Empresas';
import PlanoContas from './pages/PlanoContas';
import Lancamentos from './pages/Lancamentos';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<Navigate to="/dashboard" replace />} />
        <Route path="/dashboard" element={<Dashboard />} />
        <Route path="/empresas" element={<Empresas />} />
        <Route path="/plano-contas" element={<PlanoContas />} />
        <Route path="/lancamentos" element={<Lancamentos />} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  );
}
