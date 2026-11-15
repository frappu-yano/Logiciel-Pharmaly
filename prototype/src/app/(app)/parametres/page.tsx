import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { saveRegister, saveSite, saveUser } from "@/actions/admin";
import { ROLE_LABELS, SITE_TYPE_LABELS } from "@/lib/labels";
import { ActionForm } from "@/components/action-form";
import { Badge, Card, PageHeader, inputClass, labelClass } from "@/components/ui";

type SiteValues = {
  id: number;
  name: string;
  code: string;
  type: "BOUTIQUE" | "EN_LIGNE";
  address: string | null;
  phone: string | null;
};

const SiteFields = ({ site }: { site?: SiteValues }) => {
  const key = site?.id ?? "new";
  return (
    <>
      {site ? <input type="hidden" name="id" value={site.id} /> : null}
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="sm:col-span-2">
          <label htmlFor={`site-name-${key}`} className={labelClass}>
            Nom
          </label>
          <input id={`site-name-${key}`} name="name" defaultValue={site?.name} required className={inputClass} />
        </div>
        <div>
          <label htmlFor={`site-code-${key}`} className={labelClass}>
            Code (préfixe des tickets)
          </label>
          <input id={`site-code-${key}`} name="code" defaultValue={site?.code} required maxLength={6} className={`${inputClass} uppercase`} />
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <div>
          <label htmlFor={`site-type-${key}`} className={labelClass}>
            Type
          </label>
          <select id={`site-type-${key}`} name="type" defaultValue={site?.type ?? "BOUTIQUE"} className={inputClass}>
            <option value="BOUTIQUE">Boutique</option>
            <option value="EN_LIGNE">En ligne</option>
          </select>
        </div>
        <div>
          <label htmlFor={`site-address-${key}`} className={labelClass}>
            Adresse
          </label>
          <input id={`site-address-${key}`} name="address" defaultValue={site?.address ?? ""} className={inputClass} />
        </div>
        <div>
          <label htmlFor={`site-phone-${key}`} className={labelClass}>
            Téléphone
          </label>
          <input id={`site-phone-${key}`} name="phone" defaultValue={site?.phone ?? ""} className={inputClass} />
        </div>
      </div>
    </>
  );
};

type UserValues = {
  id: number;
  name: string;
  username: string;
  role: "ADMIN" | "GERANT" | "CAISSIER";
  active: boolean;
  sites: { id: number }[];
};

const UserFields = ({
  user,
  sites,
}: {
  user?: UserValues;
  sites: { id: number; name: string }[];
}) => {
  const key = user?.id ?? "new";
  return (
    <>
      {user ? <input type="hidden" name="id" value={user.id} /> : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <div>
          <label htmlFor={`user-name-${key}`} className={labelClass}>
            Nom complet
          </label>
          <input id={`user-name-${key}`} name="name" defaultValue={user?.name} required className={inputClass} />
        </div>
        <div>
          <label htmlFor={`user-username-${key}`} className={labelClass}>
            Identifiant
          </label>
          <input
            id={`user-username-${key}`}
            name="username"
            defaultValue={user?.username}
            required
            autoCapitalize="none"
            autoComplete="off"
            className={inputClass}
          />
        </div>
        <div>
          <label htmlFor={`user-password-${key}`} className={labelClass}>
            {user ? "Nouveau mot de passe (laisser vide pour ne pas changer)" : "Mot de passe (8 caractères minimum)"}
          </label>
          <input
            id={`user-password-${key}`}
            name="password"
            type="password"
            autoComplete="new-password"
            required={!user}
            className={inputClass}
          />
        </div>
        <div>
          <label htmlFor={`user-role-${key}`} className={labelClass}>
            Rôle
          </label>
          <select id={`user-role-${key}`} name="role" defaultValue={user?.role ?? "CAISSIER"} className={inputClass}>
            <option value="CAISSIER">Caissier</option>
            <option value="GERANT">Gérant</option>
            <option value="ADMIN">Administrateur</option>
          </select>
        </div>
      </div>
      <fieldset>
        <legend className={labelClass}>Sites autorisés (un administrateur accède à tous les sites)</legend>
        <div className="flex flex-wrap gap-x-5 gap-y-2">
          {sites.map((site) => (
            <label key={site.id} className="flex items-center gap-2 text-sm text-slate-700">
              <input
                type="checkbox"
                name="siteIds"
                value={site.id}
                defaultChecked={user?.sites.some((assigned) => assigned.id === site.id) ?? false}
                className="h-4 w-4 accent-brand-600"
              />
              {site.name}
            </label>
          ))}
        </div>
      </fieldset>
      <label className="flex items-center gap-2 text-sm text-slate-700">
        <input type="checkbox" name="active" defaultChecked={user?.active ?? true} className="h-4 w-4 accent-brand-600" />
        Compte actif
      </label>
    </>
  );
};

const summaryClass =
  "flex cursor-pointer flex-wrap items-center justify-between gap-2 rounded-lg px-3 py-2.5 text-sm hover:bg-slate-50";

export default async function ParametresPage() {
  await requireUser(["ADMIN"]);

  const [sites, registers, users] = await Promise.all([
    db.site.findMany({ orderBy: { id: "asc" } }),
    db.register.findMany({ include: { site: true }, orderBy: { id: "asc" } }),
    db.user.findMany({ include: { sites: { select: { id: true } } }, orderBy: { name: "asc" } }),
  ]);
  const boutiques = sites.filter((site) => site.type === "BOUTIQUE");

  return (
    <>
      <PageHeader title="Paramètres" subtitle="Sites, caisses et utilisateurs" />
      <div className="space-y-6">
        <Card>
          <h2 className="mb-3 font-semibold">Sites</h2>
          <div className="divide-y divide-slate-100">
            {sites.map((site) => (
              <details key={site.id}>
                <summary className={summaryClass}>
                  <span className="font-medium">
                    {site.name} <span className="font-mono text-xs text-slate-500">({site.code})</span>
                  </span>
                  <Badge tone={site.type === "BOUTIQUE" ? "green" : "slate"}>{SITE_TYPE_LABELS[site.type]}</Badge>
                </summary>
                <div className="px-3 pb-4 pt-2">
                  <ActionForm action={saveSite} submitLabel="Enregistrer le site">
                    <SiteFields site={site} />
                  </ActionForm>
                </div>
              </details>
            ))}
            <details>
              <summary className={`${summaryClass} font-semibold text-brand-700`}>+ Ajouter un site</summary>
              <div className="px-3 pb-4 pt-2">
                <ActionForm action={saveSite} submitLabel="Créer le site" resetOnSubmit>
                  <SiteFields />
                </ActionForm>
              </div>
            </details>
          </div>
        </Card>

        <Card>
          <h2 className="mb-3 font-semibold">Caisses</h2>
          <ul className="mb-4 divide-y divide-slate-100 text-sm">
            {registers.map((register) => (
              <li key={register.id} className="flex justify-between px-3 py-2.5">
                <span className="font-medium">{register.name}</span>
                <span className="text-slate-500">{register.site.name}</span>
              </li>
            ))}
          </ul>
          <ActionForm action={saveRegister} submitLabel="Ajouter la caisse" resetOnSubmit>
            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <label htmlFor="register-site" className={labelClass}>
                  Boutique
                </label>
                <select id="register-site" name="siteId" className={inputClass}>
                  {boutiques.map((site) => (
                    <option key={site.id} value={site.id}>
                      {site.name}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="register-name" className={labelClass}>
                  Nom de la caisse
                </label>
                <input id="register-name" name="name" required placeholder="Ex. : Caisse 2" className={inputClass} />
              </div>
            </div>
          </ActionForm>
        </Card>

        <Card>
          <h2 className="mb-3 font-semibold">Utilisateurs</h2>
          <div className="divide-y divide-slate-100">
            {users.map((user) => (
              <details key={user.id}>
                <summary className={summaryClass}>
                  <span className="font-medium">
                    {user.name} <span className="font-mono text-xs text-slate-500">({user.username})</span>
                  </span>
                  <span className="flex gap-2">
                    {user.active ? null : <Badge tone="red">Désactivé</Badge>}
                    <Badge>{ROLE_LABELS[user.role]}</Badge>
                  </span>
                </summary>
                <div className="px-3 pb-4 pt-2">
                  <ActionForm action={saveUser} submitLabel="Enregistrer l'utilisateur">
                    <UserFields user={user} sites={sites} />
                  </ActionForm>
                </div>
              </details>
            ))}
            <details>
              <summary className={`${summaryClass} font-semibold text-brand-700`}>+ Ajouter un utilisateur</summary>
              <div className="px-3 pb-4 pt-2">
                <ActionForm action={saveUser} submitLabel="Créer l'utilisateur" resetOnSubmit>
                  <UserFields sites={sites} />
                </ActionForm>
              </div>
            </details>
          </div>
        </Card>
      </div>
    </>
  );
}
