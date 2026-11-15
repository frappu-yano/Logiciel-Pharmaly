"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { Prisma } from "@prisma/client";
import { db } from "@/lib/db";
import { MANAGER_ROLES, requireUser } from "@/lib/auth";
import { parseMoney } from "@/lib/money";
import type { FormState } from "./types";

export const saveProduct = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  await requireUser(MANAGER_ROLES);

  const id = Number(formData.get("id")) || null;
  const name = String(formData.get("name") ?? "").trim().slice(0, 200);
  const barcode = String(formData.get("barcode") ?? "").trim().slice(0, 64);
  const reference = String(formData.get("reference") ?? "").trim().slice(0, 64);
  const categoryName = String(formData.get("category") ?? "").trim().slice(0, 80);
  const sellPrice = parseMoney(String(formData.get("sellPrice") ?? ""));
  const buyPriceInput = String(formData.get("buyPrice") ?? "").trim();
  const buyPrice = buyPriceInput ? parseMoney(buyPriceInput) : 0;
  const active = formData.get("active") === "on";

  if (!name) return { error: "Le nom du produit est obligatoire." };
  if (sellPrice === null) return { error: "Le prix de vente est invalide." };
  if (buyPrice === null) return { error: "Le prix d'achat est invalide." };

  const category = categoryName
    ? await db.category.upsert({
        where: { name: categoryName },
        update: {},
        create: { name: categoryName },
      })
    : null;

  const data = {
    name,
    barcode: barcode || null,
    reference: reference || null,
    categoryId: category?.id ?? null,
    sellPrice,
    buyPrice,
    active,
  };

  try {
    if (id) await db.product.update({ where: { id }, data });
    else await db.product.create({ data });
  } catch (error) {
    if (
      error instanceof Prisma.PrismaClientKnownRequestError &&
      error.code === "P2002"
    ) {
      return { error: "Ce code-barres est déjà utilisé par un autre produit." };
    }
    throw error;
  }

  revalidatePath("/", "layout");
  redirect("/produits");
};

export const adjustStock = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const user = await requireUser(MANAGER_ROLES);

  const productId = Number(formData.get("productId"));
  const siteId = Number(formData.get("siteId"));
  const mode = formData.get("mode") === "ENTREE" ? "ENTREE" : "AJUSTEMENT";
  const quantity = Number(formData.get("quantity"));
  const note = String(formData.get("note") ?? "").trim().slice(0, 200);

  if (!user.sites.some((site) => site.id === siteId)) {
    return { error: "Vous n'avez pas accès à ce site." };
  }
  if (!Number.isInteger(quantity) || quantity < 0 || quantity > 1_000_000) {
    return { error: "Quantité invalide." };
  }
  if (mode === "ENTREE" && quantity === 0) {
    return { error: "Saisissez la quantité reçue." };
  }

  await db.$transaction(async (tx) => {
    const current = await tx.stock.findUnique({
      where: { productId_siteId: { productId, siteId } },
    });
    const before = current?.quantity ?? 0;
    const after = mode === "ENTREE" ? before + quantity : quantity;
    const delta = after - before;
    if (delta === 0) return;

    await tx.stock.upsert({
      where: { productId_siteId: { productId, siteId } },
      update: { quantity: after },
      create: { productId, siteId, quantity: after },
    });
    await tx.stockMovement.create({
      data: {
        productId,
        siteId,
        quantity: delta,
        type: mode,
        note: note || null,
        userId: user.id,
      },
    });
  });

  revalidatePath("/", "layout");
  return { success: "Stock mis à jour." };
};
