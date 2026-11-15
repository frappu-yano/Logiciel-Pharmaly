import type { SessionSummary } from "@/lib/cash";
import { PAYMENT_LABELS, PAYMENT_METHODS } from "@/lib/labels";
import { formatMoney } from "@/lib/money";

const Row = ({ label, value, strong = false }: { label: string; value: string; strong?: boolean }) => (
  <div className={`flex justify-between gap-4 py-1.5 ${strong ? "font-semibold" : ""}`}>
    <dt className={strong ? "" : "text-slate-600"}>{label}</dt>
    <dd className="tabular-nums">{value}</dd>
  </div>
);

export const SessionReport = ({
  summary,
  openingFloat,
}: {
  summary: SessionSummary;
  openingFloat: number;
}) => (
  <dl className="text-sm">
    <Row label="Ventes validées" value={String(summary.salesCount)} />
    <Row label="Ventes annulées" value={String(summary.cancelledCount)} />
    {PAYMENT_METHODS.map((method) => (
      <Row
        key={method}
        label={`Encaissé — ${PAYMENT_LABELS[method]}`}
        value={formatMoney(summary.byMethod[method] ?? 0)}
      />
    ))}
    <Row label="Total des ventes" value={formatMoney(summary.totalSales)} strong />
    <div className="my-2 border-t border-slate-200" />
    <Row label="Fond de caisse" value={formatMoney(openingFloat)} />
    <Row label="Ventes en espèces" value={formatMoney(summary.cashSales)} />
    <Row label="Entrées d'espèces" value={formatMoney(summary.cashIn)} />
    <Row label="Sorties d'espèces" value={`− ${formatMoney(summary.cashOut)}`} />
    <Row label="Espèces attendues en caisse" value={formatMoney(summary.expectedCash)} strong />
  </dl>
);
