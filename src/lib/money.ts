const formatter = new Intl.NumberFormat("fr-FR", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

export const CURRENCY = "DA";

/** Formate un montant en centimes : 125050 -> "1 250,50 DA" */
export const formatMoney = (centimes: number): string =>
  `${formatter.format(centimes / 100).replace(/[  ]/g, " ")} ${CURRENCY}`;

/** Convertit une saisie ("1250", "1 250,50") en centimes. Retourne null si invalide. */
export const parseMoney = (input: string): number | null => {
  const cleaned = input.replace(/\s/g, "").replace(",", ".");
  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) return null;
  return Math.round(Number(cleaned) * 100);
};

/** Centimes -> valeur de champ de saisie ("1250.50") */
export const toInputMoney = (centimes: number): string =>
  (centimes / 100).toFixed(2);
