import Link from "next/link";
import { logout } from "@/actions/auth";
import { isManager, requireUser } from "@/lib/auth";
import { ROLE_LABELS } from "@/lib/labels";
import { NavLinks, type NavItem } from "@/components/nav";
import { SiteSwitcher } from "@/components/site-switcher";

export default async function AppLayout({ children }: { children: React.ReactNode }) {
  const user = await requireUser();

  const items: NavItem[] = [
    { href: "/", label: "Tableau de bord" },
    { href: "/caisse", label: "Caisse" },
    { href: "/ventes", label: "Ventes" },
    { href: "/produits", label: "Produits" },
    ...(isManager(user) ? [{ href: "/sessions", label: "Sessions de caisse" }] : []),
    ...(user.role === "ADMIN" ? [{ href: "/parametres", label: "Paramètres" }] : []),
  ];

  return (
    <div className="flex min-h-screen flex-col">
      <header className="no-print sticky top-0 z-20 border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2.5">
          <Link href="/" className="flex items-center gap-2 font-bold tracking-tight">
            <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-brand-600 text-white">
              +
            </span>
            Pharmaly
          </Link>
          <div className="ml-auto flex items-center gap-3">
            <SiteSwitcher sites={user.sites} currentSiteId={user.site.id} />
            <div className="hidden text-right text-xs leading-tight sm:block">
              <div className="font-semibold text-slate-800">{user.name}</div>
              <div className="text-slate-500">{ROLE_LABELS[user.role]}</div>
            </div>
            <form action={logout}>
              <button
                type="submit"
                className="rounded-lg px-2 py-1.5 text-sm font-medium text-slate-600 hover:bg-slate-100"
              >
                Déconnexion
              </button>
            </form>
          </div>
          <div className="order-last w-full">
            <NavLinks items={items} />
          </div>
        </div>
      </header>
      <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6">{children}</main>
    </div>
  );
}
