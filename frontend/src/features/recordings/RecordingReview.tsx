import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Alert } from "@/shared/components/ui/alert";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  getRecording,
  listRecordingEvents,
  type RecordingDetail,
  type RecordingEventItem,
} from "@/shared/api/recordings";
import {
  createRule,
  inferRule,
  type ClassifiedEvent,
  type InferRuleResponse,
  type InferredRulePayload,
} from "@/shared/api/rules";
import {
  ReactionEditor,
  emptyReactionState,
  mergeReactionIntoDefinition,
  reactionStateValid,
  type ReactionState,
} from "@/shared/components/ReactionEditor";

type Engine = "heuristic" | "llm";

type LoadState =
  | { kind: "loading" }
  | { kind: "ready"; rec: RecordingDetail; events: RecordingEventItem[]; inference: InferRuleResponse }
  | { kind: "error"; message: string };

export function RecordingReview() {
  const { id = "" } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [state, setState] = useState<LoadState>({ kind: "loading" });
  const [draftByEngine, setDraftByEngine] = useState<Record<Engine, { name: string; description: string }>>({
    heuristic: { name: "", description: "" },
    llm: { name: "", description: "" },
  });
  const [savingEngine, setSavingEngine] = useState<Engine | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [reaction, setReaction] = useState<ReactionState>(() => emptyReactionState());

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const rec = await getRecording(id);
        const allEvents: RecordingEventItem[] = [];
        let cursor: number | undefined = undefined;
        for (let i = 0; i < 20; i++) {
          const page = await listRecordingEvents(id, cursor, 200);
          allEvents.push(...page.items);
          if (page.items.length < 200 || page.nextCursor == null) break;
          cursor = page.nextCursor;
        }
        const inference = await inferRule(id);
        if (cancelled) return;
        setState({ kind: "ready", rec, events: allEvents, inference });
        if (inference.heuristic.rule) {
          setDraftByEngine((d) => ({
            ...d,
            heuristic: {
              name: inference.heuristic.rule!.suggestedName,
              description: inference.heuristic.rule!.suggestedDescription,
            },
          }));
        }
        if (inference.llm.rule) {
          setDraftByEngine((d) => ({
            ...d,
            llm: {
              name: inference.llm.rule!.suggestedName,
              description: inference.llm.rule!.suggestedDescription,
            },
          }));
        }
      } catch (e) {
        setState({
          kind: "error",
          message: e instanceof Error ? e.message : "Falha ao carregar gravação.",
        });
      }
    })();
    return () => { cancelled = true; };
  }, [id]);

  const onSave = async (engine: Engine) => {
    if (state.kind !== "ready") return;
    const rule = engine === "heuristic" ? state.inference.heuristic.rule : state.inference.llm.rule;
    if (!rule) return;
    const draft = draftByEngine[engine];

    const validity = reactionStateValid(reaction);
    if (!validity.valid) {
      setSaveError(validity.reason ?? "Reaction inválida.");
      return;
    }

    let definition = rule.definitionJson;
    try {
      definition = mergeReactionIntoDefinition(rule.definitionJson, reaction);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : "Falha ao montar reaction.");
      return;
    }

    setSavingEngine(engine);
    setSaveError(null);
    try {
      const created = await createRule({
        connectionId: state.rec.connectionId,
        sourceRecordingId: state.rec.id,
        name: draft.name.trim(),
        description: draft.description.trim() || undefined,
        definition,
      });
      navigate(`/rules/${created.id}`);
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao salvar regra.");
      setSaveError(msg);
    } finally {
      setSavingEngine(null);
    }
  };

  const reactionValid = reactionStateValid(reaction).valid;

  if (state.kind === "loading") return <p className="text-sm text-muted-foreground">Carregando…</p>;
  if (state.kind === "error") return <Alert variant="destructive">{state.message}</Alert>;

  const { rec, events, inference } = state;
  const heurClassMap = new Map(inference.heuristic.events.map((e) => [e.eventId, e]));
  const llmClassMap = new Map(inference.llm.events.map((e) => [e.eventId, e]));
  const showLlm = inference.llm.enabled;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-semibold">{rec.name}</h2>
          {rec.description && <p className="text-sm text-muted-foreground">{rec.description}</p>}
          <p className="text-xs text-muted-foreground">
            {rec.connectionName} • {events.length} eventos •{" "}
            {rec.stoppedAt
              ? `${formatDuration(rec.startedAt, rec.stoppedAt)} de duração`
              : `iniciada em ${new Date(rec.startedAt).toLocaleString()}`}
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="ghost" onClick={() => navigate("/recordings")}>Voltar</Button>
          <Button variant="outline" onClick={() => navigate("/recordings/new")}>
            Gravar novamente
          </Button>
        </div>
      </div>

      {saveError && <Alert variant="destructive">{saveError}</Alert>}

      <ReactionEditor state={reaction} onChange={setReaction} />

      <div className={`grid gap-4 ${showLlm ? "lg:grid-cols-2" : "lg:grid-cols-1"}`}>
        <EngineCard
          title="Heurística"
          subtitle="Regras determinísticas a partir do parser SQL."
          badge="parser"
          badgeClass="bg-slate-100 text-slate-700"
          enabled={true}
          success={inference.heuristic.success}
          error={inference.heuristic.error}
          rule={inference.heuristic.rule}
          name={draftByEngine.heuristic.name}
          description={draftByEngine.heuristic.description}
          onNameChange={(v) => setDraftByEngine((d) => ({ ...d, heuristic: { ...d.heuristic, name: v } }))}
          onDescriptionChange={(v) => setDraftByEngine((d) => ({ ...d, heuristic: { ...d.heuristic, description: v } }))}
          saving={savingEngine === "heuristic"}
          disabledSave={savingEngine !== null || !reactionValid}
          onSave={() => onSave("heuristic")}
        />

        {showLlm && (
          <EngineCard
            title="IA (Claude)"
            subtitle="Apoiada na descrição e no SQL pra escolher o evento principal."
            badge={inference.llm.inputTokens != null
              ? `${inference.llm.inputTokens}+${inference.llm.outputTokens} tokens`
              : "claude"}
            badgeClass="bg-violet-100 text-violet-800"
            enabled={inference.llm.enabled}
            success={inference.llm.success}
            error={inference.llm.error}
            rule={inference.llm.rule}
            reasoning={inference.llm.reasoning}
            name={draftByEngine.llm.name}
            description={draftByEngine.llm.description}
            onNameChange={(v) => setDraftByEngine((d) => ({ ...d, llm: { ...d.llm, name: v } }))}
            onDescriptionChange={(v) => setDraftByEngine((d) => ({ ...d, llm: { ...d.llm, description: v } }))}
            saving={savingEngine === "llm"}
            disabledSave={savingEngine !== null || !reactionValid}
            onSave={() => onSave("llm")}
          />
        )}
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Eventos capturados</CardTitle>
          <CardDescription>
            Cores: <span className="text-emerald-700">verde</span>=principal,
            {" "}<span className="text-amber-700">amarelo</span>=correlação,
            {" "}cinza=ruído. Quando os dois motores discordam, mostramos as duas classificações.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-2">
          {events.length === 0 ? (
            <p className="text-sm text-muted-foreground">Sem eventos.</p>
          ) : (
            <ul className="space-y-2">
              {events.map((ev) => (
                <EventRow
                  key={ev.id}
                  ev={ev}
                  heur={heurClassMap.get(ev.id)}
                  llm={showLlm ? llmClassMap.get(ev.id) : undefined}
                />
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function EngineCard({
  title, subtitle, badge, badgeClass, enabled, success, error, rule, reasoning,
  name, description, onNameChange, onDescriptionChange, saving, disabledSave, onSave,
}: {
  title: string;
  subtitle: string;
  badge: string;
  badgeClass: string;
  enabled: boolean;
  success: boolean;
  error: string | null;
  rule: InferredRulePayload | null;
  reasoning?: string | null;
  name: string;
  description: string;
  onNameChange: (v: string) => void;
  onDescriptionChange: (v: string) => void;
  saving: boolean;
  disabledSave: boolean;
  onSave: () => void;
}) {
  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between">
          <div>
            <CardTitle className="flex items-center gap-2">
              {title}
              <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${badgeClass}`}>
                {badge}
              </span>
            </CardTitle>
            <CardDescription>{subtitle}</CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-3">
        {!enabled && (
          <Alert variant="default">
            Inferência por IA está desabilitada. Configure <code className="font-mono">Llm:Provider</code>{" "}
            e <code className="font-mono">Llm:ApiKey</code> no appsettings ou variáveis de ambiente.
          </Alert>
        )}
        {enabled && !success && error && <Alert variant="destructive">{error}</Alert>}
        {enabled && !success && !error && (
          <Alert variant="default">Sem regra inferível por este motor.</Alert>
        )}

        {rule && (
          <>
            {reasoning && (
              <div className="rounded-md border bg-violet-50/40 p-3 text-xs">
                <p className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-violet-700">
                  Por que este evento
                </p>
                <p className="text-violet-900">{reasoning}</p>
              </div>
            )}

            <div className="space-y-2">
              <Label>Nome</Label>
              <Input value={name} onChange={(e) => onNameChange(e.target.value)} />
            </div>

            <div className="space-y-2">
              <Label>Descrição</Label>
              <Input value={description} onChange={(e) => onDescriptionChange(e.target.value)} />
            </div>

            <div className="space-y-2">
              <Label>Operação</Label>
              <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm font-mono">
                {rule.operation.toUpperCase()} {rule.schema ? `${rule.schema}.` : ""}{rule.table}
              </div>
            </div>

            <div>
              <h4 className="mb-1 text-xs font-semibold">Predicate</h4>
              {rule.predicate.length === 0 ? (
                <p className="text-xs text-muted-foreground">
                  Nenhum predicate detectado — dispara em qualquer {rule.operation}.
                </p>
              ) : (
                <ul className="space-y-1 text-xs font-mono">
                  {rule.predicate.map((p, i) => (
                    <li key={i}>
                      <span className="text-muted-foreground">{p.field}</span>{" "}
                      <span className="font-bold">{p.op}</span> <span>{p.value}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div>
              <h4 className="mb-1 text-xs font-semibold">
                Companions{rule.companions.length > 0 && (
                  <span className="ml-2 font-normal text-muted-foreground">
                    (escopo: {rule.correlationScope}, janela: {rule.correlationWaitMs} ms)
                  </span>
                )}
              </h4>
              {rule.companions.length === 0 ? (
                <p className="text-xs text-muted-foreground">
                  Nenhum companion detectado — regra dispara só com o evento principal.
                </p>
              ) : (
                <ul className="space-y-1 text-xs">
                  {rule.companions.map((c) => (
                    <li key={c.eventId} className="flex items-center gap-2">
                      <span
                        className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${
                          c.required ? "bg-blue-100 text-blue-800" : "bg-amber-100 text-amber-800"
                        }`}
                      >
                        {c.required ? "required" : "optional"}
                      </span>
                      <span className="font-mono">
                        {c.operation.toUpperCase()} {c.schema ? `${c.schema}.` : ""}{c.table}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div>
              <h4 className="mb-1 text-xs font-semibold">Payload (campos $.after)</h4>
              {rule.afterFields.length === 0 ? (
                <p className="text-xs text-muted-foreground">Nenhum campo identificado.</p>
              ) : (
                <p className="text-xs font-mono">{rule.afterFields.join(", ")}</p>
              )}
              {rule.partitionKey && (
                <p className="mt-1 text-xs">
                  <span className="text-muted-foreground">partition_key:</span>{" "}
                  <span className="font-mono">{rule.partitionKey}</span>
                </p>
              )}
            </div>

            <details className="rounded-md border bg-muted/30 p-2">
              <summary className="cursor-pointer text-xs font-medium">JSON completo</summary>
              <pre className="mt-2 max-h-60 overflow-auto whitespace-pre-wrap break-words font-mono text-[11px]">
                {rule.definitionJson}
              </pre>
            </details>

            <div className="pt-2">
              <Button onClick={onSave} disabled={disabledSave || !name.trim()} className="w-full">
                {saving ? "Salvando…" : "Salvar como rascunho"}
              </Button>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function EventRow({
  ev,
  heur,
  llm,
}: {
  ev: RecordingEventItem;
  heur?: ClassifiedEvent;
  llm?: ClassifiedEvent;
}) {
  const styles = useMemo(
    () => classToStyles(heur?.classification ?? "noise", heur?.reason ?? llm?.reason),
    [heur?.classification, heur?.reason, llm?.reason],
  );
  return (
    <li className={`rounded-md border ${styles.border} ${styles.bg} p-3 text-xs`}>
      <div className="mb-1 flex items-center justify-between text-muted-foreground">
        <span className="font-mono">
          {new Date(ev.eventTimestamp).toISOString().replace("T", " ").slice(0, 23)}
        </span>
        <div className="flex items-center gap-2">
          {heur && (
            <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${classToBadge(heur.classification, heur.reason)}`}>
              H: {heur.classification}
            </span>
          )}
          {llm && (
            <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${classToBadge(llm.classification, llm.reason)}`}>
              IA: {llm.classification}
            </span>
          )}
          <span>{ev.eventType} • {Math.round(ev.durationUs / 1000)} ms</span>
        </div>
      </div>
      {(heur?.reason || llm?.reason) && (
        <p className="mb-1 text-[11px] italic text-muted-foreground">
          {heur?.reason && <>H: {heur.reason}</>}
          {heur?.reason && llm?.reason && " • "}
          {llm?.reason && <>IA: {llm.reason}</>}
        </p>
      )}
      <pre className="max-h-32 overflow-auto whitespace-pre-wrap break-words font-mono">{ev.sqlText}</pre>
    </li>
  );
}

function classToStyles(c: string, reason?: string | null) {
  switch (c) {
    case "main": return { border: "border-emerald-300", bg: "bg-emerald-50" };
    case "correlation":
      return reason?.includes("required")
        ? { border: "border-blue-300", bg: "bg-blue-50" }
        : { border: "border-amber-300", bg: "bg-amber-50" };
    default: return { border: "border-border", bg: "bg-muted/20" };
  }
}

function classToBadge(c: string, reason?: string | null) {
  switch (c) {
    case "main": return "bg-emerald-100 text-emerald-800";
    case "correlation":
      return reason?.includes("required")
        ? "bg-blue-100 text-blue-800"
        : "bg-amber-100 text-amber-800";
    default: return "bg-muted text-muted-foreground";
  }
}

function formatDuration(start: string, end: string) {
  const ms = Math.max(0, new Date(end).getTime() - new Date(start).getTime());
  const total = Math.floor(ms / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}m${s.toString().padStart(2, "0")}s`;
}

