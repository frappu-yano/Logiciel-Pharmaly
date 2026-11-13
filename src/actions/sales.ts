"use server";

import { revalidatePath } from "next/cache";
import { z } from "zod";
import { db } from "@/lib/db";
import { isManager, requireUser } from "@/lib/auth";
import { findOpenSession } from "@/lib/cash";

export type PosProduct = {
  id: number;
  name: string;
  barcode: string | null;
  reference: string | null;
  sellPrice: number;
  stock: number;
};

const SEARCH_LIMIT = 20;

const toPosProduct = (product: {
  id: number;
  name: string;
  barcode: string | null;
  reference: string | null;
  sellPrice: number;
  stocks: { quantity: number }[];
}): PosProduct => ({
  id: product.id,
  name: product.name,
  barcode: product.barcode,
  reference: product.reference,
  sellPrice: product.sellPrice,
  stock: product.stocks[0]?.quantity ?? 0,
});

export const searchProducts = async (query: string): Promise<PosProduct[]> => {
  const user = await requireUser();
  const term = query.trim().slice(0, 80);
  if (!term) return [];

  const products = await db.product.findMany({
    where: {
      active: true,
      OR: [
        { name: { contains: term, mode: "insensitive" } },
        { barcode: { startsWith: term } },
        { reference: { contains: term, mode: "insensitive" } },
      ],
    },
    include: { stocks: { where: { siteId: user.site.id } } },
    orderBy: { name: "asc" },
    take: SEARCH_LIMIT,
  });
  return products.map(toPosProduct);
};

export const findProductByBarcode = async (
  barcode: string,
): Promise<PosProduct | null> => {
  const user = await requireUser();
  const code = barcode.trim();
  if (!code) return null;

  const product = await db.product.findFirst({
    where: { barcode: code, active: true },
    include: { stocks: { where: { siteId: user.site.id } } },
  });
  return product ? toPosProduct(product) : null;
};

const saleSchema = z.object({
  items: z
    .array(
      z.object({
        productId: z.number().int().positive(),
        quantity: z.number().int().positive().max(9999),
      }),
    )
    .min(1)
    .max(200),
  discount: z.number().int().min(0),
  method: z.enum(["ESPECES", "CARTE", "CHEQUE", "VIREMENT"]),
  received: z.number().int().min(0).nullable(),
  customerName: z.string().trim().max(120).nullable(),
});

export type SaleInput = z.infer<typeof saleSchema>;

export type SaleResult =
  | { ok: true; saleId: number; number: string; total: number; change: number }
  | { ok: false; error: string };

export const createSale = async (input: SaleInput): Promise<SaleResult> => {
  const user = await requireUser();
  const parsed = saleSchema.safeParse(input);
  if (!parsed.success) return { ok: false, error: "Vente invalide." };
  const { discount, method, received, customerName } = parsed.data;

  const quantities = new Map<number, number>();
  for (const item of parsed.data.items) {
    quantities.set(
      item.productId,
      (quantities.get(item.productId) ?? 0) + item.quantity,
    );
  }

  try {
    const result = await db.$transaction(async (tx) => {
      const session = await findOpenSession(user.id, user.site.id, tx);
      if (user.site.type === "BOUTIQUE" && !session) {
        throw new SaleError("Ouvrez une caisse avant d'enregistrer une vente.");
      }

      const products = await tx.product.findMany({
        where: { id: { in: [...quantities.keys()] }, active: true },
      });
      if (products.length !== quantities.size) {
        throw new SaleError(
          "Un produit du panier n'existe plus ou a été désactivé.",
        );
      }

      const lines = products.map((product) => {
        const quantity = quantities.get(product.id) ?? 0;
        return {
          productId: product.id,
          name: product.name,
          unitPrice: product.sellPrice,
          quantity,
          total: product.sellPrice * quantity,
        };
      });
      const subtotal = lines.reduce((sum, line) => sum + line.total, 0);
      if (discount > subtotal) {
        throw new SaleError("La remise dépasse le montant de la vente.");
      }
      const total = subtotal - discount;
      if (method === "ESPECES" && received !== null && received < total) {
        throw new SaleError("Le montant reçu est inférieur au total.");
      }

      const sale = await tx.sale.create({
        data: {
          siteId: user.site.id,
          sessionId: session?.id ?? null,
          userId: user.id,
          subtotal,
          discount,
          total,
          customerName: customerName || null,
          items: { create: lines },
          payments: {
            create: {
              method,
              amount: total,
              received: method === "ESPECES" ? (received ?? total) : null,
            },
          },
        },
      });
      const number = `${user.site.code}-${String(sale.id).padStart(6, "0")}`;
      await tx.sale.update({ where: { id: sale.id }, data: { number } });

      for (const line of lines) {
        await tx.stock.upsert({
          where: {
            productId_siteId: {
              productId: line.productId,
              siteId: user.site.id,
            },
          },
          update: { quantity: { decrement: line.quantity } },
          create: {
            productId: line.productId,
            siteId: user.site.id,
            quantity: -line.quantity,
          },
        });
      }
      await tx.stockMovement.createMany({
        data: lines.map((line) => ({
          productId: line.productId,
          siteId: user.site.id,
          quantity: -line.quantity,
          type: "VENTE" as const,
          saleId: sale.id,
          userId: user.id,
        })),
      });

      const change =
        method === "ESPECES" && received !== null ? received - total : 0;
      return { saleId: sale.id, number, total, change };
    });

    revalidatePath("/", "layout");
    return { ok: true, ...result };
  } catch (error) {
    if (error instanceof SaleError) return { ok: false, error: error.message };
    throw error;
  }
};

class SaleError extends Error {}

export const cancelSale = async (
  saleId: number,
  reason: string,
): Promise<{ ok: true } | { ok: false; error: string }> => {
  const user = await requireUser();
  if (!isManager(user)) {
    return { ok: false, error: "Seul un gérant peut annuler une vente." };
  }
  const cleanReason = reason.trim().slice(0, 200);
  if (!cleanReason) return { ok: false, error: "Indiquez le motif d'annulation." };

  try {
    await db.$transaction(async (tx) => {
      const sale = await tx.sale.findUnique({
        where: { id: saleId },
        include: { items: true, session: true },
      });
      if (!sale || !user.sites.some((site) => site.id === sale.siteId)) {
        throw new SaleError("Vente introuvable.");
      }
      if (sale.status === "ANNULEE") {
        throw new SaleError("Cette vente est déjà annulée.");
      }
      if (sale.session?.closedAt) {
        throw new SaleError(
          "La caisse de cette vente est clôturée : l'annulation n'est plus possible.",
        );
      }

      await tx.sale.update({
        where: { id: sale.id },
        data: {
          status: "ANNULEE",
          cancelledAt: new Date(),
          cancelledById: user.id,
          cancelReason: cleanReason,
        },
      });
      for (const item of sale.items) {
        await tx.stock.upsert({
          where: {
            productId_siteId: { productId: item.productId, siteId: sale.siteId },
          },
          update: { quantity: { increment: item.quantity } },
          create: {
            productId: item.productId,
            siteId: sale.siteId,
            quantity: item.quantity,
          },
        });
      }
      await tx.stockMovement.createMany({
        data: sale.items.map((item) => ({
          productId: item.productId,
          siteId: sale.siteId,
          quantity: item.quantity,
          type: "ANNULATION" as const,
          saleId: sale.id,
          userId: user.id,
          note: cleanReason,
        })),
      });
    });
  } catch (error) {
    if (error instanceof SaleError) return { ok: false, error: error.message };
    throw error;
  }

  revalidatePath("/", "layout");
  return { ok: true };
};
