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
  parseRecordingPayload,
  type RecordingDetail,
  type RecordingEventItem,
} from "@/shared/api/recordings";
import {
  createRule,
  inferRule,
  type ClassifiedEvent,
  type InferRuleResponse,
  type InferredCompanion,
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
  // Companions desabilitados por engine (eventId desmarcado). Default: todos
  // ligados — segue o que o motor inferiu. Quando salva, removemos do
  // definition.correlation.companions[] os que estão aqui.
  const [disabledCompanionIds, setDisabledCompanionIds] = useState<Record<Engine, Set<number>>>({
    heuristic: new Set<number>(),
    llm: new Set<number>(),
  });

  const toggleCompanion = (engine: Engine, eventId: number) => {
    setDisabledCompanionIds((prev) => {
      const next = new Set(prev[engine]);
      if (next.has(eventId)) next.delete(eventId);
      else next.add(eventId);
      return { ...prev, [engine]: next };
    });
  };

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

    const disabled = disabledCompanionIds[engine];
    const enabledCompanions = rule.companions.filter((c) => !disabled.has(c.eventId));

    let definition = rule.definitionJson;
    try {
      definition = applyCompanionSelection(definition, rule.companions, enabledCompanions);
      definition = mergeReactionIntoDefinition(definition, reaction);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : "Falha ao montar definition.");
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
          disabledCompanionIds={disabledCompanionIds.heuristic}
          onToggleCompanion={(eventId) => toggleCompanion("heuristic", eventId)}
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
            disabledCompanionIds={disabledCompanionIds.llm}
            onToggleCompanion={(eventId) => toggleCompanion("llm", eventId)}
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
  disabledCompanionIds, onToggleCompanion,
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
  disabledCompanionIds: Set<number>;
  onToggleCompanion: (eventId: number) => void;
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
                <>
                  <p className="mb-1 text-[11px] text-muted-foreground">
                    Clique para alternar inclusão. Os desmarcados saem do
                    {" "}<code className="font-mono">correlation.companions</code> da regra ao salvar.
                  </p>
                  <ul className="space-y-1 text-xs">
                    {rule.companions.map((c) => {
                      const off = disabledCompanionIds.has(c.eventId);
                      return (
                        <li key={c.eventId}>
                          <button
                            type="button"
                            onClick={() => onToggleCompanion(c.eventId)}
                            className={`flex w-full items-center gap-2 rounded-md border px-2 py-1 text-left transition-colors ${
                              off
                                ? "border-slate-200 bg-slate-50 text-muted-foreground line-through opacity-70"
                                : "border-border bg-background hover:bg-muted"
                            }`}
                          >
                            <input
                              type="checkbox"
                              checked={!off}
                              readOnly
                              tabIndex={-1}
                              className="pointer-events-none"
                            />
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
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                </>
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
      <ParsedPayloadBlock payload={ev.parsedPayload} />
    </li>
  );
}

// Mostra os DMLs parseados com schema/table e valores resolvidos. Esses valores são
// exatamente o que vai estar disponível como $.after.<coluna> nos placeholders das reactions.
export function ParsedPayloadBlock({ payload }: { payload: string | null }) {
  const parsed = parseRecordingPayload(payload);
  if (!parsed || parsed.statements.length === 0) return null;

  return (
    <div className="mt-2 space-y-2">
      {parsed.statements.map((s, idx) => {
        const columnsWithValues = Object.keys(s.values);
        const fqtn = `${s.schema ? s.schema + "." : ""}${s.table}`;
        return (
          <div key={idx} className="rounded-md border border-slate-200 bg-white/70 p-2">
            <div className="mb-1 flex items-center gap-2 text-[10px] uppercase tracking-wide text-slate-500">
              <span className="rounded bg-slate-100 px-1.5 py-0.5 font-semibold text-slate-700">
                {s.operation}
              </span>
              <span className="font-mono">{fqtn}</span>
            </div>
            {columnsWithValues.length > 0 && (
              <table className="w-full text-[11px]">
                <tbody>
                  {columnsWithValues.map((col) => (
                    <tr key={col} className="border-t border-slate-100 first:border-t-0">
                      <td className="py-0.5 pr-3 font-mono text-slate-600">$.after.{col}</td>
                      <td className="py-0.5 font-mono text-slate-900">
                        {s.values[col] === null
                          ? <span className="text-slate-400">NULL</span>
                          : s.values[col]}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {s.where.length > 0 && (
              <div className="mt-1 border-t border-slate-100 pt-1 text-[11px]">
                <span className="text-slate-500">WHERE: </span>
                <span className="font-mono">
                  {s.where.map((w, i) => (
                    <span key={i}>
                      {i > 0 && <span className="text-slate-400"> AND </span>}
                      {w.column} {w.op} {w.value}
                    </span>
                  ))}
                </span>
              </div>
            )}
          </div>
        );
      })}
    </div>
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

// Reescreve correlation.companions[] no definition JSON pra refletir a escolha
// do usuário no review: mantém só os que o usuário deixou marcados (matching
// por operation+schema+table contra a lista inferida).
//
// Se nenhum companion sobra, ajusta correlation.scope=none — replica a regra
// do InferenceService.BuildPreview que escolhe scope baseado em ter ou não
// companions. Sem isso, a rule iria ficar com scope time_window mas array
// vazio, o que confunde o RuleEngine na hora de matchear.
function applyCompanionSelection(
  definitionJson: string,
  inferred: InferredCompanion[],
  enabled: InferredCompanion[],
): string {
  const def = JSON.parse(definitionJson);
  if (!def?.correlation || !Array.isArray(def.correlation.companions)) return definitionJson;

  const enabledKeys = new Set(
    enabled.map((c) => companionKey(c.operation, c.schema, c.table)),
  );

  // Filtra preservando os campos serializados pelo backend (event_kind, required, etc.)
  const filtered = (def.correlation.companions as Array<{
    operation?: string;
    schema?: string | null;
    table?: string;
    [k: string]: unknown;
  }>).filter((c) =>
    enabledKeys.has(companionKey(c.operation ?? "", c.schema ?? null, c.table ?? "")),
  );

  def.correlation.companions = filtered;
  if (filtered.length === 0 && inferred.length > 0) {
    // Inferiu N, usuário tirou todos → vira regra sem correlação.
    def.correlation.scope = "none";
  }
  return JSON.stringify(def, null, 2);
}

function companionKey(operation: string, schema: string | null, table: string) {
  return `${operation.toLowerCase()}|${(schema ?? "dbo").toLowerCase()}|${table.toLowerCase()}`;
}

function formatDuration(start: string, end: string) {
  const ms = Math.max(0, new Date(end).getTime() - new Date(start).getTime());
  const total = Math.floor(ms / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}m${s.toString().padStart(2, "0")}s`;
}

