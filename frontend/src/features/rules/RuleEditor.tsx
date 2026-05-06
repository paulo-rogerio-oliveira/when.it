import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Alert } from "@/shared/components/ui/alert";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  activateRule,
  getRule,
  pauseRule,
  testReaction,
  updateRule,
  type RuleDetail,
  type TestReactionResult,
} from "@/shared/api/rules";
import {
  ReactionEditor,
  emptyReactionState,
  extractReactionFromDefinition,
  mergeReactionIntoDefinition,
  reactionStateValid,
  type ReactionState,
} from "@/shared/components/ReactionEditor";

type LoadState =
  | { kind: "loading" }
  | { kind: "ready"; rule: RuleDetail }
  | { kind: "error"; message: string };

export function RuleEditor() {
  const { id = "" } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [state, setState] = useState<LoadState>({ kind: "loading" });
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [reaction, setReaction] = useState<ReactionState>(() => emptyReactionState());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const [testPayload, setTestPayload] = useState<string>('{ "after": { "id": 1 } }');
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<TestReactionResult | null>(null);

  useEffect(() => {
    let cancelled = false;
    getRule(id)
      .then((rule) => {
        if (cancelled) return;
        setState({ kind: "ready", rule });
        setName(rule.name);
        setDescription(rule.description ?? "");
        setReaction(extractReactionFromDefinition(rule.definition));
      })
      .catch((e) => {
        if (cancelled) return;
        setState({ kind: "error", message: e instanceof Error ? e.message : "Falha ao carregar regra." });
      });
    return () => { cancelled = true; };
  }, [id]);

  const triggerSummary = useMemo(() => {
    if (state.kind !== "ready") return null;
    try {
      const obj = JSON.parse(state.rule.definition);
      const t = obj?.trigger;
      if (!t) return null;
      const op = String(t.operation ?? "any").toUpperCase();
      const tbl = t.schema ? `${t.schema}.${t.table}` : String(t.table ?? "?");
      return { op, tbl, db: t.database ?? null };
    } catch {
      return null;
    }
  }, [state]);

  if (state.kind === "loading") return <p className="text-sm text-muted-foreground">Carregando…</p>;
  if (state.kind === "error") return <Alert variant="destructive">{state.message}</Alert>;

  const { rule } = state;
  const lockedForEdit = rule.status === "active" || rule.status === "archived";

  const onSave = async () => {
    setError(null);
    if (!name.trim()) {
      setError("Nome é obrigatório.");
      return;
    }
    const validity = reactionStateValid(reaction);
    if (!validity.valid) {
      setError(validity.reason ?? "Reaction inválida.");
      return;
    }

    let definition: string;
    try {
      definition = mergeReactionIntoDefinition(rule.definition, reaction);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao montar reaction.");
      return;
    }

    setSaving(true);
    try {
      const updated = await updateRule(rule.id, {
        name: name.trim(),
        description: description.trim() || undefined,
        definition,
      });
      setState({ kind: "ready", rule: updated });
      setSavedAt(new Date());
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao salvar.");
      setError(msg);
    } finally {
      setSaving(false);
    }
  };

  const onActivate = async () => {
    setError(null);
    try {
      const updated = await activateRule(rule.id);
      setState({ kind: "ready", rule: updated });
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao ativar.");
      setError(msg);
    }
  };

  const onPause = async () => {
    setError(null);
    try {
      const updated = await pauseRule(rule.id);
      setState({ kind: "ready", rule: updated });
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao pausar.");
      setError(msg);
    }
  };

  const onTest = async () => {
    setError(null);
    setTestResult(null);
    let parsed: unknown = undefined;
    if (testPayload.trim()) {
      try {
        parsed = JSON.parse(testPayload);
      } catch {
        setError("Payload de teste não é JSON válido.");
        return;
      }
    }
    setTesting(true);
    try {
      const res = await testReaction(rule.id, parsed);
      setTestResult(res);
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao enfileirar teste.");
      setError(msg);
    } finally {
      setTesting(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-semibold">{rule.name}</h2>
          <p className="text-xs text-muted-foreground">
            {rule.connectionName} • v{rule.version} • status {rule.status}
            {rule.sourceRecordingId && (
              <> • origem: gravação {rule.sourceRecordingId.slice(0, 8)}…</>
            )}
          </p>
        </div>
        <Button variant="ghost" onClick={() => navigate("/rules")}>Voltar</Button>
      </div>

      {lockedForEdit && (
        <Alert variant="default">
          Regra em status <code>{rule.status}</code> não pode ser editada. Pause antes de alterar.
        </Alert>
      )}

      {error && <Alert variant="destructive">{error}</Alert>}
      {savedAt && !error && (
        <Alert variant="default">
          Salvo às {savedAt.toLocaleTimeString()} — nova versão v{rule.version}.
        </Alert>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Identificação</CardTitle>
          <CardDescription>Nome e descrição visíveis no log e dashboards.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="space-y-2">
            <Label htmlFor="rule-name">Nome</Label>
            <Input
              id="rule-name"
              value={name}
              disabled={lockedForEdit}
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="rule-desc">Descrição</Label>
            <Input
              id="rule-desc"
              value={description}
              disabled={lockedForEdit}
              onChange={(e) => setDescription(e.target.value)}
            />
          </div>
        </CardContent>
      </Card>

      {triggerSummary && (
        <Card>
          <CardHeader>
            <CardTitle>Trigger</CardTitle>
            <CardDescription>
              Definido na inferência. Para alterar, gere uma nova regra a partir de uma gravação.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm font-mono">
              {triggerSummary.op} {triggerSummary.tbl}
              {triggerSummary.db && (
                <span className="text-muted-foreground"> em {triggerSummary.db}</span>
              )}
            </div>
          </CardContent>
        </Card>
      )}

      <div className={lockedForEdit ? "pointer-events-none opacity-60" : undefined}>
        <ReactionEditor state={reaction} onChange={setReaction} />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Testar reaction</CardTitle>
          <CardDescription>
            Enfileira uma linha em <code>dbsense.outbox</code> usando a reaction salva. O
            ReactionExecutorWorker pega no próximo poll (~500ms). Útil pra validar
            executável/SQL/exchange sem ter que produzir o evento real no banco alvo.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="space-y-2">
            <Label htmlFor="test-payload">Payload de teste (JSON)</Label>
            <textarea
              id="test-payload"
              value={testPayload}
              onChange={(e) => setTestPayload(e.target.value)}
              rows={4}
              className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
              placeholder={'{ "after": { "id": 42 } }'}
            />
            <p className="text-xs text-muted-foreground">
              Resolve placeholders <code>$.after.X</code>, <code>$rule.id</code>,
              <code> $payload.json</code> antes de gravar no outbox.
            </p>
          </div>
          {testResult && (
            <Alert variant="default">
              Enfileirado: outbox <code>#{testResult.outboxId}</code> (events_log{" "}
              <code>#{testResult.eventsLogId}</code>) — tipo <code>{testResult.reactionType}</code>.
              Acompanhe o status no banco.
            </Alert>
          )}
          <div className="flex justify-end">
            <Button variant="outline" onClick={onTest} disabled={testing}>
              {testing ? "Enfileirando…" : "Disparar reaction"}
            </Button>
          </div>
        </CardContent>
      </Card>

      <details className="rounded-md border bg-muted/30 p-2">
        <summary className="cursor-pointer text-xs font-medium">Definição completa (JSON)</summary>
        <pre className="mt-2 max-h-72 overflow-auto whitespace-pre-wrap break-words font-mono text-[11px]">
          {rule.definition}
        </pre>
      </details>

      <div className="flex flex-wrap justify-end gap-2">
        {rule.status === "active" ? (
          <Button variant="outline" onClick={onPause}>Pausar</Button>
        ) : (
          <Button variant="outline" onClick={onActivate} disabled={rule.status === "archived"}>
            Ativar
          </Button>
        )}
        <Button onClick={onSave} disabled={lockedForEdit || saving || !name.trim()}>
          {saving ? "Salvando…" : "Salvar alterações"}
        </Button>
      </div>
    </div>
  );
}
