import "server-only";
import { cache } from "react";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { SignJWT, jwtVerify } from "jose";
import type { Role, SiteType } from "@prisma/client";
import { db } from "./db";

const COOKIE_NAME = "pharmaly_session";
const SESSION_DAYS = 7;

type SessionPayload = { userId: number; siteId: number };

export type CurrentSite = {
  id: number;
  code: string;
  name: string;
  type: SiteType;
};

export type CurrentUser = {
  id: number;
  name: string;
  username: string;
  role: Role;
  sites: CurrentSite[];
  site: CurrentSite;
};

const getSecret = (): Uint8Array => {
  const secret = process.env.SESSION_SECRET;
  if (!secret || secret.length < 32) {
    throw new Error(
      "SESSION_SECRET est manquant ou trop court (32 caractères minimum).",
    );
  }
  return new TextEncoder().encode(secret);
};

export const createSession = async (payload: SessionPayload): Promise<void> => {
  const token = await new SignJWT(payload)
    .setProtectedHeader({ alg: "HS256" })
    .setIssuedAt()
    .setExpirationTime(`${SESSION_DAYS}d`)
    .sign(getSecret());

  (await cookies()).set(COOKIE_NAME, token, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.COOKIE_SECURE === "true",
    path: "/",
    maxAge: SESSION_DAYS * 24 * 60 * 60,
  });
};

export const destroySession = async (): Promise<void> => {
  (await cookies()).delete(COOKIE_NAME);
};

const readSession = async (): Promise<SessionPayload | null> => {
  const token = (await cookies()).get(COOKIE_NAME)?.value;
  if (!token) return null;
  try {
    const { payload } = await jwtVerify(token, getSecret());
    const { userId, siteId } = payload;
    if (typeof userId !== "number" || typeof siteId !== "number") return null;
    return { userId, siteId };
  } catch {
    return null;
  }
};

/** Sites accessibles : tous pour un administrateur, sinon ceux qui lui sont affectés. */
export const listAccessibleSites = async (user: {
  id: number;
  role: Role;
}): Promise<CurrentSite[]> =>
  db.site.findMany({
    where: {
      active: true,
      ...(user.role === "ADMIN" ? {} : { users: { some: { id: user.id } } }),
    },
    select: { id: true, code: true, name: true, type: true },
    orderBy: { id: "asc" },
  });

export const getCurrentUser = cache(async (): Promise<CurrentUser | null> => {
  const session = await readSession();
  if (!session) return null;

  const user = await db.user.findUnique({
    where: { id: session.userId },
    select: { id: true, name: true, username: true, role: true, active: true },
  });
  if (!user || !user.active) return null;

  const sites = await listAccessibleSites(user);
  const site = sites.find((candidate) => candidate.id === session.siteId);
  if (!site) return null;

  return {
    id: user.id,
    name: user.name,
    username: user.username,
    role: user.role,
    sites,
    site,
  };
});

export const requireUser = async (roles?: Role[]): Promise<CurrentUser> => {
  const user = await getCurrentUser();
  if (!user) redirect("/login");
  if (roles && !roles.includes(user.role)) redirect("/");
  return user;
};

export const MANAGER_ROLES: Role[] = ["ADMIN", "GERANT"];

export const isManager = (user: CurrentUser): boolean =>
  MANAGER_ROLES.includes(user.role);
