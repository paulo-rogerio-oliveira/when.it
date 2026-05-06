import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface EmpresaState {
  empresaId: string | null;
  setEmpresaId: (id: string | null) => void;
}

export const useEmpresaStore = create<EmpresaState>()(
  persist(
    (set) => ({
      empresaId: null,
      setEmpresaId: (id) => set({ empresaId: id })
    }),
    { name: 'sandbox-empresa' }
  )
);
