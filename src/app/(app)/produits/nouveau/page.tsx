import { db } from "@/lib/db";
import { MANAGER_ROLES, requireUser } from "@/lib/auth";
import { Card, PageHeader } from "@/components/ui";
import { ProductForm } from "../product-form";

export default async function NouveauProduitPage() {
  await requireUser(MANAGER_ROLES);
  const categories = await db.category.findMany({ orderBy: { name: "asc" } });

  return (
    <>
      <PageHeader title="Nouveau produit" />
      <Card className="max-w-2xl">
        <ProductForm categories={categories.map((category) => category.name)} />
      </Card>
    </>
  );
}
