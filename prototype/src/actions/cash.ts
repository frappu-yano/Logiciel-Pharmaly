"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import { findOpenSession, summarizeSession } from "@/lib/cash";
import { parseMoney } from "@/lib/money";
import type { FormState } from "./types";

export const openSession = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const user = await requireUser();
  const registerId = Number(formData.get("registerId"));
  const openingFloat = parseMoney(String(formData.get("openingFloat") ?? ""));
  if (openingFloat === null) {
    return { error: "Le fond de caisse est invalide." };
  }

  const error = await db.$transaction(async (tx) => {
    const register = await tx.register.findFirst({
      where: { id: registerId, siteId: user.site.id, active: true },
    });
    if (!register) return "Caisse introuvable sur ce site.";

    if (await findOpenSession(user.id, user.site.id, tx)) {
      return "Vous avez déjà une caisse ouverte.";
    }
    const busy = await tx.cashSession.findFirst({
      where: { registerId, closedAt: null },
      include: { user: { select: { name: true } } },
    });
    if (busy) return `Cette caisse est déjà ouverte par ${busy.user.name}.`;

    await tx.cashSession.create({
      data: { registerId, userId: user.id, openingFloat },
    });
    return null;
  });
  if (error) return { error };

  revalidatePath("/", "layout");
  redirect("/caisse");
};

export const addCashMovement = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const user = await requireUser();
  const type = formData.get("type") === "ENTREE" ? "ENTREE" : "SORTIE";
  const amount = parseMoney(String(formData.get("amount") ?? ""));
  const reason = String(formData.get("reason") ?? "").trim().slice(0, 200);
  if (amount === null || amount <= 0) return { error: "Montant invalide." };
  if (!reason) return { error: "Indiquez le motif du mouvement." };

  const session = await findOpenSession(user.id, user.site.id);
  if (!session) return { error: "Aucune caisse ouverte." };

  await db.cashMovement.create({
    data: { sessionId: session.id, type, amount, reason, userId: user.id },
  });
  revalidatePath("/", "layout");
  return { success: "Mouvement enregistré." };
};

export const closeSession = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const user = await requireUser();
  const countedCash = parseMoney(String(formData.get("countedCash") ?? ""));
  const note = String(formData.get("note") ?? "").trim().slice(0, 500);
  if (countedCash === null) {
    return { error: "Saisissez le montant des espèces comptées." };
  }

  const sessionId = await db.$transaction(async (tx) => {
    const session = await findOpenSession(user.id, user.site.id, tx);
    if (!session) return null;
    const summary = await summarizeSession(session.id, tx);
    await tx.cashSession.update({
      where: { id: session.id },
      data: {
        closedAt: new Date(),
        closedById: user.id,
        expectedCash: summary.expectedCash,
        countedCash,
        note: note || null,
      },
    });
    return session.id;
  });
  if (sessionId === null) return { error: "Aucune caisse ouverte." };

  revalidatePath("/", "layout");
  redirect(`/sessions/${sessionId}`);
};
