import Link from "next/link";
import { db } from "@/lib/db";
import { MANAGER_ROLES, requireUser } from "@/lib/auth";
import { formatDateTime } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { Badge, Card, PageHeader, tableClass, tdClass, thClass } from "@/components/ui";

const PAGE_SIZE = 100;

export default async function SessionsPage() {
  const user = await requireUser(MANAGER_ROLES);
  const sessions = await db.cashSession.findMany({
    where: { register: { siteId: user.site.id } },
    include: { register: true, user: { select: { name: true } } },
    orderBy: { openedAt: "desc" },
    take: PAGE_SIZE,
  });

  return (
    <>
      <PageHeader title="Sessions de caisse" subtitle={user.site.name} />
      <Card className="overflow-x-auto p-0">
        {sessions.length === 0 ? (
          <p className="p-5 text-sm text-slate-500">Aucune session de caisse sur ce site.</p>
        ) : (
          <table className={tableClass}>
            <thead>
              <tr>
                <th className={thClass}>Caisse</th>
                <th className={thClass}>Caissier</th>
                <th className={thClass}>Ouverture</th>
                <th className={thClass}>Clôture</th>
                <th className={`${thClass} text-right`}>Attendu</th>
                <th className={`${thClass} text-right`}>Compté</th>
                <th className={`${thClass} text-right`}>Écart</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((session) => {
                const gap =
                  session.countedCash !== null && session.expectedCash !== null
                    ? session.countedCash - session.expectedCash
                    : null;
                return (
                  <tr key={session.id} className="hover:bg-slate-50">
                    <td className={tdClass}>
                      <Link href={`/sessions/${session.id}`} className="font-medium text-brand-700 hover:underline">
                        {session.register.name}
                      </Link>
                    </td>
                    <td className={tdClass}>{session.user.name}</td>
                    <td className={tdClass}>{formatDateTime(session.openedAt)}</td>
                    <td className={tdClass}>
                      {session.closedAt ? formatDateTime(session.closedAt) : <Badge tone="green">Ouverte</Badge>}
                    </td>
                    <td className={`${tdClass} text-right tabular-nums`}>
                      {session.expectedCash !== null ? formatMoney(session.expectedCash) : "—"}
                    </td>
                    <td className={`${tdClass} text-right tabular-nums`}>
                      {session.countedCash !== null ? formatMoney(session.countedCash) : "—"}
                    </td>
                    <td className={`${tdClass} text-right tabular-nums`}>
                      {gap === null ? "—" : gap === 0 ? (
                        <Badge tone="green">0,00</Badge>
                      ) : (
                        <Badge tone="red">{formatMoney(gap)}</Badge>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
