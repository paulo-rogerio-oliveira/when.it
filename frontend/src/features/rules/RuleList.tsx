import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Trash2 } from "lucide-react";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { deleteRule, listRules, type RuleListItem } from "@/shared/api/rules";

export function RuleList() {
  const [items, setItems] = useState<RuleListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  function refresh() {
    listRules()
      .then(setItems)
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar."));
  }

  useEffect(() => { refresh(); }, []);

  async function handleDelete(item: RuleListItem) {
    const activeWarning = item.status === "active"
      ? " Ela está ativa e será removida do Worker."
      : "";
    if (!window.confirm(
      `Excluir a regra "${item.name}"?${activeWarning} O histórico e outbox ligados a ela também serão removidos. Esta ação não pode ser desfeita.`,
    )) {
      return;
    }

    setError(null);
    setDeletingId(item.id);
    try {
      await deleteRule(item.id);
      refresh();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao excluir.");
      setError(msg);
    } finally {
      setDeletingId(null);
    }
  }

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
                    <div className="flex items-center justify-end gap-3">
                      <Link to={`/rules/${r.id}`} className="text-sm font-medium text-primary hover:underline">
                        Editar reaction
                      </Link>
                      <button
                        type="button"
                        onClick={() => handleDelete(r)}
                        disabled={deletingId === r.id}
                        title="Excluir regra"
                        className="text-muted-foreground hover:text-red-600 disabled:opacity-30 disabled:hover:text-muted-foreground"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
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
