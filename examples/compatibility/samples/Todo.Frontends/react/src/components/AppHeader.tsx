import { useMvvmSnapshot } from "@runic-artifex/mvvm-react";

export interface AppHeaderProps {
  readonly title: string;
  readonly subtitle: string;
}

export function AppHeader({ title, subtitle }: AppHeaderProps) {
  const snapshot = useMvvmSnapshot();
  return (
    <header className="mb-4">
      <div className="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div>
          <span className="framework-badge badge text-bg-primary mb-2">React</span>
          <h1 className="display-6 fw-semibold mb-1">{title}</h1>
          <p className="text-secondary mb-0">{subtitle}</p>
        </div>
        <span className={`badge ${snapshot.synchronized ? "text-bg-success" : "text-bg-secondary"}`}>
          {snapshot.synchronized ? `Connected · r${snapshot.revision}` : snapshot.phase}
        </span>
      </div>
    </header>
  );
}
