import Link from "next/link";
import { db } from "@/lib/db";
import { isManager, requireUser } from "@/lib/auth";
import { findOpenSession } from "@/lib/cash";
import { dayRange, formatDateTime, todayIso } from "@/lib/labels";
import { formatMoney } from "@/lib/money";
import { Badge, Card, PageHeader, buttonPrimary } from "@/components/ui";

export default async function DashboardPage() {
  const user = await requireUser();
  const today = dayRange(todayIso());
  const siteIds = isManager(user) ? user.sites.map((site) => site.id) : [user.site.id];

  const [totals, openSession, recentSales] = await Promise.all([
    db.sale.groupBy({
      by: ["siteId"],
      where: { siteId: { in: siteIds }, status: "VALIDEE", createdAt: today },
      _sum: { total: true },
      _count: true,
    }),
    findOpenSession(user.id, user.site.id),
    db.sale.findMany({
      where: { siteId: user.site.id, createdAt: today },
      orderBy: { createdAt: "desc" },
      take: 8,
      include: { user: { select: { name: true } } },
    }),
  ]);

  const sites = user.sites.filter((site) => siteIds.includes(site.id));

  return (
    <>
      <PageHeader
        title={`Bonjour ${user.name}`}
        subtitle={`Site actif : ${user.site.name}`}
        action={
          user.site.type === "BOUTIQUE" ? (
            <Link href="/caisse" className={buttonPrimary}>
              {openSession ? "Reprendre la vente" : "Ouvrir la caisse"}
            </Link>
          ) : null
        }
      />

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {sites.map((site) => {
          const total = totals.find((row) => row.siteId === site.id);
          return (
            <Card key={site.id}>
              <p className="text-sm font-medium text-slate-500">{site.name}</p>
              <p className="mt-2 text-2xl font-bold tabular-nums">
                {formatMoney(total?._sum.total ?? 0)}
              </p>
              <p className="mt-1 text-sm text-slate-500">
                {total?._count ?? 0} vente(s) aujourd&apos;hui
              </p>
            </Card>
          );
        })}
      </div>

      <Card className="mt-6">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="font-semibold">Dernières ventes du jour — {user.site.name}</h2>
          <Link href="/ventes" className="text-sm font-medium text-brand-700 hover:underline">
            Tout voir
          </Link>
        </div>
        {recentSales.length === 0 ? (
          <p className="text-sm text-slate-500">Aucune vente enregistrée aujourd&apos;hui.</p>
        ) : (
          <ul className="divide-y divide-slate-100">
            {recentSales.map((sale) => (
              <li key={sale.id}>
                <Link
                  href={`/ventes/${sale.id}`}
                  className="flex items-center justify-between gap-3 py-2.5 text-sm hover:bg-slate-50"
                >
                  <span className="font-mono text-slate-700">{sale.number}</span>
                  <span className="hidden text-slate-500 sm:inline">
                    {formatDateTime(sale.createdAt)} · {sale.user.name}
                  </span>
                  <span className="flex items-center gap-2">
                    {sale.status === "ANNULEE" ? <Badge tone="red">Annulée</Badge> : null}
                    <span className="font-semibold tabular-nums">{formatMoney(sale.total)}</span>
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </>
  );
}
