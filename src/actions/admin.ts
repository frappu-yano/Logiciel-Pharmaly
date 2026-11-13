"use server";

import { revalidatePath } from "next/cache";
import bcrypt from "bcryptjs";
import { Prisma } from "@prisma/client";
import { db } from "@/lib/db";
import { requireUser } from "@/lib/auth";
import type { FormState } from "./types";

const MIN_PASSWORD_LENGTH = 8;

const isUniqueError = (error: unknown): boolean =>
  error instanceof Prisma.PrismaClientKnownRequestError && error.code === "P2002";

export const saveSite = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  await requireUser(["ADMIN"]);

  const id = Number(formData.get("id")) || null;
  const name = String(formData.get("name") ?? "").trim().slice(0, 100);
  const code = String(formData.get("code") ?? "")
    .trim()
    .toUpperCase()
    .slice(0, 6);
  const type = formData.get("type") === "EN_LIGNE" ? "EN_LIGNE" : "BOUTIQUE";
  const address = String(formData.get("address") ?? "").trim().slice(0, 200);
  const phone = String(formData.get("phone") ?? "").trim().slice(0, 40);

  if (!name) return { error: "Le nom du site est obligatoire." };
  if (!/^[A-Z0-9]{1,6}$/.test(code)) {
    return { error: "Le code doit contenir 1 à 6 lettres ou chiffres." };
  }

  const data = { name, code, type, address: address || null, phone: phone || null } as const;
  try {
    if (id) await db.site.update({ where: { id }, data });
    else await db.site.create({ data });
  } catch (error) {
    if (isUniqueError(error)) return { error: "Ce code de site existe déjà." };
    throw error;
  }

  revalidatePath("/", "layout");
  return { success: "Site enregistré." };
};

export const saveRegister = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  await requireUser(["ADMIN"]);

  const siteId = Number(formData.get("siteId"));
  const name = String(formData.get("name") ?? "").trim().slice(0, 60);
  if (!name) return { error: "Le nom de la caisse est obligatoire." };

  const site = await db.site.findFirst({ where: { id: siteId, type: "BOUTIQUE" } });
  if (!site) return { error: "Choisissez une boutique." };

  await db.register.create({ data: { siteId, name } });
  revalidatePath("/", "layout");
  return { success: "Caisse ajoutée." };
};

export const saveUser = async (
  _previous: FormState,
  formData: FormData,
): Promise<FormState> => {
  const admin = await requireUser(["ADMIN"]);

  const id = Number(formData.get("id")) || null;
  const name = String(formData.get("name") ?? "").trim().slice(0, 100);
  const username = String(formData.get("username") ?? "")
    .trim()
    .toLowerCase()
    .slice(0, 40);
  const password = String(formData.get("password") ?? "");
  const roleInput = String(formData.get("role") ?? "");
  const role =
    roleInput === "ADMIN" || roleInput === "GERANT" ? roleInput : "CAISSIER";
  const active = formData.get("active") === "on";
  const siteIds = formData
    .getAll("siteIds")
    .map(Number)
    .filter((siteId) => Number.isInteger(siteId) && siteId > 0);

  if (!name) return { error: "Le nom est obligatoire." };
  if (!/^[a-z0-9._-]{3,40}$/.test(username)) {
    return {
      error: "L'identifiant doit contenir 3 à 40 lettres minuscules, chiffres, points ou tirets.",
    };
  }
  if (!id && password.length < MIN_PASSWORD_LENGTH) {
    return {
      error: `Le mot de passe doit contenir au moins ${MIN_PASSWORD_LENGTH} caractères.`,
    };
  }
  if (password && password.length < MIN_PASSWORD_LENGTH) {
    return {
      error: `Le mot de passe doit contenir au moins ${MIN_PASSWORD_LENGTH} caractères.`,
    };
  }
  if (id === admin.id && (!active || role !== "ADMIN")) {
    return {
      error: "Vous ne pouvez pas désactiver votre propre compte ni retirer votre rôle d'administrateur.",
    };
  }

  const passwordHash = password ? await bcrypt.hash(password, 12) : undefined;
  const sites = siteIds.map((siteId) => ({ id: siteId }));

  try {
    if (id) {
      await db.user.update({
        where: { id },
        data: {
          name,
          username,
          role,
          active,
          ...(passwordHash ? { passwordHash } : {}),
          sites: { set: sites },
        },
      });
    } else {
      await db.user.create({
        data: {
          name,
          username,
          role,
          active,
          passwordHash: passwordHash ?? "",
          sites: { connect: sites },
        },
      });
    }
  } catch (error) {
    if (isUniqueError(error)) return { error: "Cet identifiant est déjà utilisé." };
    throw error;
  }

  revalidatePath("/", "layout");
  return { success: "Utilisateur enregistré." };
};
