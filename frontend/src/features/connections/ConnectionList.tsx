import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { Alert } from "@/shared/components/ui/alert";
import {
  deleteConnection,
  listConnections,
  testConnectionById,
  type ConnectionListItem,
} from "@/shared/api/connections";

export function ConnectionList() {
  const navigate = useNavigate();
  const [items, setItems] = useState<ConnectionListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);

  const load = async () => {
    try {
      setError(null);
      setItems(await listConnections());
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
      await testConnectionById(id);
    } catch {
      /* result is reflected in updated row after reload */
    }
    setTestingId(null);
    await load();
  };

  const onDelete = async (id: string, name: string) => {
    if (!window.confirm(`Apagar a conexão "${name}"?`)) return;
    await deleteConnection(id);
    await load();
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-semibold">Conexões</h2>
          <p className="text-sm text-muted-foreground">
            SQL Servers observados pelo DbSense.
          </p>
        </div>
        <Button onClick={() => navigate("/connections/new")}>Nova conexão</Button>
      </div>

      {error && <Alert variant="destructive">{error}</Alert>}

      {items === null && !error && <p className="text-sm text-muted-foreground">Carregando…</p>}

      {items !== null && items.length === 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Nenhuma conexão cadastrada</CardTitle>
            <CardDescription>
              Cadastre o SQL Server alvo do sistema legado para começar a gravar operações.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button onClick={() => navigate("/connections/new")}>Cadastrar a primeira</Button>
          </CardContent>
        </Card>
      )}

      {items !== null && items.length > 0 && (
        <div className="overflow-hidden rounded-md border bg-background">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left">
              <tr>
                <th className="px-4 py-2 font-medium">Nome</th>
                <th className="px-4 py-2 font-medium">Servidor</th>
                <th className="px-4 py-2 font-medium">Database</th>
                <th className="px-4 py-2 font-medium">Auth</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Último teste</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {items.map((c) => (
                <tr key={c.id} className="border-t">
                  <td className="px-4 py-2 font-medium">
                    <Link to={`/connections/${c.id}`} className="hover:underline">
                      {c.name}
                    </Link>
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{c.server}</td>
                  <td className="px-4 py-2 text-muted-foreground">{c.database}</td>
                  <td className="px-4 py-2 text-muted-foreground">{c.authType}</td>
                  <td className="px-4 py-2">
                    <StatusBadge status={c.status} />
                  </td>
                  <td className="px-4 py-2 text-muted-foreground" title={c.lastError ?? undefined}>
                    {c.lastTestedAt ? new Date(c.lastTestedAt).toLocaleString() : "—"}
                  </td>
                  <td className="px-4 py-2 text-right">
                    <div className="flex justify-end gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => onTest(c.id)}
                        disabled={testingId === c.id}
                      >
                        {testingId === c.id ? "Testando…" : "Testar"}
                      </Button>
                      <Button size="sm" variant="ghost" onClick={() => onDelete(c.id, c.name)}>
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
    testing: "bg-amber-100 text-amber-800",
    error: "bg-red-100 text-red-800",
  };
  const cls = styles[status] ?? styles.inactive;
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{status}</span>;
}
