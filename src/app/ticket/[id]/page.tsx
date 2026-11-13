import { notFound } from "next/navigation";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { PAYMENT_LABELS, formatDateTime } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { AutoPrint } from "./auto-print";

const TICKET_STYLES = `
  @page { size: 80mm auto; margin: 3mm; }
  body { background: #ffffff; }
`;

export default async function TicketPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ auto?: string }>;
}) {
  const user = await requireUser();
  const saleId = Number((await params).id);
  if (!Number.isInteger(saleId)) notFound();

  const sale = await db.sale.findUnique({
    where: { id: saleId },
    include: { items: true, payments: true, site: true, user: { select: { name: true } } },
  });
  if (!sale || !user.sites.some((site) => site.id === sale.siteId)) notFound();

  const payment = sale.payments[0];
  const change = payment?.received != null ? payment.received - sale.total : 0;

  return (
    <main className="mx-auto w-[72mm] py-2 font-mono text-[12px] leading-snug text-black">
      <style>{TICKET_STYLES}</style>
      {(await searchParams).auto === "1" ? <AutoPrint /> : null}

      <header className="text-center">
        <p className="text-base font-bold">Pharmaly</p>
        <p>{sale.site.name}</p>
        {sale.site.address ? <p>{sale.site.address}</p> : null}
        {sale.site.phone ? <p>Tél. : {sale.site.phone}</p> : null}
      </header>

      <div className="my-2 border-t border-dashed border-black" />
      <p>Ticket : {sale.number}</p>
      <p>Date : {formatDateTime(sale.createdAt)}</p>
      <p>Caissier : {sale.user.name}</p>
      {sale.customerName ? <p>Client : {sale.customerName}</p> : null}
      {sale.status === "ANNULEE" ? <p className="font-bold">*** VENTE ANNULÉE ***</p> : null}
      <div className="my-2 border-t border-dashed border-black" />

      <ul className="space-y-1">
        {sale.items.map((item) => (
          <li key={item.id}>
            <p>{item.name}</p>
            <p className="flex justify-between">
              <span>
                {item.quantity} x {formatMoney(item.unitPrice)}
              </span>
              <span>{formatMoney(item.total)}</span>
            </p>
          </li>
        ))}
      </ul>

      <div className="my-2 border-t border-dashed border-black" />
      {sale.discount > 0 ? (
        <>
          <p className="flex justify-between">
            <span>Sous-total</span>
            <span>{formatMoney(sale.subtotal)}</span>
          </p>
          <p className="flex justify-between">
            <span>Remise</span>
            <span>- {formatMoney(sale.discount)}</span>
          </p>
        </>
      ) : null}
      <p className="flex justify-between text-sm font-bold">
        <span>TOTAL</span>
        <span>{formatMoney(sale.total)}</span>
      </p>
      {payment ? (
        <p className="flex justify-between">
          <span>Paiement</span>
          <span>{PAYMENT_LABELS[payment.method]}</span>
        </p>
      ) : null}
      {payment?.received != null ? (
        <>
          <p className="flex justify-between">
            <span>Reçu</span>
            <span>{formatMoney(payment.received)}</span>
          </p>
          <p className="flex justify-between">
            <span>Rendu</span>
            <span>{formatMoney(change)}</span>
          </p>
        </>
      ) : null}

      <div className="my-2 border-t border-dashed border-black" />
      <p className="text-center">Merci de votre visite</p>
    </main>
  );
}
