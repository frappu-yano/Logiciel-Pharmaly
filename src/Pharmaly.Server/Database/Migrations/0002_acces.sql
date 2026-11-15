-- Accès : comptes de caisse (niveau 1), employés (niveau 2), permissions individuelles,
-- sessions et journal d'audit.

CREATE TABLE employees (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Matricule court saisi au clavier pour s'identifier.
    employee_number integer NOT NULL UNIQUE CHECK (employee_number > 0),
    full_name       text NOT NULL CHECK (btrim(full_name) <> ''),
    -- Code personnel, jamais stocké en clair.
    secret_hash     text NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE register_accounts (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    login           text NOT NULL UNIQUE CHECK (login = lower(login) AND login ~ '^[a-z0-9._-]{3,40}$'),
    display_name    text NOT NULL,
    store_id        uuid NOT NULL REFERENCES stores (id),
    password_hash   text NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE employee_permissions (
    employee_id     uuid NOT NULL REFERENCES employees (id) ON DELETE CASCADE,
    permission_code text NOT NULL,
    -- NULL : le droit vaut pour tous les sites ; sinon uniquement pour ce site.
    store_id        uuid REFERENCES stores (id),
    granted_by      uuid REFERENCES employees (id),
    granted_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (employee_id, permission_code, store_id)
);

CREATE TABLE role_templates (
    id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE
);

CREATE TABLE role_template_permissions (
    role_template_id uuid NOT NULL REFERENCES role_templates (id) ON DELETE CASCADE,
    permission_code  text NOT NULL,
    PRIMARY KEY (role_template_id, permission_code)
);

-- Les jetons de session ne sont jamais stockés : seule leur empreinte l'est.
CREATE TABLE poste_sessions (
    token_hash          text PRIMARY KEY,
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    expires_at          timestamptz NOT NULL,
    revoked_at          timestamptz
);

CREATE TABLE employee_sessions (
    token_hash       text PRIMARY KEY,
    employee_id      uuid NOT NULL REFERENCES employees (id),
    poste_token_hash text NOT NULL REFERENCES poste_sessions (token_hash),
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    ended_at         timestamptz
);

CREATE INDEX employee_sessions_employee ON employee_sessions (employee_id) WHERE ended_at IS NULL;

CREATE TABLE audit_logs (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    action              text NOT NULL,
    employee_id         uuid REFERENCES employees (id),
    register_account_id uuid REFERENCES register_accounts (id),
    store_id            uuid REFERENCES stores (id),
    entity_type         text,
    entity_id           text,
    details             jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX audit_logs_occurred_at ON audit_logs (occurred_at);
CREATE INDEX audit_logs_entity ON audit_logs (entity_type, entity_id);

-- Le journal d'audit est en ajout seul : ni modification, ni suppression, ni vidage.
CREATE FUNCTION forbid_audit_log_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le journal d''audit ne peut être ni modifié ni supprimé.'
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;

CREATE TRIGGER audit_logs_append_only
    BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION forbid_audit_log_change();

CREATE TRIGGER audit_logs_no_truncate
    BEFORE TRUNCATE ON audit_logs
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_audit_log_change();
