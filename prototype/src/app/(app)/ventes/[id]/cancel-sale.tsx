"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { cancelSale } from "@/actions/sales";
import { buttonDanger, buttonSecondary, inputClass, labelClass } from "@/components/ui";

export const CancelSale = ({ saleId }: { saleId: number }) => {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleCancel = async () => {
    setPending(true);
    setError(null);
    const result = await cancelSale(saleId, reason);
    setPending(false);
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setOpen(false);
    router.refresh();
  };

  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)} className={buttonDanger}>
        Annuler la vente
      </button>
    );
  }

  return (
    <div className="w-full space-y-3 rounded-lg border border-red-200 bg-red-50 p-4">
      <p className="text-sm text-red-800">
        L&apos;annulation remet les produits en stock et retire la vente du total de la
        caisse. Elle ne peut pas être défaite.
      </p>
      <div>
        <label htmlFor="cancel-reason" className={labelClass}>
          Motif de l&apos;annulation
        </label>
        <input
          id="cancel-reason"
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          maxLength={200}
          autoFocus
          className={inputClass}
        />
      </div>
      {error ? (
        <p role="alert" className="text-sm font-medium text-red-700">
          {error}
        </p>
      ) : null}
      <div className="flex gap-2">
        <button type="button" onClick={() => void handleCancel()} disabled={pending} className={buttonDanger}>
          {pending ? "Annulation…" : "Confirmer l'annulation"}
        </button>
        <button type="button" onClick={() => setOpen(false)} className={buttonSecondary}>
          Garder la vente
        </button>
      </div>
    </div>
  );
};
