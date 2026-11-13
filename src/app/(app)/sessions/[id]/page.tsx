import { notFound } from "next/navigation";
import { db } from "@/lib/db";
import { isManager, requireUser } from "@/lib/auth";
import { summarizeSession } from "@/lib/cash";
import { formatDateTime } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { PrintButton } from "@/components/print-button";
import { SessionReport } from "@/components/session-report";
import { Badge, Card, PageHeader } from "@/components/ui";

export default async function SessionPage({ params }: { params: Promise<{ id: string }> }) {
  const user = await requireUser();
  const sessionId = Number((await params).id);
  if (!Number.isInteger(sessionId)) notFound();

  const session = await db.cashSession.findUnique({
    where: { id: sessionId },
    include: {
      register: { include: { site: true } },
      user: { select: { name: true } },
      movements: { orderBy: { createdAt: "asc" } },
    },
  });
  if (!session) notFound();

  const canView =
    session.userId === user.id ||
    (isManager(user) && user.sites.some((site) => site.id === session.register.siteId));
  if (!canView) notFound();

  const summary = await summarizeSession(session.id);
  const gap = session.countedCash !== null ? session.countedCash - summary.expectedCash : null;

  return (
    <>
      <PageHeader
        title={`Rapport de caisse — ${session.register.name}`}
        subtitle={`${session.register.site.name} · ${session.user.name}`}
        action={<PrintButton label="Imprimer le rapport" />}
      />
      <Card className="max-w-xl">
        <dl className="mb-3 text-sm">
          <div className="flex justify-between py-1.5">
            <dt className="text-slate-600">Ouverture</dt>
            <dd>{formatDateTime(session.openedAt)}</dd>
          </div>
          <div className="flex justify-between py-1.5">
            <dt className="text-slate-600">Clôture</dt>
            <dd>{session.closedAt ? formatDateTime(session.closedAt) : <Badge tone="green">Ouverte</Badge>}</dd>
          </div>
        </dl>
        <div className="border-t border-slate-200 pt-3">
          <SessionReport summary={summary} openingFloat={session.openingFloat} />
        </div>
        {session.countedCash !== null && gap !== null ? (
          <dl className="mt-3 border-t border-slate-200 pt-3 text-sm">
            <div className="flex justify-between py-1.5">
              <dt className="text-slate-600">Espèces comptées</dt>
              <dd className="tabular-nums">{formatMoney(session.countedCash)}</dd>
            </div>
            <div className="flex justify-between py-1.5 font-semibold">
              <dt>Écart</dt>
              <dd className={`tabular-nums ${gap === 0 ? "text-brand-700" : "text-red-600"}`}>
                {formatMoney(gap)}
              </dd>
            </div>
          </dl>
        ) : null}
        {session.note ? <p className="mt-3 text-sm text-slate-600">Remarque : {session.note}</p> : null}
        {session.movements.length > 0 ? (
          <div className="mt-4 border-t border-slate-200 pt-3">
            <h2 className="mb-2 text-sm font-semibold">Mouvements d&apos;espèces</h2>
            <ul className="space-y-1 text-sm">
              {session.movements.map((movement) => (
                <li key={movement.id} className="flex justify-between gap-3">
                  <span className="text-slate-600">
                    {movement.type === "ENTREE" ? "Entrée" : "Sortie"} — {movement.reason}
                  </span>
                  <span className="tabular-nums">{formatMoney(movement.amount)}</span>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </Card>
    </>
  );
}
