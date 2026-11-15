"use server";

import { redirect } from "next/navigation";
import bcrypt from "bcryptjs";
import { db } from "@/lib/db";
import {
  createSession,
  destroySession,
  listAccessibleSites,
  requireUser,
} from "@/lib/auth";
import type { FormState } from "./types";

const MAX_ATTEMPTS = 5;
const LOCK_MINUTES = 5;
const attempts = new Map<string, { count: number; firstAt: number }>();

const isLocked = (username: string): boolean => {
  const entry = attempts.get(username);
  if (!entry) return false;
  if (Date.now() - entry.firstAt > LOCK_MINUTES * 60_000) {
    attempts.delete(username);
    return false;
  }
  return entry.count >= MAX_ATTEMPTS;
};

const recordFailure = (username: string): void => {
  const entry = attempts.get(username);
  if (!entry) {
    attempts.set(username, { count: 1, firstAt: Date.now() });
    return;
  }
  entry.count += 1;
};

export const login = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const username = String(formData.get("username") ?? "").trim().toLowerCase();
  const password = String(formData.get("password") ?? "");
  if (!username || !password) {
    return { error: "Saisissez votre identifiant et votre mot de passe." };
  }
  if (isLocked(username)) {
    return {
      error: `Trop de tentatives. Réessayez dans ${LOCK_MINUTES} minutes.`,
    };
  }

  const user = await db.user.findUnique({ where: { username } });
  const valid =
    user !== null &&
    user.active &&
    (await bcrypt.compare(password, user.passwordHash));
  if (!user || !valid) {
    recordFailure(username);
    return { error: "Identifiant ou mot de passe incorrect." };
  }
  attempts.delete(username);

  const sites = await listAccessibleSites(user);
  const firstSite = sites[0];
  if (!firstSite) {
    return {
      error: "Aucun site ne vous est affecté. Contactez un administrateur.",
    };
  }

  await createSession({ userId: user.id, siteId: firstSite.id });
  redirect("/");
};

export const logout = async (): Promise<void> => {
  await destroySession();
  redirect("/login");
};

export const switchSite = async (formData: FormData): Promise<void> => {
  const user = await requireUser();
  const siteId = Number(formData.get("siteId"));
  const site = user.sites.find((candidate) => candidate.id === siteId);
  if (site) await createSession({ userId: user.id, siteId: site.id });
  redirect("/");
};
