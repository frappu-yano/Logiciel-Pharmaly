import Link from "next/link";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { PAYMENT_LABELS, dayRange, formatDateTime, todayIso } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { Badge, Card, PageHeader, buttonSecondary, inputClass, tableClass, tdClass, thClass } from "@/components/ui";

const MAX_ROWS = 500;

export default async function VentesPage({
  searchParams,
}: {
  searchParams: Promise<{ date?: string }>;
}) {
  const user = await requireUser();
  const requested = (await searchParams).date ?? "";
  const date = /^\d{4}-\d{2}-\d{2}$/.test(requested) ? requested : todayIso();

  const sales = await db.sale.findMany({
    where: { siteId: user.site.id, createdAt: dayRange(date) },
    include: { user: { select: { name: true } }, payments: true },
    orderBy: { createdAt: "desc" },
    take: MAX_ROWS,
  });
  const validSales = sales.filter((sale) => sale.status === "VALIDEE");
  const total = validSales.reduce((sum, sale) => sum + sale.total, 0);

  return (
    <>
      <PageHeader
        title="Ventes"
        subtitle={`${user.site.name} · ${validSales.length} vente(s) · ${formatMoney(total)}`}
        action={
          <form className="flex items-end gap-2">
            <div>
              <label htmlFor="date" className="sr-only">
                Date
              </label>
              <input id="date" type="date" name="date" defaultValue={date} className={inputClass} />
            </div>
            <button type="submit" className={buttonSecondary}>
              Afficher
            </button>
          </form>
        }
      />
      <Card className="overflow-x-auto p-0">
        {sales.length === 0 ? (
          <p className="p-5 text-sm text-slate-500">Aucune vente à cette date.</p>
        ) : (
          <table className={tableClass}>
            <thead>
              <tr>
                <th className={thClass}>Ticket</th>
                <th className={thClass}>Heure</th>
                <th className={thClass}>Caissier</th>
                <th className={thClass}>Paiement</th>
                <th className={thClass}>État</th>
                <th className={`${thClass} text-right`}>Total</th>
              </tr>
            </thead>
            <tbody>
              {sales.map((sale) => (
                <tr key={sale.id} className="hover:bg-slate-50">
                  <td className={tdClass}>
                    <Link href={`/ventes/${sale.id}`} className="font-mono font-medium text-brand-700 hover:underline">
                      {sale.number}
                    </Link>
                  </td>
                  <td className={tdClass}>{formatDateTime(sale.createdAt)}</td>
                  <td className={tdClass}>{sale.user.name}</td>
                  <td className={tdClass}>
                    {sale.payments.map((payment) => PAYMENT_LABELS[payment.method]).join(", ")}
                  </td>
                  <td className={tdClass}>
                    {sale.status === "VALIDEE" ? <Badge tone="green">Validée</Badge> : <Badge tone="red">Annulée</Badge>}
                  </td>
                  <td className={`${tdClass} text-right font-semibold tabular-nums`}>{formatMoney(sale.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
