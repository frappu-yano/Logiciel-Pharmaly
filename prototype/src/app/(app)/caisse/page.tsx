import Link from "next/link";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { findOpenSession } from "@/lib/cash";
import { openSession } from "@/actions/cash";
import { ActionForm } from "@/components/action-form";
import { Card, PageHeader, buttonSecondary, inputClass, labelClass } from "@/components/ui";
import { PosScreen } from "./pos-screen";

export default async function CaissePage() {
  const user = await requireUser();

  if (user.site.type !== "BOUTIQUE") {
    return (
      <>
        <PageHeader title="Caisse" />
        <Card>
          <p className="text-sm text-slate-600">
            Le site « {user.site.name} » est une boutique en ligne : il n&apos;a pas de
            caisse. Choisissez une boutique physique dans le sélecteur en haut de
            l&apos;écran.
          </p>
        </Card>
      </>
    );
  }

  const session = await findOpenSession(user.id, user.site.id);

  if (!session) {
    const registers = await db.register.findMany({
      where: { siteId: user.site.id, active: true },
      include: {
        sessions: {
          where: { closedAt: null },
          include: { user: { select: { name: true } } },
        },
      },
      orderBy: { id: "asc" },
    });

    return (
      <>
        <PageHeader
          title="Ouvrir la caisse"
          subtitle="Comptez le fond de caisse avant de commencer à vendre."
        />
        <Card className="max-w-md">
          {registers.length === 0 ? (
            <p className="text-sm text-slate-600">
              Aucune caisse n&apos;est configurée pour ce site. Un administrateur doit en
              créer une dans les paramètres.
            </p>
          ) : (
            <ActionForm action={openSession} submitLabel="Ouvrir la caisse">
              <div>
                <label htmlFor="registerId" className={labelClass}>
                  Caisse
                </label>
                <select id="registerId" name="registerId" className={inputClass}>
                  {registers.map((register) => {
                    const busy = register.sessions[0];
                    return (
                      <option key={register.id} value={register.id} disabled={Boolean(busy)}>
                        {register.name}
                        {busy ? ` — ouverte par ${busy.user.name}` : ""}
                      </option>
                    );
                  })}
                </select>
              </div>
              <div>
                <label htmlFor="openingFloat" className={labelClass}>
                  Fond de caisse (DA)
                </label>
                <input
                  id="openingFloat"
                  name="openingFloat"
                  inputMode="decimal"
                  defaultValue="0"
                  required
                  className={inputClass}
                />
              </div>
            </ActionForm>
          )}
        </Card>
      </>
    );
  }

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-bold tracking-tight">{session.register.name}</h1>
          <p className="text-sm text-slate-500">
            {user.site.name} · caissier : {user.name}
          </p>
        </div>
        <Link href="/caisse/cloture" className={buttonSecondary}>
          Mouvements et clôture
        </Link>
      </div>
      <PosScreen />
    </>
  );
}
