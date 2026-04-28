import { useState } from "react";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Alert } from "@/shared/components/ui/alert";
import { createAdmin } from "@/shared/api/setup";

type State =
  | { kind: "idle" }
  | { kind: "submitting" }
  | { kind: "done" }
  | { kind: "error"; message: string };

export function CreateAdminUser({ onContinue }: { onContinue: () => void }) {
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [state, setState] = useState<State>({ kind: "idle" });

  const canSubmit =
    username.trim().length > 0 && password.length >= 8 && password === confirm;

  const submit = async () => {
    setState({ kind: "submitting" });
    try {
      await createAdmin({ username: username.trim(), password });
      setState({ kind: "done" });
    } catch (e: unknown) {
      const msg =
        (e as { response?: { data?: { error?: string } }; message?: string })?.response?.data?.error
        ?? (e as { message?: string })?.message
        ?? "Falha ao criar administrador";
      setState({ kind: "error", message: msg });
    }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Crie a conta de administrador do DbSense. Esse usuário poderá gerenciar conexões, regras e
        criar novos usuários.
      </p>

      <div className="grid grid-cols-1 gap-4">
        <div className="space-y-2">
          <Label htmlFor="username">Usuário</Label>
          <Input id="username" value={username} onChange={(e) => setUsername(e.target.value)} />
        </div>
        <div className="space-y-2">
          <Label htmlFor="password">Senha (min. 8 caracteres)</Label>
          <Input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="confirm">Confirmar senha</Label>
          <Input
            id="confirm"
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
          {confirm && password !== confirm && (
            <p className="text-xs text-destructive">As senhas não conferem.</p>
          )}
        </div>
      </div>

      {state.kind === "error" && <Alert variant="destructive">{state.message}</Alert>}
      {state.kind === "done" && <Alert variant="success">Administrador criado com sucesso.</Alert>}

      <div className="flex justify-end">
        {state.kind === "done" ? (
          <Button onClick={onContinue}>Continuar</Button>
        ) : (
          <Button onClick={submit} disabled={!canSubmit || state.kind === "submitting"}>
            {state.kind === "submitting" ? "Criando..." : "Criar administrador"}
          </Button>
        )}
      </div>
    </div>
  );
}
