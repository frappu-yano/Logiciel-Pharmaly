import { login } from "@/actions/auth";
import { ActionForm } from "@/components/action-form";
import { inputClass, labelClass } from "@/components/ui";

export const LoginForm = () => (
  <ActionForm
    action={login}
    submitLabel="Se connecter"
    pendingLabel="Connexion…"
    className="space-y-4 [&>button]:w-full"
  >
    <div>
      <label htmlFor="username" className={labelClass}>
        Identifiant
      </label>
      <input
        id="username"
        name="username"
        autoComplete="username"
        autoCapitalize="none"
        autoFocus
        required
        className={inputClass}
      />
    </div>
    <div>
      <label htmlFor="password" className={labelClass}>
        Mot de passe
      </label>
      <input
        id="password"
        name="password"
        type="password"
        autoComplete="current-password"
        required
        className={inputClass}
      />
    </div>
  </ActionForm>
);
