import Link from "next/link";
import { db } from "@/lib/db";
import { isManager, requireUser } from "@/lib/auth";
import { formatMoney } from "@/lib/money";
import { Badge, Card, PageHeader, buttonPrimary, buttonSecondary, inputClass, tableClass, tdClass, thClass } from "@/components/ui";

const PAGE_SIZE = 50;

export default async function ProduitsPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; page?: string }>;
}) {
  const user = await requireUser();
  const { q = "", page: pageParam } = await searchParams;
  const term = q.trim().slice(0, 80);
  const page = Math.max(Number(pageParam) || 1, 1);
  const canEdit = isManager(user);

  const where = term
    ? {
        OR: [
          { name: { contains: term, mode: "insensitive" as const } },
          { barcode: { startsWith: term } },
          { reference: { contains: term, mode: "insensitive" as const } },
        ],
      }
    : {};

  const [products, count] = await Promise.all([
    db.product.findMany({
      where,
      include: { category: true, stocks: { where: { siteId: user.site.id } } },
      orderBy: { name: "asc" },
      skip: (page - 1) * PAGE_SIZE,
      take: PAGE_SIZE,
    }),
    db.product.count({ where }),
  ]);
  const pageCount = Math.max(Math.ceil(count / PAGE_SIZE), 1);
  const pageHref = (target: number) =>
    `/produits?${new URLSearchParams({ ...(term ? { q: term } : {}), page: String(target) })}`;

  return (
    <>
      <PageHeader
        title="Produits"
        subtitle={`${count} produit(s) · stock affiché : ${user.site.name}`}
        action={
          canEdit ? (
            <Link href="/produits/nouveau" className={buttonPrimary}>
              Nouveau produit
            </Link>
          ) : null
        }
      />
      <form className="mb-4 flex max-w-xl gap-2">
        <label htmlFor="q" className="sr-only">
          Rechercher un produit
        </label>
        <input id="q" name="q" defaultValue={term} placeholder="Nom, code-barres ou référence" className={inputClass} />
        <button type="submit" className={buttonSecondary}>
          Rechercher
        </button>
      </form>
      <Card className="overflow-x-auto p-0">
        {products.length === 0 ? (
          <p className="p-5 text-sm text-slate-500">
            {term ? "Aucun produit ne correspond à cette recherche." : "Aucun produit. Créez le premier avec « Nouveau produit »."}
          </p>
        ) : (
          <table className={tableClass}>
            <thead>
              <tr>
                <th className={thClass}>Produit</th>
                <th className={thClass}>Code-barres</th>
                <th className={thClass}>Catégorie</th>
                <th className={`${thClass} text-right`}>Prix de vente</th>
                <th className={`${thClass} text-right`}>Stock</th>
              </tr>
            </thead>
            <tbody>
              {products.map((product) => {
                const stock = product.stocks[0]?.quantity ?? 0;
                return (
                  <tr key={product.id} className="hover:bg-slate-50">
                    <td className={tdClass}>
                      {canEdit ? (
                        <Link href={`/produits/${product.id}`} className="font-medium text-brand-700 hover:underline">
                          {product.name}
                        </Link>
                      ) : (
                        <span className="font-medium">{product.name}</span>
                      )}
                      {product.active ? null : (
                        <span className="ml-2">
                          <Badge>Désactivé</Badge>
                        </span>
                      )}
                    </td>
                    <td className={`${tdClass} font-mono text-xs`}>{product.barcode ?? "—"}</td>
                    <td className={tdClass}>{product.category?.name ?? "—"}</td>
                    <td className={`${tdClass} text-right tabular-nums`}>{formatMoney(product.sellPrice)}</td>
                    <td className={`${tdClass} text-right`}>
                      <Badge tone={stock <= 0 ? "red" : stock <= 5 ? "amber" : "green"}>{stock}</Badge>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Card>
      {pageCount > 1 ? (
        <nav className="mt-4 flex items-center justify-between text-sm" aria-label="Pagination">
          {page > 1 ? (
            <Link href={pageHref(page - 1)} className={buttonSecondary}>
              Précédent
            </Link>
          ) : (
            <span />
          )}
          <span className="text-slate-500">
            Page {page} sur {pageCount}
          </span>
          {page < pageCount ? (
            <Link href={pageHref(page + 1)} className={buttonSecondary}>
              Suivant
            </Link>
          ) : (
            <span />
          )}
        </nav>
      ) : null}
    </>
  );
}
