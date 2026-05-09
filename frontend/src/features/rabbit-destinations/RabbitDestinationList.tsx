import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { Alert } from "@/shared/components/ui/alert";
import {
  deleteRabbitDestination,
  listRabbitDestinations,
  testRabbitDestinationById,
  type RabbitDestinationListItem,
} from "@/shared/api/rabbit-destinations";

export function RabbitDestinationList() {
  const navigate = useNavigate();
  const [items, setItems] = useState<RabbitDestinationListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);

  const load = async () => {
    try {
      setError(null);
      setItems(await listRabbitDestinations());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao carregar.");
    }
  };

  useEffect(() => {
    load();
  }, []);

  const onTest = async (id: string) => {
    setTestingId(id);
    try {
      await testRabbitDestinationById(id);
    } catch {
      /* status fica refletido no reload via campo Status */
    }
    setTestingId(null);
    await load();
  };

  const onDelete = async (id: string, name: string) => {
    if (!window.confirm(`Apagar o destino "${name}"?`)) return;
    await deleteRabbitDestination(id);
    await load();
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-semibold">Destinos RabbitMQ</h2>
          <p className="text-sm text-muted-foreground">
            Brokers para publicar reactions do tipo <code>rabbit</code>.
          </p>
        </div>
        <Button onClick={() => navigate("/rabbit-destinations/new")}>Novo destino</Button>
      </div>

      {error && <Alert variant="destructive">{error}</Alert>}

      {items === null && !error && <p className="text-sm text-muted-foreground">Carregando…</p>}

      {items !== null && items.length === 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Nenhum destino cadastrado</CardTitle>
            <CardDescription>
              Cadastre o broker (host, vhost, credenciais, exchange default) para que regras com
              reaction <code>rabbit</code> possam apontar para ele.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button onClick={() => navigate("/rabbit-destinations/new")}>Cadastrar o primeiro</Button>
          </CardContent>
        </Card>
      )}

      {items !== null && items.length > 0 && (
        <div className="overflow-hidden rounded-md border bg-background">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left">
              <tr>
                <th className="px-4 py-2 font-medium">Nome</th>
                <th className="px-4 py-2 font-medium">Host</th>
                <th className="px-4 py-2 font-medium">VHost</th>
                <th className="px-4 py-2 font-medium">Default exchange</th>
                <th className="px-4 py-2 font-medium">TLS</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Último teste</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {items.map((d) => (
                <tr key={d.id} className="border-t">
                  <td className="px-4 py-2 font-medium">
                    <Link to={`/rabbit-destinations/${d.id}`} className="hover:underline">
                      {d.name}
                    </Link>
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {d.host}:{d.port || (d.useTls ? 5671 : 5672)}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{d.virtualHost}</td>
                  <td className="px-4 py-2 text-muted-foreground">{d.defaultExchange || "—"}</td>
                  <td className="px-4 py-2 text-muted-foreground">{d.useTls ? "sim" : "não"}</td>
                  <td className="px-4 py-2">
                    <StatusBadge status={d.status} />
                  </td>
                  <td className="px-4 py-2 text-muted-foreground" title={d.lastError ?? undefined}>
                    {d.lastTestedAt ? new Date(d.lastTestedAt).toLocaleString() : "—"}
                  </td>
                  <td className="px-4 py-2 text-right">
                    <div className="flex justify-end gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => onTest(d.id)}
                        disabled={testingId === d.id}
                      >
                        {testingId === d.id ? "Testando…" : "Testar"}
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => onDelete(d.id, d.name)}>
                        Apagar
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: "bg-emerald-100 text-emerald-800",
    inactive: "bg-muted text-muted-foreground",
    error: "bg-red-100 text-red-800",
  };
  const cls = styles[status] ?? styles.inactive;
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{status}</span>;
}
