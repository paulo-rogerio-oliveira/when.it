import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { listRules, type RuleListItem } from "@/shared/api/rules";

export function RuleList() {
  const [items, setItems] = useState<RuleListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listRules()
      .then(setItems)
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar."));
  }, []);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Regras</h2>
        <p className="text-sm text-muted-foreground">
          Regras inferidas a partir de gravações. Edite a reaction independentemente —
          não precisa gravar de novo.
        </p>
      </div>

      {error && <Alert variant="destructive">{error}</Alert>}

      {items === null && !error && (
        <p className="text-sm text-muted-foreground">Carregando…</p>
      )}

      {items !== null && items.length === 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Nenhuma regra</CardTitle>
            <CardDescription>
              Crie uma regra a partir de uma gravação em <Link to="/recordings" className="underline">Gravações</Link>.
            </CardDescription>
          </CardHeader>
          <CardContent />
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
                <th className="px-4 py-2 font-medium">Versão</th>
                <th className="px-4 py-2 font-medium">Atualizada</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {items.map((r) => (
                <tr key={r.id} className="border-t">
                  <td className="px-4 py-2 font-medium">
                    <Link to={`/rules/${r.id}`} className="hover:underline">
                      {r.name}
                    </Link>
                    {r.description && (
                      <p className="text-xs text-muted-foreground">{r.description}</p>
                    )}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{r.connectionName}</td>
                  <td className="px-4 py-2">
                    <RuleStatusBadge status={r.status} />
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">v{r.version}</td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {new Date(r.updatedAt).toLocaleString()}
                  </td>
                  <td className="px-4 py-2 text-right">
                    <Link to={`/rules/${r.id}`} className="text-sm font-medium text-primary hover:underline">
                      Editar reaction
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

function RuleStatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    draft: "bg-slate-100 text-slate-800",
    testing: "bg-amber-100 text-amber-800",
    active: "bg-emerald-100 text-emerald-800",
    paused: "bg-yellow-100 text-yellow-800",
    archived: "bg-muted text-muted-foreground",
  };
  const cls = styles[status] ?? styles.archived;
  return <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{status}</span>;
}
