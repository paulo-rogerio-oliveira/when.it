import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  createRabbitDestination,
  getRabbitDestination,
  testRabbitDestinationAdHoc,
  updateRabbitDestination,
  type SaveRabbitDestinationInput,
} from "@/shared/api/rabbit-destinations";

type TestState =
  | { kind: "idle" }
  | { kind: "testing" }
  | { kind: "ok"; elapsedMs: number }
  | { kind: "error"; message: string };

export function RabbitDestinationEditor() {
  const { id } = useParams<{ id: string }>();
  const isEdit = Boolean(id);
  const navigate = useNavigate();

  const [name, setName] = useState("");
  const [host, setHost] = useState("");
  // 0 = "deixe o default": worker resolve para 5672 (sem TLS) ou 5671 (com TLS).
  // Mantemos como string para permitir o campo vazio sem conflitar com type=number.
  const [port, setPort] = useState<string>("");
  const [virtualHost, setVirtualHost] = useState("/");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [hasStoredPassword, setHasStoredPassword] = useState(false);
  const [clearPassword, setClearPassword] = useState(false);
  const [useTls, setUseTls] = useState(false);
  const [defaultExchange, setDefaultExchange] = useState("");
  const [test, setTest] = useState<TestState>({ kind: "idle" });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    getRabbitDestination(id)
      .then((d) => {
        setName(d.name);
        setHost(d.host);
        setPort(d.port > 0 ? String(d.port) : "");
        setVirtualHost(d.virtualHost);
        setUsername(d.username);
        setHasStoredPassword(d.hasPassword);
        setUseTls(d.useTls);
        setDefaultExchange(d.defaultExchange);
      })
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar."));
  }, [id]);

  const buildPayload = (): SaveRabbitDestinationInput => ({
    name: name.trim(),
    host: host.trim(),
    port: port.trim() === "" ? 0 : Number(port) || 0,
    virtualHost: virtualHost.trim() || "/",
    username: username.trim(),
    password: password ? password : undefined,
    useTls,
    defaultExchange: defaultExchange.trim(),
  });

  const onTest = async () => {
    setTest({ kind: "testing" });
    try {
      const r = await testRabbitDestinationAdHoc(buildPayload());
      setTest(
        r.success
          ? { kind: "ok", elapsedMs: r.elapsedMs }
          : { kind: "error", message: r.error ?? "Falha desconhecida" },
      );
    } catch (e) {
      setTest({
        kind: "error",
        message: e instanceof Error ? e.message : "Falha ao contactar a API",
      });
    }
  };

  const onSave = async () => {
    setSaving(true);
    setError(null);
    try {
      const payload = buildPayload();
      if (isEdit && id) {
        await updateRabbitDestination(id, { ...payload, clearPassword });
      } else {
        await createRabbitDestination(payload);
      }
      navigate("/rabbit-destinations");
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao salvar.");
      setError(msg);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <div>
        <h2 className="text-2xl font-semibold">
          {isEdit ? "Editar destino RabbitMQ" : "Novo destino RabbitMQ"}
        </h2>
        <p className="text-sm text-muted-foreground">
          Broker e credenciais usados pelas reactions <code>rabbit</code>.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Conexão</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="name">Nome amigável</Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="ex.: RabbitMQ produção"
            />
          </div>

          <div className="grid grid-cols-3 gap-4">
            <div className="col-span-2 space-y-2">
              <Label htmlFor="host">Host</Label>
              <Input
                id="host"
                value={host}
                onChange={(e) => setHost(e.target.value)}
                placeholder="rabbit.minha-empresa.com"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="port">Porta</Label>
              <Input
                id="port"
                type="number"
                min={0}
                max={65535}
                value={port}
                onChange={(e) => setPort(e.target.value)}
                placeholder={useTls ? "5671" : "5672"}
              />
              <p className="text-xs text-muted-foreground">
                Vazio usa o default ({useTls ? "5671 TLS" : "5672"}).
              </p>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="vhost">Virtual host</Label>
              <Input
                id="vhost"
                value={virtualHost}
                onChange={(e) => setVirtualHost(e.target.value)}
                placeholder="/"
              />
            </div>
            <div className="space-y-2 self-end">
              <label className="flex h-10 items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={useTls}
                  onChange={(e) => setUseTls(e.target.checked)}
                />
                Usar TLS (amqps)
              </label>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="username">Usuário</Label>
              <Input
                id="username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                placeholder="dbsense"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="password">
                Senha
                {isEdit && hasStoredPassword && !clearPassword && (
                  <span className="ml-2 text-xs text-muted-foreground">
                    (deixe em branco para manter a atual)
                  </span>
                )}
              </Label>
              <Input
                id="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
              {isEdit && hasStoredPassword && (
                <label className="flex items-center gap-2 text-xs text-muted-foreground">
                  <input
                    type="checkbox"
                    checked={clearPassword}
                    onChange={(e) => setClearPassword(e.target.checked)}
                  />
                  Remover senha armazenada
                </label>
              )}
            </div>
          </div>

          <div className="space-y-2">
            <Label htmlFor="default-exchange">Default exchange (opcional)</Label>
            <Input
              id="default-exchange"
              value={defaultExchange}
              onChange={(e) => setDefaultExchange(e.target.value)}
              placeholder="events"
            />
            <p className="text-xs text-muted-foreground">
              Usada quando a rule não definir <code>exchange</code> na config. Vazio = default
              exchange do broker (routing key vira nome de fila).
            </p>
          </div>

          {test.kind === "ok" && (
            <Alert variant="success">Handshake ok ({test.elapsedMs} ms).</Alert>
          )}
          {test.kind === "error" && <Alert variant="destructive">{test.message}</Alert>}
          {error && <Alert variant="destructive">{error}</Alert>}

          <div className="flex flex-wrap items-center justify-between gap-2 pt-2">
            <Button variant="outline" onClick={onTest} disabled={test.kind === "testing"}>
              {test.kind === "testing" ? "Testando…" : "Testar conexão"}
            </Button>
            <div className="flex gap-2">
              <Button variant="ghost" onClick={() => navigate("/rabbit-destinations")}>
                Cancelar
              </Button>
              <Button onClick={onSave} disabled={saving}>
                {saving ? "Salvando…" : isEdit ? "Salvar" : "Criar"}
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
