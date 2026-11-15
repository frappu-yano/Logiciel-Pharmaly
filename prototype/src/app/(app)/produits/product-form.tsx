import { saveProduct } from "@/actions/products";
import { toInputMoney } from "@/lib/money";
import { ActionForm } from "@/components/action-form";
import { inputClass, labelClass } from "@/components/ui";

type ProductValues = {
  id: number;
  name: string;
  barcode: string | null;
  reference: string | null;
  sellPrice: number;
  buyPrice: number;
  active: boolean;
  category: { name: string } | null;
};

export const ProductForm = ({
  product,
  categories,
}: {
  product?: ProductValues;
  categories: string[];
}) => (
  <ActionForm action={saveProduct} submitLabel="Enregistrer le produit">
    {product ? <input type="hidden" name="id" value={product.id} /> : null}
    <div>
      <label htmlFor="name" className={labelClass}>
        Nom du produit
      </label>
      <input id="name" name="name" defaultValue={product?.name} required maxLength={200} className={inputClass} />
    </div>
    <div className="grid gap-4 sm:grid-cols-2">
      <div>
        <label htmlFor="barcode" className={labelClass}>
          Code-barres
        </label>
        <input
          id="barcode"
          name="barcode"
          defaultValue={product?.barcode ?? ""}
          maxLength={64}
          placeholder="Scannez ou saisissez le code"
          className={`${inputClass} font-mono`}
        />
      </div>
      <div>
        <label htmlFor="reference" className={labelClass}>
          Référence interne
        </label>
        <input id="reference" name="reference" defaultValue={product?.reference ?? ""} maxLength={64} className={inputClass} />
      </div>
    </div>
    <div>
      <label htmlFor="category" className={labelClass}>
        Catégorie
      </label>
      <input
        id="category"
        name="category"
        list="category-options"
        defaultValue={product?.category?.name ?? ""}
        maxLength={80}
        className={inputClass}
      />
      <datalist id="category-options">
        {categories.map((category) => (
          <option key={category} value={category} />
        ))}
      </datalist>
    </div>
    <div className="grid gap-4 sm:grid-cols-2">
      <div>
        <label htmlFor="sellPrice" className={labelClass}>
          Prix de vente (DA)
        </label>
        <input
          id="sellPrice"
          name="sellPrice"
          inputMode="decimal"
          defaultValue={product ? toInputMoney(product.sellPrice) : ""}
          required
          className={inputClass}
        />
      </div>
      <div>
        <label htmlFor="buyPrice" className={labelClass}>
          Prix d&apos;achat (DA)
        </label>
        <input
          id="buyPrice"
          name="buyPrice"
          inputMode="decimal"
          defaultValue={product ? toInputMoney(product.buyPrice) : ""}
          className={inputClass}
        />
      </div>
    </div>
    <label className="flex items-center gap-2 text-sm text-slate-700">
      <input type="checkbox" name="active" defaultChecked={product?.active ?? true} className="h-4 w-4 accent-brand-600" />
      Produit en vente
    </label>
  </ActionForm>
);
