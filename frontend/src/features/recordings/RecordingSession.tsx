import { useEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  discardRecording,
  getRecording,
  listRecordingEvents,
  stopRecording,
  type RecordingDetail,
  type RecordingEventItem,
} from "@/shared/api/recordings";
import { ParsedPayloadBlock } from "./RecordingReview";

const POLL_MS = 1500;

export function RecordingSession() {
  const { id = "" } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [rec, setRec] = useState<RecordingDetail | null>(null);
  const [events, setEvents] = useState<RecordingEventItem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [discarding, setDiscarding] = useState(false);
  const cursorRef = useRef<number | undefined>(undefined);
  const eventsBoxRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      try {
        const detail = await getRecording(id);
        if (cancelled) return;
        setRec(detail);
        const page = await listRecordingEvents(id, cursorRef.current, 200);
        if (cancelled) return;
        if (page.items.length > 0) {
          setEvents((prev) => [...prev, ...page.items]);
          cursorRef.current = page.nextCursor ?? cursorRef.current;
          requestAnimationFrame(() => {
            const el = eventsBoxRef.current;
            if (el) el.scrollTop = el.scrollHeight;
          });
        }
        if (detail.status === "recording" && !cancelled) {
          setTimeout(tick, POLL_MS);
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : "Falha ao atualizar.");
      }
    };
    tick();
    return () => {
      cancelled = true;
    };
  }, [id]);

  const onStop = async () => {
    setStopping(true);
    try {
      const detail = await stopRecording(id);
      setRec(detail);
      navigate(`/recordings/${id}/review`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao parar gravação.");
    } finally {
      setStopping(false);
    }
  };

  const onDiscard = async () => {
    if (!window.confirm("Descartar esta gravação?")) return;
    setDiscarding(true);
    try {
      await discardRecording(id);
      navigate("/recordings");
    } finally {
      setDiscarding(false);
    }
  };

  if (!rec && !error) return <p className="text-sm text-muted-foreground">Carregando…</p>;
  if (error) return <Alert variant="destructive">{error}</Alert>;
  if (!rec) return null;

  const isRecording = rec.status === "recording";

  return (
    <div className="space-y-4">
      <SessionHeader rec={rec} onStop={onStop} stopping={stopping} />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_2fr]">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Como gravar</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <ol className="list-decimal space-y-2 pl-4 text-muted-foreground">
              <li>Mantenha esta aba aberta.</li>
              <li>
                Vá ao seu sistema legado e <strong>execute a operação</strong> que você quer
                transformar em evento.
              </li>
              <li>Volte aqui e clique em <strong>Parar gravação</strong>.</li>
            </ol>
            <div className="rounded-md border bg-muted/30 p-3 text-xs">
              <p className="font-medium">Conexão</p>
              <p className="text-muted-foreground">{rec.connectionName}</p>
              {(rec.filterHostName || rec.filterAppName || rec.filterLoginName) && (
                <>
                  <p className="mt-2 font-medium">Filtros</p>
                  <ul className="text-muted-foreground">
                    {rec.filterHostName && <li>host_name = {rec.filterHostName}</li>}
                    {rec.filterAppName && <li>app_name = {rec.filterAppName}</li>}
                    {rec.filterLoginName && <li>login_name = {rec.filterLoginName}</li>}
                  </ul>
                </>
              )}
            </div>
            {!isRecording && (
              <div className="flex flex-col gap-2 pt-2">
                <Button variant="outline" onClick={() => navigate("/recordings/new")}>
                  Nova gravação
                </Button>
                <Button variant="ghost" onClick={onDiscard} disabled={discarding}>
                  Descartar
                </Button>
              </div>
            )}
          </CardContent>
        </Card>

        <Card className="flex h-[60vh] flex-col">
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-base">Eventos capturados</CardTitle>
            <span className="text-xs text-muted-foreground">{events.length} eventos</span>
          </CardHeader>
          <CardContent className="flex-1 overflow-hidden p-0">
            <div ref={eventsBoxRef} className="h-full overflow-y-auto px-4 pb-4">
              {events.length === 0 ? (
                <EmptyState isRecording={isRecording} />
              ) : (
                <ul className="space-y-2">
                  {events.map((e) => (
                    <EventRow key={e.id} ev={e} />
                  ))}
                </ul>
              )}
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function SessionHeader({
  rec,
  onStop,
  stopping,
}: {
  rec: RecordingDetail;
  onStop: () => void;
  stopping: boolean;
}) {
  const elapsed = useElapsed(rec.startedAt, rec.stoppedAt);
  const isRecording = rec.status === "recording";
  return (
    <div
      className={`flex items-center justify-between rounded-md border p-4 ${
        isRecording ? "border-red-300 bg-red-50" : "border-border bg-background"
      }`}
    >
      <div>
        <div className="flex items-center gap-3">
          {isRecording && (
            <span className="inline-flex h-2.5 w-2.5 animate-pulse rounded-full bg-red-500" />
          )}
          <h2 className="text-lg font-semibold">
            {isRecording ? "GRAVANDO" : rec.status.toUpperCase()} — {rec.name}
          </h2>
        </div>
        <p className="text-xs text-muted-foreground">
          {rec.connectionName} • iniciada em {new Date(rec.startedAt).toLocaleString()}
          {rec.stoppedAt && ` • encerrada em ${new Date(rec.stoppedAt).toLocaleString()}`}
        </p>
      </div>
      <div className="flex items-center gap-4">
        <span className="font-mono text-lg">{elapsed}</span>
        {isRecording ? (
          <Button variant="destructive" onClick={onStop} disabled={stopping}>
            {stopping ? "Parando…" : "Parar gravação"}
          </Button>
        ) : (
          <span className="text-sm font-medium">Total: {rec.eventCount} eventos</span>
        )}
      </div>
    </div>
  );
}

function EmptyState({ isRecording }: { isRecording: boolean }) {
  return (
    <div className="grid h-full place-items-center text-center text-sm text-muted-foreground">
      {isRecording ? (
        <div className="space-y-1">
          <p>Aguardando atividade no SQL Server alvo…</p>
          <p className="text-xs">
            O coletor XEvents será ligado nas próximas fatias. Por enquanto a gravação está
            registrada e os comandos do worker enfileirados.
          </p>
        </div>
      ) : (
        <p>Nenhum evento capturado nesta gravação.</p>
      )}
    </div>
  );
}

function EventRow({ ev }: { ev: RecordingEventItem }) {
  return (
    <li className="rounded-md border bg-background p-3 text-xs">
      <div className="flex items-center justify-between text-muted-foreground">
        <span className="font-mono">
          {new Date(ev.eventTimestamp).toISOString().replace("T", " ").slice(0, 23)}
        </span>
        <span>
          {ev.eventType} • sid={ev.sessionId} • {Math.round(ev.durationUs / 1000)} ms
        </span>
      </div>
      {ev.objectName && <p className="mt-1 font-medium">{ev.objectName}</p>}
      <pre className="mt-1 max-h-32 overflow-auto whitespace-pre-wrap break-words font-mono">
        {ev.sqlText}
      </pre>
      <ParsedPayloadBlock payload={ev.parsedPayload} />
    </li>
  );
}

function useElapsed(startedAt: string, stoppedAt: string | null) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (stoppedAt) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [stoppedAt]);
  const start = new Date(startedAt).getTime();
  const end = stoppedAt ? new Date(stoppedAt).getTime() : now;
  const ms = Math.max(0, end - start);
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}
