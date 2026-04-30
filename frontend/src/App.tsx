import { Navigate, Route, Routes } from "react-router-dom";
import { useEffect, useState } from "react";
import { SetupWizard } from "./features/setup/SetupWizard";
import { LoginPage } from "./features/auth/LoginPage";
import { Dashboard } from "./features/dashboard/Dashboard";
import { ConnectionList } from "./features/connections/ConnectionList";
import { ConnectionEditor } from "./features/connections/ConnectionEditor";
import { RecordingList } from "./features/recordings/RecordingList";
import { RecordingWizard } from "./features/recordings/RecordingWizard";
import { RecordingSession } from "./features/recordings/RecordingSession";
import { RecordingReview } from "./features/recordings/RecordingReview";
import { RuleList } from "./features/rules/RuleList";
import { RuleEditor } from "./features/rules/RuleEditor";
import { AppShell } from "./app/AppShell";
import { useAuth } from "./app/auth-provider";
import { getSetupStatus } from "./shared/api/setup";

type BootState = "loading" | "not_provisioned" | "pending_admin" | "ready";

export default function App() {
  const [boot, setBoot] = useState<BootState>("loading");

  useEffect(() => {
    getSetupStatus()
      .then((r) => setBoot(r.status as BootState))
      .catch(() => setBoot("not_provisioned"));
  }, []);

  if (boot === "loading") {
    return (
      <div className="flex min-h-screen items-center justify-center text-muted-foreground">
        Carregando...
      </div>
    );
  }

  if (boot !== "ready") {
    return (
      <Routes>
        <Route path="/setup/*" element={<SetupWizard initialStatus={boot} />} />
        <Route path="*" element={<Navigate to="/setup" replace />} />
      </Routes>
    );
  }

  return <AuthedRoutes />;
}

function AuthedRoutes() {
  const { token } = useAuth();

  if (!token) {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route path="/" element={<Dashboard />} />
        <Route path="/connections" element={<ConnectionList />} />
        <Route path="/connections/new" element={<ConnectionEditor />} />
        <Route path="/connections/:id" element={<ConnectionEditor />} />
        <Route path="/recordings" element={<RecordingList />} />
        <Route path="/recordings/new" element={<RecordingWizard />} />
        <Route path="/recordings/:id/session" element={<RecordingSession />} />
        <Route path="/recordings/:id/review" element={<RecordingReview />} />
        <Route path="/rules" element={<RuleList />} />
        <Route path="/rules/:id" element={<RuleEditor />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
