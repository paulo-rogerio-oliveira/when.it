import { Button } from "@/shared/components/ui/button";

export function Complete({ onFinish }: { onFinish: () => void }) {
  return (
    <div className="space-y-4">
      <div className="rounded-md border border-emerald-300 bg-emerald-50 p-4 text-sm text-emerald-900">
        Configuração concluída. Faça login para começar a usar o DbSense.
      </div>
      <div className="flex justify-end">
        <Button onClick={onFinish}>Ir para o login</Button>
      </div>
    </div>
  );
}
