"use client";

import { buttonSecondary } from "./ui";

export const PrintButton = ({ label = "Imprimer" }: { label?: string }) => (
  <button type="button" onClick={() => window.print()} className={`${buttonSecondary} no-print`}>
    {label}
  </button>
);
