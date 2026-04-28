import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { Alert } from "@/shared/components/ui/alert";
import { listRecordings, type RecordingListItem } from "@/shared/api/recordings";

export function RecordingList() {
  const navigate = useNavigate();
  const [items, setItems] = useState<RecordingListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listRecordings()
      .then(setItems)
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar."));
  }, []);

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-semibold">Gravações</h2>
          <p className="text-sm text-muted-foreground">
            Sessões de captura de operações no SQL Server alvo.
          </p>
        </div>
        <Button onClick={() => navigate("/recordings/new")}>Nova gravação</Button>
      </div>

      {error && <Alert variant="destructive">{error}</Alert>}

      {items === null && !error && <p className="text-sm text-muted-foreground">Carregando…</p>}

      {items !== null && items.length === 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Nenhuma gravação</CardTitle>
            <CardDescription>
              Inicie uma gravação para capturar a operação que você quer transformar em evento.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button onClick={() => navigate("/recordings/new")}>Iniciar primeira gravação</Button>
          </CardContent>
        </Card>
      )}

      {items !== null && items.length > 0 && (
        <div className="overflow-hidden rounded-md border bg-background">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left">
              <tr>
                <th className="px-4 py-2 font-medium">Nome</th>
                <th className="px-4 py-2 font-medium">Conexão</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Iniciada</th>
                <th className="px-4 py-2 font-medium">Eventos</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {items.map((r) => (
                <tr key={r.id} className="border-t">
                  <td className="px-4 py-2 font-medium">
                    <Link to={`/recordings/${r.id}/session`} className="hover:underline">
                      {r.name}
                    </Link>
                    {r.description && (
                      <p className="text-xs text-muted-foreground">{r.description}</p>
                    )}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{r.connectionName}</td>
                  <td className="px-4 py-2">
                    <RecordingStatusBadge status={r.status} />
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {new Date(r.startedAt).toLocaleString()}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{r.eventCount}</td>
                  <td className="px-4 py-2 text-right">
                    <Link
                      to={r.status === "recording"
                        ? `/recordings/${r.id}/session`
                        : `/recordings/${r.id}/review`}
                      className="text-sm font-medium text-primary hover:underline"
                    >
                      {r.status === "recording" ? "Abrir" : "Revisar"}
                    </Link>
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

function RecordingStatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    recording: "bg-red-100 text-red-800",
    completed: "bg-emerald-100 text-emerald-800",
    failed: "bg-red-100 text-red-800",
    discarded: "bg-muted text-muted-foreground",
  };
  const cls = styles[status] ?? styles.discarded;
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{status}</span>;
}
