import { Link } from "react-router-dom";
import {
  AlertTriangle,
  ArrowRight,
  CheckCircle2,
  Clock3,
  Database,
  GitBranch,
  MessageSquare,
  Plus,
  Radio,
} from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { listConnections, type ConnectionListItem } from "@/shared/api/connections";
import { listRabbitDestinations, type RabbitDestinationListItem } from "@/shared/api/rabbit-destinations";
import { listRecordings, type RecordingListItem } from "@/shared/api/recordings";
import { listRules, type RuleListItem } from "@/shared/api/rules";
import { cn } from "@/shared/utils/cn";

type DashboardData = {
  connections: ConnectionListItem[];
  rabbitDestinations: RabbitDestinationListItem[];
  recordings: RecordingListItem[];
  rules: RuleListItem[];
};

export function Dashboard() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    Promise.all([
      listConnections(),
      listRabbitDestinations(),
      listRecordings(),
      listRules(),
    ])
      .then(([connections, rabbitDestinations, recordings, rules]) => {
        if (!cancelled) setData({ connections, rabbitDestinations, recordings, rules });
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : "Falha ao carregar o painel.");
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const summary = useMemo(() => {
    const connections = data?.connections ?? [];
    const rabbitDestinations = data?.rabbitDestinations ?? [];
    const recordings = data?.recordings ?? [];
    const rules = data?.rules ?? [];

    return {
      activeConnections: connections.filter((c) => c.status === "active").length,
      connectionErrors: connections.filter((c) => c.status === "error").length,
      activeRabbit: rabbitDestinations.filter((d) => d.status === "active").length,
      rabbitErrors: rabbitDestinations.filter((d) => d.status === "error").length,
      activeRules: rules.filter((r) => r.status === "active").length,
      draftRules: rules.filter((r) => r.status === "draft").length,
      runningRecordings: recordings.filter((r) => r.status === "recording").length,
      completedRecordings: recordings.filter((r) => r.status === "completed").length,
      totalEvents: recordings.reduce((sum, r) => sum + r.eventCount, 0),
    };
  }, [data]);

  const recentRecordings = (data?.recordings ?? []).slice(0, 5);
  const recentRules = (data?.rules ?? []).slice(0, 5);
  const hasNoSetupData = data
    && data.connections.length === 0
    && data.recordings.length === 0
    && data.rules.length === 0;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div>
          <h2 className="text-2xl font-semibold">Painel operacional</h2>
          <p className="text-sm text-muted-foreground">
            Acompanhe conexões, gravações, regras ativas e destinos de publicação.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <ActionLink to="/connections/new" icon={<Database className="h-4 w-4" />}>
            Conexão
          </ActionLink>
          <ActionLink to="/recordings/new" icon={<Radio className="h-4 w-4" />}>
            Gravação
          </ActionLink>
          <ActionLink to="/rabbit-destinations/new" icon={<MessageSquare className="h-4 w-4" />}>
            RabbitMQ
          </ActionLink>
        </div>
      </div>

      {error && <Alert variant="destructive">{error}</Alert>}

      {data === null && !error && (
        <div className="rounded-md border bg-background px-4 py-3 text-sm text-muted-foreground">
          Carregando painel...
        </div>
      )}

      {hasNoSetupData && (
        <Card>
          <CardHeader>
            <CardTitle className="text-lg">Comece pela conexão alvo</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm text-muted-foreground">
            <p>
              Cadastre o SQL Server da aplicação que será observada. Depois grave uma operação real,
              revise a regra inferida e configure a reaction.
            </p>
            <div className="flex flex-wrap gap-2">
              <ActionLink to="/connections/new" icon={<Plus className="h-4 w-4" />}>
                Cadastrar conexão
              </ActionLink>
              <SecondaryLink to="/recordings">Ver gravações</SecondaryLink>
            </div>
          </CardContent>
        </Card>
      )}

      {data && (
        <>
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
            <MetricCard
              icon={<Database className="h-5 w-5" />}
              label="Conexões SQL"
              value={`${summary.activeConnections}/${data.connections.length}`}
              detail={summary.connectionErrors > 0
                ? `${summary.connectionErrors} com erro`
                : "ativas / cadastradas"}
              tone={summary.connectionErrors > 0 ? "danger" : "ok"}
            />
            <MetricCard
              icon={<Radio className="h-5 w-5" />}
              label="Gravações"
              value={String(summary.runningRecordings)}
              detail={`${summary.completedRecordings} concluídas, ${summary.totalEvents} eventos`}
              tone={summary.runningRecordings > 0 ? "attention" : "neutral"}
            />
            <MetricCard
              icon={<GitBranch className="h-5 w-5" />}
              label="Regras ativas"
              value={String(summary.activeRules)}
              detail={`${summary.draftRules} rascunhos pendentes`}
              tone={summary.activeRules > 0 ? "ok" : "neutral"}
            />
            <MetricCard
              icon={<MessageSquare className="h-5 w-5" />}
              label="Destinos RabbitMQ"
              value={`${summary.activeRabbit}/${data.rabbitDestinations.length}`}
              detail={summary.rabbitErrors > 0
                ? `${summary.rabbitErrors} com erro`
                : "ativos / cadastrados"}
              tone={summary.rabbitErrors > 0 ? "danger" : "neutral"}
            />
          </div>

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-[1.2fr_1fr]">
            <Card>
              <CardHeader className="flex-row items-center justify-between">
                <CardTitle className="text-lg">Gravações recentes</CardTitle>
                <SecondaryLink to="/recordings">Abrir lista</SecondaryLink>
              </CardHeader>
              <CardContent>
                {recentRecordings.length === 0 ? (
                  <EmptyState
                    title="Nenhuma gravação ainda"
                    description="Crie uma sessão para capturar DMLs reais e gerar uma regra."
                    to="/recordings/new"
                    action="Nova gravação"
                  />
                ) : (
                  <div className="divide-y">
                    {recentRecordings.map((r) => (
                      <Link
                        key={r.id}
                        to={r.status === "recording"
                          ? `/recordings/${r.id}/session`
                          : `/recordings/${r.id}/review`}
                        className="flex items-center justify-between gap-4 py-3 hover:bg-muted/40"
                      >
                        <div className="min-w-0">
                          <div className="truncate text-sm font-medium">{r.name}</div>
                          <div className="truncate text-xs text-muted-foreground">
                            {r.connectionName} · {r.eventCount} eventos
                          </div>
                        </div>
                        <StatusPill status={r.status} />
                      </Link>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="flex-row items-center justify-between">
                <CardTitle className="text-lg">Regras recentes</CardTitle>
                <SecondaryLink to="/rules">Abrir regras</SecondaryLink>
              </CardHeader>
              <CardContent>
                {recentRules.length === 0 ? (
                  <EmptyState
                    title="Nenhuma regra criada"
                    description="As regras nascem da revisão de uma gravação concluída."
                    to="/recordings"
                    action="Ver gravações"
                  />
                ) : (
                  <div className="divide-y">
                    {recentRules.map((r) => (
                      <Link
                        key={r.id}
                        to={`/rules/${r.id}`}
                        className="flex items-center justify-between gap-4 py-3 hover:bg-muted/40"
                      >
                        <div className="min-w-0">
                          <div className="truncate text-sm font-medium">{r.name}</div>
                          <div className="truncate text-xs text-muted-foreground">
                            {r.connectionName} · v{r.version}
                          </div>
                        </div>
                        <StatusPill status={r.status} />
                      </Link>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>
          </div>
        </>
      )}
    </div>
  );
}

function MetricCard({
  icon,
  label,
  value,
  detail,
  tone,
}: {
  icon: ReactNode;
  label: string;
  value: string;
  detail: string;
  tone: "ok" | "attention" | "danger" | "neutral";
}) {
  const toneClasses = {
    ok: "text-emerald-700 bg-emerald-50",
    attention: "text-amber-700 bg-amber-50",
    danger: "text-red-700 bg-red-50",
    neutral: "text-slate-700 bg-slate-50",
  };

  return (
    <Card>
      <CardContent className="flex items-start gap-4 p-4">
        <div className={cn("rounded-md p-2", toneClasses[tone])}>{icon}</div>
        <div className="min-w-0">
          <div className="text-sm text-muted-foreground">{label}</div>
          <div className="mt-1 text-2xl font-semibold leading-none">{value}</div>
          <div className="mt-2 truncate text-xs text-muted-foreground">{detail}</div>
        </div>
      </CardContent>
    </Card>
  );
}

function StatusPill({ status }: { status: string }) {
  const icon = status === "active" || status === "completed"
    ? <CheckCircle2 className="h-3.5 w-3.5" />
    : status === "recording" || status === "draft"
      ? <Clock3 className="h-3.5 w-3.5" />
      : status === "failed" || status === "error"
        ? <AlertTriangle className="h-3.5 w-3.5" />
        : null;

  const styles: Record<string, string> = {
    active: "bg-emerald-50 text-emerald-700",
    completed: "bg-emerald-50 text-emerald-700",
    recording: "bg-amber-50 text-amber-700",
    draft: "bg-slate-100 text-slate-700",
    paused: "bg-yellow-50 text-yellow-700",
    failed: "bg-red-50 text-red-700",
    discarded: "bg-muted text-muted-foreground",
    archived: "bg-muted text-muted-foreground",
  };

  return (
    <span className={cn(
      "inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-1 text-xs font-medium",
      styles[status] ?? "bg-muted text-muted-foreground",
    )}>
      {icon}
      {status}
    </span>
  );
}

function EmptyState({
  title,
  description,
  to,
  action,
}: {
  title: string;
  description: string;
  to: string;
  action: string;
}) {
  return (
    <div className="rounded-md border border-dashed px-4 py-6 text-sm">
      <div className="font-medium">{title}</div>
      <p className="mt-1 text-muted-foreground">{description}</p>
      <SecondaryLink to={to} className="mt-3">
        {action}
      </SecondaryLink>
    </div>
  );
}

function ActionLink({
  to,
  icon,
  children,
}: {
  to: string;
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <Link
      to={to}
      className="inline-flex h-9 items-center justify-center gap-2 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
    >
      {icon}
      {children}
    </Link>
  );
}

function SecondaryLink({
  to,
  children,
  className,
}: {
  to: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <Link
      to={to}
      className={cn(
        "inline-flex h-8 items-center justify-center gap-1 rounded-md border bg-background px-3 text-sm font-medium hover:bg-muted",
        className,
      )}
    >
      {children}
      <ArrowRight className="h-3.5 w-3.5" />
    </Link>
  );
}
