"use client";

import { useActionState, useTransition, type FormEvent, type ReactNode } from "react";
import type { FormState } from "@/actions/types";
import { FormMessage, buttonPrimary } from "./ui";

type Props = {
  action: (previous: FormState, formData: FormData) => Promise<FormState>;
  children: ReactNode;
  submitLabel: string;
  pendingLabel?: string;
  className?: string;
  /** Vide le formulaire après un enregistrement réussi (formulaires d'ajout). */
  resetOnSubmit?: boolean;
};

export const ActionForm = ({
  action,
  children,
  submitLabel,
  pendingLabel = "Enregistrement…",
  className = "space-y-4",
  resetOnSubmit = false,
}: Props) => {
  const [state, formAction, pending] = useActionState(action, undefined);
  const [, startTransition] = useTransition();

  // Soumission manuelle : évite que React ne vide les champs après une erreur de validation.
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = event.currentTarget;
    const formData = new FormData(form);
    startTransition(() => {
      formAction(formData);
      if (resetOnSubmit) form.reset();
    });
  };

  return (
    <form onSubmit={handleSubmit} className={className}>
      {children}
      <FormMessage state={state} />
      <button type="submit" disabled={pending} className={buttonPrimary}>
        {pending ? pendingLabel : submitLabel}
      </button>
    </form>
  );
};
