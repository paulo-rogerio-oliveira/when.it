import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { Welcome } from "./steps/Welcome";
import { DatabaseConnection } from "./steps/DatabaseConnection";
import { ProvisionSchema } from "./steps/ProvisionSchema";
import { CreateAdminUser } from "./steps/CreateAdminUser";
import { Complete } from "./steps/Complete";
import type { TestConnectionRequest } from "@/shared/api/setup";

const STEPS = [
  { id: 1, label: "Boas-vindas" },
  { id: 2, label: "Conexão" },
  { id: 3, label: "Provisionar" },
  { id: 4, label: "Administrador" },
  { id: 5, label: "Concluído" },
] as const;

type Props = {
  initialStatus: "not_provisioned" | "pending_admin" | "ready";
};

export function SetupWizard({ initialStatus }: Props) {
  const startStep = initialStatus === "pending_admin" ? 4 : 1;
  const [step, setStep] = useState<number>(startStep);
  const [connection, setConnection] = useState<TestConnectionRequest | null>(null);
  const navigate = useNavigate();

  return (
    <div className="min-h-screen bg-muted/30 py-10">
      <div className="mx-auto max-w-3xl px-4">
        <header className="mb-8 text-center">
          <h1 className="text-3xl font-semibold">DbSense — Configuração inicial</h1>
          <p className="text-muted-foreground">Siga os passos abaixo para preparar seu ambiente.</p>
        </header>

        <nav className="mb-6 flex items-center justify-between gap-2">
          {STEPS.map((s, i) => (
            <div key={s.id} className="flex flex-1 items-center gap-2">
              <div
                className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm font-medium ${
                  step >= s.id ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground"
                }`}
              >
                {s.id}
              </div>
              <span className={`text-xs ${step >= s.id ? "text-foreground" : "text-muted-foreground"}`}>
                {s.label}
              </span>
              {i < STEPS.length - 1 && <div className="mx-2 h-px flex-1 bg-border" />}
            </div>
          ))}
        </nav>

        <Card>
          <CardHeader>
            <CardTitle>{STEPS.find((s) => s.id === step)?.label}</CardTitle>
            <CardDescription>Passo {step} de {STEPS.length}</CardDescription>
          </CardHeader>
          <CardContent>
            {step === 1 && <Welcome onContinue={() => setStep(2)} />}
            {step === 2 && (
              <DatabaseConnection
                onContinue={(c) => {
                  setConnection(c);
                  setStep(3);
                }}
              />
            )}
            {step === 3 && connection && (
              <ProvisionSchema
                connection={connection}
                onBack={() => setStep(2)}
                onContinue={() => setStep(4)}
              />
            )}
            {step === 4 && <CreateAdminUser onContinue={() => setStep(5)} />}
            {step === 5 && <Complete onFinish={() => navigate("/login")} />}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
