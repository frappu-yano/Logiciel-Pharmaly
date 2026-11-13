import { notFound } from "next/navigation";
import { db } from "@/lib/db";
import { MANAGER_ROLES, requireUser } from "@/lib/auth";
import { adjustStock } from "@/actions/products";
import { ActionForm } from "@/components/action-form";
import { Card, PageHeader, inputClass, labelClass } from "@/components/ui";
import { ProductForm } from "../product-form";

export default async function ProduitPage({ params }: { params: Promise<{ id: string }> }) {
  const user = await requireUser(MANAGER_ROLES);
  const productId = Number((await params).id);
  if (!Number.isInteger(productId)) notFound();

  const [product, categories] = await Promise.all([
    db.product.findUnique({
      where: { id: productId },
      include: { category: true, stocks: true },
    }),
    db.category.findMany({ orderBy: { name: "asc" } }),
  ]);
  if (!product) notFound();

  return (
    <>
      <PageHeader title={product.name} subtitle="Fiche produit et stock par site" />
      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <h2 className="mb-3 font-semibold">Fiche produit</h2>
          <ProductForm product={product} categories={categories.map((category) => category.name)} />
        </Card>
        <div className="space-y-4">
          {user.sites.map((site) => {
            const quantity = product.stocks.find((stock) => stock.siteId === site.id)?.quantity ?? 0;
            return (
              <Card key={site.id}>
                <div className="mb-3 flex items-baseline justify-between">
                  <h2 className="font-semibold">{site.name}</h2>
                  <p className="text-sm text-slate-600">
                    En stock : <span className="text-lg font-bold tabular-nums text-slate-900">{quantity}</span>
                  </p>
                </div>
                <ActionForm action={adjustStock} submitLabel="Mettre à jour le stock" resetOnSubmit>
                  <input type="hidden" name="productId" value={product.id} />
                  <input type="hidden" name="siteId" value={site.id} />
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label htmlFor={`mode-${site.id}`} className={labelClass}>
                        Opération
                      </label>
                      <select id={`mode-${site.id}`} name="mode" defaultValue="ENTREE" className={inputClass}>
                        <option value="ENTREE">Ajouter (réception)</option>
                        <option value="AJUSTEMENT">Fixer le stock (inventaire)</option>
                      </select>
                    </div>
                    <div>
                      <label htmlFor={`quantity-${site.id}`} className={labelClass}>
                        Quantité
                      </label>
                      <input
                        id={`quantity-${site.id}`}
                        name="quantity"
                        type="number"
                        min={0}
                        step={1}
                        required
                        className={inputClass}
                      />
                    </div>
                  </div>
                  <div>
                    <label htmlFor={`note-${site.id}`} className={labelClass}>
                      Remarque (facultatif)
                    </label>
                    <input id={`note-${site.id}`} name="note" maxLength={200} className={inputClass} />
                  </div>
                </ActionForm>
              </Card>
            );
          })}
        </div>
      </div>
    </>
  );
}
