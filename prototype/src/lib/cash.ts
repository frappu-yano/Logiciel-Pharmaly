import "server-only";
import type { Prisma } from "@prisma/client";
import { db } from "./db";

type Client = Prisma.TransactionClient | typeof db;

/** Session de caisse ouverte par cet utilisateur sur ce site, s'il y en a une. */
export const findOpenSession = (userId: number, siteId: number, client: Client = db) =>
  client.cashSession.findFirst({
    where: { userId, closedAt: null, register: { siteId } },
    include: { register: true },
  });

export type SessionSummary = {
  salesCount: number;
  cancelledCount: number;
  totalSales: number;
  byMethod: Record<string, number>;
  cashSales: number;
  cashIn: number;
  cashOut: number;
  expectedCash: number;
};

/** Calcule les totaux d'une session : ventes par mode de paiement et espèces attendues. */
export const summarizeSession = async (
  sessionId: number,
  client: Client = db,
): Promise<SessionSummary> => {
  const session = await client.cashSession.findUniqueOrThrow({
    where: { id: sessionId },
    select: { openingFloat: true },
  });

  const [payments, salesCount, cancelledCount, movements] = await Promise.all([
    client.payment.groupBy({
      by: ["method"],
      where: { sale: { sessionId, status: "VALIDEE" } },
      _sum: { amount: true },
    }),
    client.sale.count({ where: { sessionId, status: "VALIDEE" } }),
    client.sale.count({ where: { sessionId, status: "ANNULEE" } }),
    client.cashMovement.groupBy({
      by: ["type"],
      where: { sessionId },
      _sum: { amount: true },
    }),
  ]);

  const byMethod: Record<string, number> = {};
  for (const payment of payments) {
    byMethod[payment.method] = payment._sum.amount ?? 0;
  }
  const totalSales = Object.values(byMethod).reduce((sum, value) => sum + value, 0);
  const cashSales = byMethod.ESPECES ?? 0;
  const cashIn = movements.find((m) => m.type === "ENTREE")?._sum.amount ?? 0;
  const cashOut = movements.find((m) => m.type === "SORTIE")?._sum.amount ?? 0;

  return {
    salesCount,
    cancelledCount,
    totalSales,
    byMethod,
    cashSales,
    cashIn,
    cashOut,
    expectedCash: session.openingFloat + cashSales + cashIn - cashOut,
  };
};
