import { notFound } from "next/navigation";
import { db } from "@/lib/db";
import { isManager, requireUser } from "@/lib/auth";
import { PAYMENT_LABELS, formatDateTime } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { Badge, Card, PageHeader, buttonSecondary, tableClass, tdClass, thClass } from "@/components/ui";
import { CancelSale } from "./cancel-sale";

export default async function VentePage({ params }: { params: Promise<{ id: string }> }) {
  const user = await requireUser();
  const saleId = Number((await params).id);
  if (!Number.isInteger(saleId)) notFound();

  const sale = await db.sale.findUnique({
    where: { id: saleId },
    include: {
      items: true,
      payments: true,
      site: true,
      session: { select: { closedAt: true } },
      user: { select: { name: true } },
      cancelledBy: { select: { name: true } },
    },
  });
  if (!sale || !user.sites.some((site) => site.id === sale.siteId)) notFound();

  const canCancel = isManager(user) && sale.status === "VALIDEE" && !sale.session?.closedAt;

  return (
    <>
      <PageHeader
        title={`Vente ${sale.number}`}
        subtitle={`${sale.site.name} · ${formatDateTime(sale.createdAt)} · ${sale.user.name}`}
        action={
          <a href={`/ticket/${sale.id}?auto=1`} target="_blank" rel="noreferrer" className={buttonSecondary}>
            Imprimer le ticket
          </a>
        }
      />
      <Card className="max-w-3xl">
        <div className="mb-3 flex flex-wrap items-center gap-2 text-sm">
          {sale.status === "VALIDEE" ? <Badge tone="green">Validée</Badge> : <Badge tone="red">Annulée</Badge>}
          {sale.customerName ? <span className="text-slate-600">Client : {sale.customerName}</span> : null}
        </div>
        {sale.status === "ANNULEE" && sale.cancelledAt ? (
          <p className="mb-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            Annulée le {formatDateTime(sale.cancelledAt)} par {sale.cancelledBy?.name ?? "—"} — motif :{" "}
            {sale.cancelReason}
          </p>
        ) : null}
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <thead>
              <tr>
                <th className={thClass}>Produit</th>
                <th className={`${thClass} text-right`}>Prix unitaire</th>
                <th className={`${thClass} text-right`}>Qté</th>
                <th className={`${thClass} text-right`}>Total</th>
              </tr>
            </thead>
            <tbody>
              {sale.items.map((item) => (
                <tr key={item.id}>
                  <td className={tdClass}>{item.name}</td>
                  <td className={`${tdClass} text-right tabular-nums`}>{formatMoney(item.unitPrice)}</td>
                  <td className={`${tdClass} text-right tabular-nums`}>{item.quantity}</td>
                  <td className={`${tdClass} text-right tabular-nums`}>{formatMoney(item.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <dl className="ml-auto mt-4 max-w-xs space-y-1 text-sm">
          <div className="flex justify-between">
            <dt className="text-slate-600">Sous-total</dt>
            <dd className="tabular-nums">{formatMoney(sale.subtotal)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-slate-600">Remise</dt>
            <dd className="tabular-nums">{formatMoney(sale.discount)}</dd>
          </div>
          <div className="flex justify-between text-base font-bold">
            <dt>Total</dt>
            <dd className="tabular-nums">{formatMoney(sale.total)}</dd>
          </div>
          {sale.payments.map((payment) => (
            <div key={payment.id} className="flex justify-between">
              <dt className="text-slate-600">{PAYMENT_LABELS[payment.method]}</dt>
              <dd className="tabular-nums">{formatMoney(payment.amount)}</dd>
            </div>
          ))}
        </dl>
        {canCancel ? (
          <div className="mt-5 flex border-t border-slate-200 pt-4">
            <CancelSale saleId={sale.id} />
          </div>
        ) : null}
      </Card>
    </>
  );
}
