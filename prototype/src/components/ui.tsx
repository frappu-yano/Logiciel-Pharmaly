import type { ReactNode } from "react";

export const buttonPrimary =
  "inline-flex items-center justify-center gap-2 rounded-lg bg-brand-600 px-4 py-2.5 text-sm font-semibold text-white transition-colors hover:bg-brand-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-600 disabled:cursor-not-allowed disabled:opacity-50";

export const buttonSecondary =
  "inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-600 disabled:cursor-not-allowed disabled:opacity-50";

export const buttonDanger =
  "inline-flex items-center justify-center gap-2 rounded-lg bg-red-600 px-4 py-2.5 text-sm font-semibold text-white transition-colors hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50";

export const inputClass =
  "block w-full rounded-lg border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-600 focus:outline-none focus:ring-2 focus:ring-brand-100";

export const labelClass = "mb-1 block text-sm font-medium text-slate-700";

export const Card = ({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) => (
  <section
    className={`rounded-xl border border-slate-200 bg-white p-5 shadow-sm ${className}`}
  >
    {children}
  </section>
);

export const PageHeader = ({
  title,
  subtitle,
  action,
}: {
  title: string;
  subtitle?: string;
  action?: ReactNode;
}) => (
  <header className="mb-6 flex flex-wrap items-end justify-between gap-3">
    <div>
      <h1 className="text-2xl font-bold tracking-tight text-slate-900">{title}</h1>
      {subtitle ? <p className="mt-1 text-sm text-slate-500">{subtitle}</p> : null}
    </div>
    {action}
  </header>
);

type BadgeTone = "green" | "red" | "amber" | "slate";

const BADGE_TONES: Record<BadgeTone, string> = {
  green: "bg-brand-100 text-brand-800",
  red: "bg-red-100 text-red-700",
  amber: "bg-amber-100 text-amber-800",
  slate: "bg-slate-100 text-slate-700",
};

export const Badge = ({
  children,
  tone = "slate",
}: {
  children: ReactNode;
  tone?: BadgeTone;
}) => (
  <span
    className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold ${BADGE_TONES[tone]}`}
  >
    {children}
  </span>
);

export const FormMessage = ({
  state,
}: {
  state: { error?: string; success?: string } | undefined;
}) => {
  if (state?.error) {
    return (
      <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
        {state.error}
      </p>
    );
  }
  if (state?.success) {
    return (
      <p role="status" className="rounded-lg bg-brand-50 px-3 py-2 text-sm text-brand-800">
        {state.success}
      </p>
    );
  }
  return null;
};

export const tableClass = "w-full text-left text-sm";
export const thClass =
  "border-b border-slate-200 px-3 py-2.5 text-xs font-semibold uppercase tracking-wide text-slate-500";
export const tdClass = "border-b border-slate-100 px-3 py-2.5 align-middle";
