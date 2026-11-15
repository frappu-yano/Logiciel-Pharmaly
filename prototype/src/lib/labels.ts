import type { PaymentMethod, Role, SiteType } from "@prisma/client";

export const ROLE_LABELS: Record<Role, string> = {
  ADMIN: "Administrateur",
  GERANT: "Gérant",
  CAISSIER: "Caissier",
};

export const SITE_TYPE_LABELS: Record<SiteType, string> = {
  BOUTIQUE: "Boutique",
  EN_LIGNE: "En ligne",
};

export const PAYMENT_LABELS: Record<PaymentMethod, string> = {
  ESPECES: "Espèces",
  CARTE: "Carte",
  CHEQUE: "Chèque",
  VIREMENT: "Virement",
};

export const PAYMENT_METHODS: PaymentMethod[] = [
  "ESPECES",
  "CARTE",
  "CHEQUE",
  "VIREMENT",
];

const dateTimeFormatter = new Intl.DateTimeFormat("fr-FR", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Africa/Algiers",
});

export const formatDateTime = (date: Date): string =>
  dateTimeFormatter.format(date);

/** Début de la journée (heure d'Algérie, UTC+1 sans changement d'heure) pour une date "AAAA-MM-JJ". */
export const startOfDay = (isoDate: string): Date =>
  new Date(`${isoDate}T00:00:00+01:00`);

/** Date du jour en Algérie au format "AAAA-MM-JJ". */
export const todayIso = (): string =>
  new Intl.DateTimeFormat("en-CA", { timeZone: "Africa/Algiers" }).format(
    new Date(),
  );

const DAY_MS = 24 * 60 * 60 * 1000;

export const dayRange = (isoDate: string): { gte: Date; lt: Date } => {
  const start = startOfDay(isoDate);
  return { gte: start, lt: new Date(start.getTime() + DAY_MS) };
};
