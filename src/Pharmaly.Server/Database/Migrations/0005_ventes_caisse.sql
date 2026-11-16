-- Ventes, caisse et impayés : clients, sessions de caisse, tickets, paiements, créances,
-- règlements (éventuellement groupés) et mouvements d'espèces.
--
-- Journée comptable (D11) : chaque ligne d'argent porte une colonne « business_date »,
-- NULL tant que la journée n'est pas clôturée. La clôture quotidienne (par magasin et par
-- jour) la renseignera ; une vente dont la journée est clôturée ne s'annule plus (D13).
--
-- Chiffre d'affaires et trésorerie sont séparés dans les données :
--   * « sales » porte le chiffre d'affaires, au jour de la vente ;
--   * « payments » = encaissements des ventes au comptant ;
--   * « receivable_settlements » = encaissements d'anciennes créances (trésorerie du jour
--     du règlement, jamais du chiffre d'affaires) ;
--   * « cash_movements » = entrées, sorties et remboursements en espèces hors vente.

-- ---------- Clients ----------

CREATE TABLE customers (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL CHECK (btrim(name) <> ''),
    -- Nom normalisé (minuscules, sans accents) pour la recherche.
    search_name text NOT NULL,
    phone       text,
    created_by  uuid NOT NULL REFERENCES employees (id),
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX customers_search_name_trgm ON customers USING gin (search_name gin_trgm_ops);
CREATE INDEX customers_phone ON customers (phone) WHERE phone IS NOT NULL;

-- ---------- Sessions de caisse ----------

CREATE TABLE cash_sessions (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    store_id            uuid NOT NULL REFERENCES stores (id),
    status              text NOT NULL DEFAULT 'OUVERTE' CHECK (status IN ('OUVERTE', 'FERMEE')),
    -- Fond de caisse compté à l'ouverture.
    opening_float       numeric(12, 2) NOT NULL CHECK (opening_float >= 0),
    opened_by           uuid NOT NULL REFERENCES employees (id),
    opened_at           timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    -- Renseignés par la clôture (lot suivant).
    closed_by           uuid REFERENCES employees (id),
    closed_at           timestamptz,
    counted_cash        numeric(12, 2) CHECK (counted_cash >= 0),
    business_date       date,
    CHECK ((status = 'FERMEE') = (closed_at IS NOT NULL))
);

-- Une seule session ouverte par compte de caisse.
CREATE UNIQUE INDEX cash_sessions_one_open ON cash_sessions (register_account_id) WHERE status = 'OUVERTE';
CREATE INDEX cash_sessions_store ON cash_sessions (store_id, opened_at);

-- ---------- Tickets ----------

-- Compteur de tickets par site : incrémenté dans la transaction de la vente, donc sans
-- trou et jamais réutilisé.
CREATE TABLE ticket_counters (
    store_id    uuid PRIMARY KEY REFERENCES stores (id),
    last_number bigint NOT NULL DEFAULT 0 CHECK (last_number >= 0)
);

-- Valeur du code-barres imprimé sur le ticket : « TK » + 10 chiffres, unique sur toute la base.
CREATE SEQUENCE ticket_barcode_seq START WITH 1;

CREATE TABLE sales (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id            uuid NOT NULL REFERENCES stores (id),
    ticket_seq          bigint NOT NULL CHECK (ticket_seq > 0),
    -- Numéro lisible, préfixé du code du site : « D1-000042 ».
    ticket_number       text NOT NULL UNIQUE,
    ticket_barcode      text NOT NULL UNIQUE CHECK (ticket_barcode ~ '^TK[0-9]{10,}$'),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    cash_session_id     uuid NOT NULL REFERENCES cash_sessions (id),
    -- Employé réellement identifié au moment de la vente.
    employee_id         uuid NOT NULL REFERENCES employees (id),
    customer_id         uuid REFERENCES customers (id),
    payment_mode        text NOT NULL CHECK (payment_mode IN ('ESPECES', 'TPE', 'IMPAYE')),
    status              text NOT NULL CHECK (status IN ('VALIDEE', 'EN_ATTENTE_TPE', 'ANNULEE')),
    subtotal            numeric(12, 2) NOT NULL CHECK (subtotal >= 0),
    discount_kind       text CHECK (discount_kind IN ('MONTANT', 'POURCENTAGE')),
    -- Valeur saisie : un montant en dinars ou un pourcentage.
    discount_value      numeric(12, 2) CHECK (discount_value > 0),
    discount_amount     numeric(12, 2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    total               numeric(12, 2) NOT NULL CHECK (total >= 0),
    created_at          timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    business_date       date,
    UNIQUE (store_id, ticket_seq),
    CHECK (discount_amount <= subtotal),
    CHECK (total = subtotal - discount_amount),
    CHECK ((discount_kind IS NULL) = (discount_value IS NULL)),
    CHECK (discount_kind IS NOT NULL OR discount_amount = 0),
    CHECK (discount_kind <> 'POURCENTAGE' OR discount_value <= 100),
    -- Vente impayée : client obligatoire (D5).
    CHECK (payment_mode <> 'IMPAYE' OR customer_id IS NOT NULL),
    CHECK (status <> 'EN_ATTENTE_TPE' OR payment_mode = 'TPE')
);

CREATE INDEX sales_store_created ON sales (store_id, created_at DESC);
CREATE INDEX sales_employee ON sales (employee_id, created_at DESC);
CREATE INDEX sales_customer ON sales (customer_id) WHERE customer_id IS NOT NULL;
CREATE INDEX sales_session ON sales (cash_session_id);

CREATE TABLE sale_lines (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sale_id      uuid NOT NULL REFERENCES sales (id),
    line_number  integer NOT NULL CHECK (line_number > 0),
    product_id   uuid NOT NULL REFERENCES products (id),
    -- Désignation et prix au moment de la vente : le ticket ne change pas si le produit change.
    product_name text NOT NULL,
    location_id  uuid NOT NULL REFERENCES stock_locations (id),
    quantity     numeric(12, 3) NOT NULL CHECK (quantity > 0),
    unit_price   numeric(12, 2) NOT NULL CHECK (unit_price >= 0),
    line_total   numeric(12, 2) NOT NULL CHECK (line_total >= 0),
    UNIQUE (sale_id, line_number),
    -- Un produit figure une seule fois sur un ticket : un seul mouvement VENTE par produit.
    UNIQUE (sale_id, product_id)
);

-- Les lignes d'un ticket ne changent jamais.
CREATE TRIGGER sale_lines_append_only
    BEFORE UPDATE OR DELETE ON sale_lines
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER sale_lines_no_truncate
    BEFORE TRUNCATE ON sale_lines
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

-- Un ticket ne se supprime pas, et ni son numéro, ni ses montants, ni son auteur ne changent.
-- Seuls l'état (annulation, réponse du TPE) et la journée comptable évoluent.
CREATE FUNCTION protect_sale() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Un ticket ne peut pas être supprimé.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF (NEW.store_id, NEW.ticket_seq, NEW.ticket_number, NEW.ticket_barcode, NEW.register_account_id,
        NEW.cash_session_id, NEW.employee_id, NEW.payment_mode, NEW.subtotal, NEW.discount_amount, NEW.total,
        NEW.created_at, NEW.operation_id)
       IS DISTINCT FROM
       (OLD.store_id, OLD.ticket_seq, OLD.ticket_number, OLD.ticket_barcode, OLD.register_account_id,
        OLD.cash_session_id, OLD.employee_id, OLD.payment_mode, OLD.subtotal, OLD.discount_amount, OLD.total,
        OLD.created_at, OLD.operation_id)
       OR NEW.customer_id IS DISTINCT FROM OLD.customer_id
       OR NEW.discount_kind IS DISTINCT FROM OLD.discount_kind
       OR NEW.discount_value IS DISTINCT FROM OLD.discount_value THEN
        RAISE EXCEPTION 'Un ticket enregistré ne peut pas être modifié.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF OLD.status = 'ANNULEE' AND NEW.status <> 'ANNULEE' THEN
        RAISE EXCEPTION 'Une vente annulée le reste.' USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER sales_protected
    BEFORE UPDATE OR DELETE ON sales
    FOR EACH ROW EXECUTE FUNCTION protect_sale();

-- ---------- Annulations de vente ----------

-- Opération compensatoire reliée à la vente. La clé primaire rend la double annulation impossible.
CREATE TABLE sale_cancellations (
    sale_id             uuid PRIMARY KEY REFERENCES sales (id),
    -- ANNULATION : décidée par un employé autorisé. TPE_REFUSE : paiement par carte refusé.
    kind                text NOT NULL CHECK (kind IN ('ANNULATION', 'TPE_REFUSE')),
    reason              text NOT NULL CHECK (btrim(reason) <> ''),
    employee_id         uuid NOT NULL REFERENCES employees (id),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    store_id            uuid NOT NULL REFERENCES stores (id),
    -- Session de caisse d'où sort le remboursement, s'il y en a un.
    cash_session_id     uuid REFERENCES cash_sessions (id),
    refund_method       text CHECK (refund_method IN ('ESPECES', 'TPE')),
    refund_amount       numeric(12, 2) NOT NULL DEFAULT 0 CHECK (refund_amount >= 0),
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    business_date       date,
    CHECK (refund_method IS NOT NULL OR refund_amount = 0),
    CHECK (refund_method IS NULL OR cash_session_id IS NOT NULL)
);

-- ---------- Encaissements des ventes au comptant ----------

CREATE TABLE payments (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Un seul paiement par vente : un TPE refusé annule la vente, on ne la repaie pas.
    sale_id             uuid NOT NULL UNIQUE REFERENCES sales (id),
    method              text NOT NULL CHECK (method IN ('ESPECES', 'TPE')),
    -- REMBOURSE : encaissé, puis remboursé par l'annulation de la vente.
    status              text NOT NULL CHECK (status IN ('EN_ATTENTE', 'CONFIRME', 'ECHOUE', 'REMBOURSE')),
    amount              numeric(12, 2) NOT NULL CHECK (amount >= 0),
    -- Espèces : montant remis par le client et monnaie rendue.
    amount_received     numeric(12, 2),
    change_given        numeric(12, 2),
    cash_session_id     uuid NOT NULL REFERENCES cash_sessions (id),
    store_id            uuid NOT NULL REFERENCES stores (id),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    -- Employé qui encaisse (espèces) ou qui lance le paiement par carte.
    employee_id         uuid NOT NULL REFERENCES employees (id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL REFERENCES operations (operation_id),
    -- Date de l'encaissement effectif (espèces : à la vente ; TPE : à « accepté »).
    confirmed_at        timestamptz,
    -- Réponse du TPE : qui l'a saisie, quand, par quelle opération.
    resolved_by         uuid REFERENCES employees (id),
    resolved_at         timestamptz,
    resolve_operation_id uuid REFERENCES operations (operation_id),
    business_date       date,
    CHECK (method <> 'ESPECES' OR (amount_received IS NOT NULL AND amount_received >= amount
                                   AND change_given = amount_received - amount
                                   AND status IN ('CONFIRME', 'REMBOURSE'))),
    CHECK (method <> 'TPE' OR (amount_received IS NULL AND change_given IS NULL AND amount > 0)),
    CHECK ((status IN ('CONFIRME', 'REMBOURSE')) = (confirmed_at IS NOT NULL)),
    CHECK (status <> 'ECHOUE' OR resolved_at IS NOT NULL)
);

CREATE INDEX payments_session ON payments (cash_session_id);
CREATE INDEX payments_pending ON payments (store_id) WHERE status = 'EN_ATTENTE';

-- ---------- Créances ----------

CREATE TABLE receivables (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Une créance par vente impayée.
    sale_id      uuid NOT NULL UNIQUE REFERENCES sales (id),
    customer_id  uuid NOT NULL REFERENCES customers (id),
    store_id     uuid NOT NULL REFERENCES stores (id),
    amount       numeric(12, 2) NOT NULL CHECK (amount > 0),
    status       text NOT NULL DEFAULT 'OUVERTE' CHECK (status IN ('OUVERTE', 'SOLDEE', 'ANNULEE')),
    created_at   timestamptz NOT NULL DEFAULT now(),
    settled_at   timestamptz,
    cancelled_at timestamptz,
    CHECK ((status = 'SOLDEE') = (settled_at IS NOT NULL)),
    CHECK ((status = 'ANNULEE') = (cancelled_at IS NOT NULL))
);

CREATE INDEX receivables_customer ON receivables (customer_id, status);
CREATE INDEX receivables_status ON receivables (status, created_at DESC);

-- Un règlement = un seul encaissement, pour une ou plusieurs créances du même client.
CREATE TABLE receivable_settlements (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id         uuid NOT NULL REFERENCES customers (id),
    method              text NOT NULL CHECK (method IN ('ESPECES', 'TPE')),
    status              text NOT NULL CHECK (status IN ('EN_ATTENTE', 'CONFIRME', 'ECHOUE')),
    -- Exactement la somme des créances réglées.
    amount              numeric(12, 2) NOT NULL CHECK (amount > 0),
    amount_received     numeric(12, 2),
    change_given        numeric(12, 2),
    -- Session, site et employé du poste qui encaisse (pas ceux de la vente d'origine).
    cash_session_id     uuid NOT NULL REFERENCES cash_sessions (id),
    store_id            uuid NOT NULL REFERENCES stores (id),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    employee_id         uuid NOT NULL REFERENCES employees (id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL UNIQUE REFERENCES operations (operation_id),
    confirmed_at        timestamptz,
    resolved_by         uuid REFERENCES employees (id),
    resolved_at         timestamptz,
    resolve_operation_id uuid REFERENCES operations (operation_id),
    business_date       date,
    CHECK (method <> 'ESPECES' OR (amount_received IS NOT NULL AND amount_received >= amount
                                   AND change_given = amount_received - amount AND status = 'CONFIRME')),
    CHECK (method <> 'TPE' OR (amount_received IS NULL AND change_given IS NULL)),
    CHECK ((status = 'CONFIRME') = (confirmed_at IS NOT NULL)),
    CHECK (status <> 'ECHOUE' OR resolved_at IS NOT NULL)
);

CREATE INDEX receivable_settlements_session ON receivable_settlements (cash_session_id);
CREATE INDEX receivable_settlements_customer ON receivable_settlements (customer_id, created_at DESC);

CREATE TABLE receivable_settlement_items (
    settlement_id uuid NOT NULL REFERENCES receivable_settlements (id),
    receivable_id uuid NOT NULL REFERENCES receivables (id),
    amount        numeric(12, 2) NOT NULL CHECK (amount > 0),
    -- Faux uniquement quand le règlement a échoué (TPE refusé) : la créance est alors libérée.
    is_active     boolean NOT NULL DEFAULT true,
    PRIMARY KEY (settlement_id, receivable_id)
);

-- Une créance figure dans au plus un règlement en attente ou confirmé : le double règlement
-- est refusé par la base, y compris pendant qu'un paiement par carte attend sa réponse.
CREATE UNIQUE INDEX receivable_settlement_items_one_active
    ON receivable_settlement_items (receivable_id) WHERE is_active;

-- ---------- Mouvements d'espèces hors encaissement ----------

CREATE TABLE cash_movements (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    cash_session_id     uuid NOT NULL REFERENCES cash_sessions (id),
    store_id            uuid NOT NULL REFERENCES stores (id),
    register_account_id uuid NOT NULL REFERENCES register_accounts (id),
    employee_id         uuid NOT NULL REFERENCES employees (id),
    -- ENTREE et SORTIE : hors vente. REMBOURSEMENT_VENTE : espèces rendues pour une vente annulée.
    kind                text NOT NULL CHECK (kind IN ('ENTREE', 'SORTIE', 'REMBOURSEMENT_VENTE')),
    -- Toujours positif ; le sens vient de « kind ».
    amount              numeric(12, 2) NOT NULL CHECK (amount > 0),
    reason              text NOT NULL CHECK (btrim(reason) <> ''),
    sale_id             uuid REFERENCES sales (id),
    occurred_at         timestamptz NOT NULL DEFAULT now(),
    operation_id        uuid NOT NULL REFERENCES operations (operation_id),
    business_date       date,
    CHECK ((kind = 'REMBOURSEMENT_VENTE') = (sale_id IS NOT NULL))
);

CREATE INDEX cash_movements_session ON cash_movements (cash_session_id, occurred_at);
-- Une vente annulée est remboursée une seule fois.
CREATE UNIQUE INDEX cash_movements_one_refund_per_sale ON cash_movements (sale_id) WHERE sale_id IS NOT NULL;
