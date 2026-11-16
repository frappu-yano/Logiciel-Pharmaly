-- Retours clients et clôture quotidienne.
--
-- Retour : document compensatoire relié à un ticket, jamais modifié. Il appartient à la
-- journée où il est fait ; il ne réécrit ni la vente d'origine ni son jour.
-- Clôture (D11) : une par magasin et par jour. Elle renseigne « business_date » sur toutes
-- les opérations du magasin qui n'ont pas encore de journée, et fige un rapport.

-- ---------- Motifs de retour (D8, valeurs par défaut à valider par la direction) ----------

CREATE TABLE return_reasons (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    label             text NOT NULL CHECK (btrim(label) <> ''),
    -- Destination de la marchandise : emplacement vendable du site, emplacement « mis à
    -- l'écart » du site, ou aucune réintégration.
    stock_destination text NOT NULL CHECK (stock_destination IN ('VENDABLE', 'ECARTE', 'AUCUNE')),
    is_active         boolean NOT NULL DEFAULT true,
    sort_order        integer NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX return_reasons_label ON return_reasons (lower(label));

INSERT INTO return_reasons (label, stock_destination, sort_order) VALUES
    ('Retour revendable', 'VENDABLE', 1),
    ('Produit endommagé', 'AUCUNE', 2),
    ('Produit défectueux', 'ECARTE', 3),
    ('Erreur de vente', 'VENDABLE', 4);

-- ---------- Retours ----------

-- Compteur de retours par site : incrémenté dans la transaction du retour, sans trou.
CREATE TABLE return_counters (
    store_id    uuid PRIMARY KEY REFERENCES stores (id),
    last_number bigint NOT NULL DEFAULT 0 CHECK (last_number >= 0)
);

CREATE TABLE returns (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id                uuid NOT NULL REFERENCES stores (id),
    return_seq              bigint NOT NULL CHECK (return_seq > 0),
    -- Numéro lisible, préfixé du code du site : « D1-R000042 ».
    return_number           text NOT NULL UNIQUE,
    sale_id                 uuid NOT NULL REFERENCES sales (id),
    reason_id               uuid NOT NULL REFERENCES return_reasons (id),
    -- Libellé et destination du motif au moment du retour : le document ne change pas si
    -- le motif est modifié ensuite.
    reason_label            text NOT NULL,
    stock_destination       text NOT NULL CHECK (stock_destination IN ('VENDABLE', 'ECARTE', 'AUCUNE')),
    -- Emplacement où la marchandise est réintégrée ; absent si elle sort du circuit.
    destination_location_id uuid REFERENCES stock_locations (id),
    note                    text,
    -- Employé réellement identifié, compte de caisse et session du poste qui fait le retour.
    employee_id             uuid NOT NULL REFERENCES employees (id),
    register_account_id     uuid NOT NULL REFERENCES register_accounts (id),
    cash_session_id         uuid NOT NULL REFERENCES cash_sessions (id),
    -- ESPECES : sortie de la caisse de cette session. TPE : tracé, sans sortie d'espèces.
    refund_method           text CHECK (refund_method IN ('ESPECES', 'TPE')),
    refund_amount           numeric(12, 2) NOT NULL CHECK (refund_amount >= 0),
    created_at              timestamptz NOT NULL DEFAULT now(),
    operation_id            uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    business_date           date,
    UNIQUE (store_id, return_seq),
    CHECK ((stock_destination = 'AUCUNE') = (destination_location_id IS NULL)),
    CHECK (refund_method IS NOT NULL OR refund_amount = 0)
);

CREATE INDEX returns_sale ON returns (sale_id);
CREATE INDEX returns_store_created ON returns (store_id, created_at DESC);
CREATE INDEX returns_session ON returns (cash_session_id);

CREATE TABLE return_lines (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    return_id          uuid NOT NULL REFERENCES returns (id),
    line_number        integer NOT NULL CHECK (line_number > 0),
    sale_line_id       bigint NOT NULL REFERENCES sale_lines (id),
    product_id         uuid NOT NULL REFERENCES products (id),
    product_name       text NOT NULL,
    quantity           numeric(12, 3) NOT NULL CHECK (quantity > 0),
    unit_price         numeric(12, 2) NOT NULL CHECK (unit_price >= 0),
    -- Part du remboursement due à cette ligne (remise de la vente déduite).
    refund_amount      numeric(12, 2) NOT NULL CHECK (refund_amount >= 0),
    -- Quantité remise en stock : toute la quantité, ou rien si le motif ne réintègre pas.
    -- La quantité non réintégrée (quantity − restocked_quantity) est sortie du circuit.
    restocked_quantity numeric(12, 3) NOT NULL CHECK (restocked_quantity IN (0, quantity)),
    UNIQUE (return_id, line_number),
    UNIQUE (return_id, sale_line_id)
);

CREATE INDEX return_lines_sale_line ON return_lines (sale_line_id);

CREATE TRIGGER return_lines_append_only
    BEFORE UPDATE OR DELETE ON return_lines
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER return_lines_no_truncate
    BEFORE TRUNCATE ON return_lines
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

-- Un retour ne se supprime pas et ne se modifie pas : seule sa journée comptable est
-- renseignée, une fois, par la clôture.
CREATE FUNCTION protect_return() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Un retour ne peut pas être supprimé.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF (to_jsonb(NEW) - 'business_date') IS DISTINCT FROM (to_jsonb(OLD) - 'business_date')
       OR OLD.business_date IS NOT NULL THEN
        RAISE EXCEPTION 'Un retour enregistré ne peut pas être modifié.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER returns_protected
    BEFORE UPDATE OR DELETE ON returns
    FOR EACH ROW EXECUTE FUNCTION protect_return();

-- ---------- Fermeture d'une caisse ----------

ALTER TABLE cash_sessions
    -- Espèces attendues calculées par le serveur à la fermeture, et écart avec le comptage.
    ADD COLUMN expected_cash      numeric(12, 2),
    ADD COLUMN cash_difference    numeric(12, 2),
    ADD COLUMN close_operation_id uuid UNIQUE REFERENCES operations (operation_id),
    ADD CONSTRAINT cash_sessions_closed_complete CHECK (
        (status = 'FERMEE') = (closed_by IS NOT NULL AND counted_cash IS NOT NULL AND expected_cash IS NOT NULL
                               AND cash_difference IS NOT NULL AND close_operation_id IS NOT NULL)),
    ADD CONSTRAINT cash_sessions_open_blank CHECK (
        status = 'FERMEE' OR (closed_by IS NULL AND counted_cash IS NULL AND expected_cash IS NULL
                              AND cash_difference IS NULL AND close_operation_id IS NULL AND business_date IS NULL)),
    ADD CONSTRAINT cash_sessions_difference CHECK (cash_difference = counted_cash - expected_cash);

-- Une session ne se supprime pas ; son ouverture ne change jamais ; une fois fermée, elle ne
-- se rouvre pas et seule sa journée comptable est renseignée, une fois, par la clôture.
CREATE FUNCTION protect_cash_session() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Une session de caisse ne peut pas être supprimée.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF (NEW.id, NEW.register_account_id, NEW.store_id, NEW.opening_float, NEW.opened_by, NEW.opened_at, NEW.operation_id)
       IS DISTINCT FROM
       (OLD.id, OLD.register_account_id, OLD.store_id, OLD.opening_float, OLD.opened_by, OLD.opened_at, OLD.operation_id) THEN
        RAISE EXCEPTION 'L''ouverture d''une session de caisse ne peut pas être modifiée.'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF OLD.status = 'FERMEE'
       AND ((to_jsonb(NEW) - 'business_date') IS DISTINCT FROM (to_jsonb(OLD) - 'business_date')
            OR OLD.business_date IS NOT NULL) THEN
        RAISE EXCEPTION 'Une session de caisse fermée ne peut plus être modifiée.'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER cash_sessions_protected
    BEFORE UPDATE OR DELETE ON cash_sessions
    FOR EACH ROW EXECUTE FUNCTION protect_cash_session();

-- ---------- Opérations de secours en attente de synchronisation ----------

-- Le mode secours n'est pas développé (décision réservée à la direction, O4). Cette table
-- n'existe que pour le garde-fou de la clôture : tant qu'une ligne non synchronisée existe
-- pour un magasin, sa journée ne peut pas être présentée comme consolidée. Aujourd'hui,
-- rien n'y écrit : elle est toujours vide.
CREATE TABLE offline_pending_operations (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id    uuid NOT NULL REFERENCES stores (id),
    description text,
    reported_at timestamptz NOT NULL DEFAULT now(),
    -- Renseigné quand l'opération a été reçue et rapprochée par le serveur.
    synced_at   timestamptz
);

CREATE INDEX offline_pending_operations_store ON offline_pending_operations (store_id) WHERE synced_at IS NULL;

-- ---------- Clôture quotidienne ----------

CREATE TABLE daily_closures (
    id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id                   uuid NOT NULL REFERENCES stores (id),
    business_date              date NOT NULL,
    closed_by                  uuid NOT NULL REFERENCES employees (id),
    register_account_id        uuid NOT NULL REFERENCES register_accounts (id),
    closed_at                  timestamptz NOT NULL DEFAULT now(),
    operation_id               uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    -- Procédure exceptionnelle : clôture faite alors que des opérations de secours restaient
    -- inconnues du serveur. Le rapport le signale.
    forced                     boolean NOT NULL DEFAULT false,
    force_reason               text,
    unknown_offline_operations integer NOT NULL DEFAULT 0 CHECK (unknown_offline_operations >= 0),
    -- Rapport calculé une fois, à la clôture.
    report                     jsonb NOT NULL,
    -- Une seule clôture par magasin et par jour.
    UNIQUE (store_id, business_date),
    CHECK (forced = (force_reason IS NOT NULL AND btrim(force_reason) <> '')),
    CHECK (forced = (unknown_offline_operations > 0))
);

-- Une clôture et son rapport sont figés : ni modification ni suppression.
CREATE TRIGGER daily_closures_append_only
    BEFORE UPDATE OR DELETE ON daily_closures
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER daily_closures_no_truncate
    BEFORE TRUNCATE ON daily_closures
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

-- ---------- Journée comptable figée ----------

-- Une fois renseignée par la clôture, la journée comptable d'une opération ne change plus.
CREATE FUNCTION protect_business_date() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.business_date IS NOT NULL AND NEW.business_date IS DISTINCT FROM OLD.business_date THEN
        RAISE EXCEPTION 'La journée comptable de % est clôturée : elle ne peut plus changer.', TG_TABLE_NAME
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER sales_business_date_frozen
    BEFORE UPDATE ON sales FOR EACH ROW EXECUTE FUNCTION protect_business_date();
CREATE TRIGGER payments_business_date_frozen
    BEFORE UPDATE ON payments FOR EACH ROW EXECUTE FUNCTION protect_business_date();
CREATE TRIGGER receivable_settlements_business_date_frozen
    BEFORE UPDATE ON receivable_settlements FOR EACH ROW EXECUTE FUNCTION protect_business_date();
CREATE TRIGGER cash_movements_business_date_frozen
    BEFORE UPDATE ON cash_movements FOR EACH ROW EXECUTE FUNCTION protect_business_date();
CREATE TRIGGER sale_cancellations_business_date_frozen
    BEFORE UPDATE ON sale_cancellations FOR EACH ROW EXECUTE FUNCTION protect_business_date();

CREATE INDEX sales_open_day ON sales (store_id) WHERE business_date IS NULL;
CREATE INDEX payments_open_day ON payments (store_id) WHERE business_date IS NULL;
CREATE INDEX receivable_settlements_open_day ON receivable_settlements (store_id) WHERE business_date IS NULL;
CREATE INDEX cash_movements_open_day ON cash_movements (store_id) WHERE business_date IS NULL;
CREATE INDEX sale_cancellations_open_day ON sale_cancellations (store_id) WHERE business_date IS NULL;
CREATE INDEX returns_open_day ON returns (store_id) WHERE business_date IS NULL;
CREATE INDEX cash_sessions_open_day ON cash_sessions (store_id) WHERE business_date IS NULL;
