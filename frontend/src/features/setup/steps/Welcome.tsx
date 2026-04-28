import { Button } from "@/shared/components/ui/button";

export function Welcome({ onContinue }: { onContinue: () => void }) {
  return (
    <div className="space-y-4">
      <p>Vamos preparar o DbSense no seu ambiente. Antes de começar, tenha em mãos:</p>
      <ul className="list-disc space-y-2 pl-6 text-sm text-muted-foreground">
        <li>
          Credencial do <strong>SQL Server de controle</strong> (com permissão para criar schema e
          tabelas).
        </li>
        <li>
          Credencial do <strong>SQL Server alvo</strong> (com{" "}
          <code className="rounded bg-muted px-1">ALTER ANY EVENT SESSION</code> +{" "}
          <code className="rounded bg-muted px-1">VIEW SERVER STATE</code>).
        </li>
        <li>Host, porta e credencial do <strong>RabbitMQ</strong> destino.</li>
      </ul>
      <p className="text-sm text-muted-foreground">
        Os passos 3 e 4 (alvo + RabbitMQ) ficam acessíveis após o login.
      </p>
      <div className="flex justify-end">
        <Button onClick={onContinue}>Começar</Button>
      </div>
    </div>
  );
}
