import Link from "next/link";
import { redirect } from "next/navigation";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { findOpenSession, summarizeSession } from "@/lib/cash";
import { addCashMovement, closeSession } from "@/actions/cash";
import { formatDateTime } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { ActionForm } from "@/components/action-form";
import { SessionReport } from "@/components/session-report";
import { Badge, Card, PageHeader, buttonSecondary, inputClass, labelClass } from "@/components/ui";

export default async function CloturePage() {
  const user = await requireUser();
  const session = await findOpenSession(user.id, user.site.id);
  if (!session) redirect("/caisse");

  const [summary, movements] = await Promise.all([
    summarizeSession(session.id),
    db.cashMovement.findMany({
      where: { sessionId: session.id },
      orderBy: { createdAt: "desc" },
    }),
  ]);

  return (
    <>
      <PageHeader
        title={`${session.register.name} — mouvements et clôture`}
        subtitle={`Ouverte le ${formatDateTime(session.openedAt)}`}
        action={
          <Link href="/caisse" className={buttonSecondary}>
            Retour à la vente
          </Link>
        }
      />
      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <h2 className="mb-3 font-semibold">Situation de la caisse</h2>
          <SessionReport summary={summary} openingFloat={session.openingFloat} />
        </Card>

        <div className="space-y-4">
          <Card>
            <h2 className="mb-3 font-semibold">Entrée ou sortie d&apos;espèces</h2>
            <ActionForm action={addCashMovement} submitLabel="Enregistrer le mouvement" resetOnSubmit>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label htmlFor="type" className={labelClass}>
                    Type
                  </label>
                  <select id="type" name="type" defaultValue="SORTIE" className={inputClass}>
                    <option value="SORTIE">Sortie</option>
                    <option value="ENTREE">Entrée</option>
                  </select>
                </div>
                <div>
                  <label htmlFor="amount" className={labelClass}>
                    Montant (DA)
                  </label>
                  <input id="amount" name="amount" inputMode="decimal" required className={inputClass} />
                </div>
              </div>
              <div>
                <label htmlFor="reason" className={labelClass}>
                  Motif
                </label>
                <input
                  id="reason"
                  name="reason"
                  required
                  maxLength={200}
                  placeholder="Ex. : dépôt en banque, achat de fournitures"
                  className={inputClass}
                />
              </div>
            </ActionForm>
            {movements.length > 0 ? (
              <ul className="mt-4 divide-y divide-slate-100 border-t border-slate-200 text-sm">
                {movements.map((movement) => (
                  <li key={movement.id} className="flex items-center justify-between gap-3 py-2">
                    <span>
                      <Badge tone={movement.type === "ENTREE" ? "green" : "amber"}>
                        {movement.type === "ENTREE" ? "Entrée" : "Sortie"}
                      </Badge>
                      <span className="ml-2 text-slate-700">{movement.reason}</span>
                    </span>
                    <span className="font-semibold tabular-nums">{formatMoney(movement.amount)}</span>
                  </li>
                ))}
              </ul>
            ) : null}
          </Card>

          <Card>
            <h2 className="mb-1 font-semibold">Clôturer la caisse</h2>
            <p className="mb-3 text-sm text-slate-500">
              Comptez les espèces du tiroir. L&apos;écart avec le montant attendu sera
              enregistré.
            </p>
            <ActionForm action={closeSession} submitLabel="Clôturer la caisse">
              <div>
                <label htmlFor="countedCash" className={labelClass}>
                  Espèces comptées (DA)
                </label>
                <input id="countedCash" name="countedCash" inputMode="decimal" required className={inputClass} />
              </div>
              <div>
                <label htmlFor="note" className={labelClass}>
                  Remarque (facultatif)
                </label>
                <input id="note" name="note" maxLength={500} className={inputClass} />
              </div>
            </ActionForm>
          </Card>
        </div>
      </div>
    </>
  );
}
