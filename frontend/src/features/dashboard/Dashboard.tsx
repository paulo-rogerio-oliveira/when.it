import { Link } from "react-router-dom";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";

export function Dashboard() {
  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Visão geral</h2>
        <p className="text-sm text-muted-foreground">
          Setup concluído. Use os atalhos abaixo para começar.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <Link to="/connections">
          <Card className="transition-colors hover:bg-muted/50">
            <CardHeader>
              <CardTitle>Conexões</CardTitle>
              <CardDescription>
                Cadastre e teste o SQL Server alvo do sistema legado.
              </CardDescription>
            </CardHeader>
          </Card>
        </Link>
        <Link to="/recordings/new">
          <Card className="transition-colors hover:bg-muted/50">
            <CardHeader>
              <CardTitle>Nova gravação</CardTitle>
              <CardDescription>
                Capture uma operação real para inferir uma regra.
              </CardDescription>
            </CardHeader>
          </Card>
        </Link>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Próximas fatias</CardTitle>
        </CardHeader>
        <CardContent>
          <ul className="list-disc space-y-1 pl-6 text-sm text-muted-foreground">
            <li>Coletor XEvents (worker) — captura eventos brutos durante gravações.</li>
            <li>Inferência de regras a partir de gravações.</li>
            <li>Destinos RabbitMQ + outbox transacional.</li>
            <li>Dashboard em tempo real (SSE).</li>
          </ul>
        </CardContent>
      </Card>
    </div>
  );
}
