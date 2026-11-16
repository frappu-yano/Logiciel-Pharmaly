-- Bons fournisseurs : fournisseurs, bons, lignes (avec numéro de version), historique
-- immuable des achats et historique des états et corrections de chaque bon.
-- Les quantités entrent en stock par le moteur de stock (journal stock_movements) dès
-- qu'une ligne est enregistrée ; rien ici ne porte un solde.

-- ---------- Fournisseurs ----------

CREATE TABLE suppliers (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name       text NOT NULL CHECK (btrim(name) <> ''),
    is_active  boolean NOT NULL DEFAULT true,
    created_by uuid REFERENCES employees (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Nom unique sans tenir compte de la casse.
CREATE UNIQUE INDEX suppliers_name_unique ON suppliers (lower(name));

-- ---------- Bons ----------

CREATE SEQUENCE supplier_note_number_seq START WITH 1;

CREATE TABLE supplier_notes (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Numéro lisible, unique et stable.
    number             bigint NOT NULL UNIQUE DEFAULT nextval('supplier_note_number_seq'),
    supplier_id        uuid NOT NULL REFERENCES suppliers (id),
    -- Emplacement où entre la marchandise.
    location_id        uuid NOT NULL REFERENCES stock_locations (id),
    supplier_reference text NOT NULL CHECK (btrim(supplier_reference) <> ''),
    note_date          date NOT NULL,
    status             text NOT NULL DEFAULT 'EN_SAISIE' CHECK (status IN ('EN_SAISIE', 'VALIDE', 'ANNULE')),
    created_by         uuid NOT NULL REFERENCES employees (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    validated_by       uuid REFERENCES employees (id),
    validated_at       timestamptz,
    cancelled_by       uuid REFERENCES employees (id),
    cancelled_at       timestamptz,
    -- Verrou à bail : un seul employé par bon à la fois. Passé « lock_expires_at », un
    -- autre employé peut prendre le bon ; le même employé le reprend à tout moment.
    lock_employee_id         uuid REFERENCES employees (id),
    lock_register_account_id uuid REFERENCES register_accounts (id),
    lock_acquired_at         timestamptz,
    lock_expires_at          timestamptz,
    CHECK ((lock_employee_id IS NULL) = (lock_register_account_id IS NULL)
       AND (lock_employee_id IS NULL) = (lock_acquired_at IS NULL)
       AND (lock_employee_id IS NULL) = (lock_expires_at IS NULL))
);

CREATE INDEX supplier_notes_supplier ON supplier_notes (supplier_id);
CREATE INDEX supplier_notes_date ON supplier_notes (note_date DESC, number DESC);
CREATE INDEX supplier_notes_status ON supplier_notes (status);

-- ---------- Lignes ----------

CREATE TABLE supplier_note_lines (
    id                      uuid PRIMARY KEY,
    note_id                 uuid NOT NULL REFERENCES supplier_notes (id),
    -- Rang de la ligne dans le bon, attribué à l'enregistrement.
    position                integer NOT NULL CHECK (position > 0),
    product_id              uuid NOT NULL REFERENCES products (id),
    -- Quantité entrée en stock par cette ligne (conservée telle quelle si la ligne est annulée).
    quantity                numeric(12, 3) NOT NULL CHECK (quantity > 0),
    purchase_price          numeric(12, 2) NOT NULL CHECK (purchase_price >= 0),
    -- Dernier prix d'achat connu du produit au moment de l'enregistrement de la ligne.
    previous_purchase_price numeric(12, 2),
    -- Augmente à chaque modification : une demande fondée sur une version périmée est refusée.
    version                 integer NOT NULL DEFAULT 1 CHECK (version > 0),
    is_cancelled            boolean NOT NULL DEFAULT false,
    created_by              uuid NOT NULL REFERENCES employees (id),
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              uuid REFERENCES employees (id),
    updated_at              timestamptz,
    UNIQUE (note_id, position)
);

CREATE INDEX supplier_note_lines_product ON supplier_note_lines (product_id);

-- ---------- Historique des achats (ajout seul) ----------

-- Une entrée par enregistrement, correction ou annulation d'une ligne. Une correction de
-- prix AJOUTE une entrée : les anciennes ne sont jamais réécrites.
CREATE TABLE purchase_history (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id     uuid NOT NULL REFERENCES products (id),
    supplier_id    uuid NOT NULL REFERENCES suppliers (id),
    note_id        uuid NOT NULL REFERENCES supplier_notes (id),
    line_id        uuid NOT NULL REFERENCES supplier_note_lines (id),
    note_date      date NOT NULL,
    kind           text NOT NULL CHECK (kind IN ('ACHAT', 'CORRECTION', 'ANNULATION')),
    purchase_price numeric(12, 2) NOT NULL CHECK (purchase_price >= 0),
    -- Quantité de la ligne après l'événement (0 pour une annulation).
    quantity       numeric(12, 3) NOT NULL CHECK (quantity >= 0),
    employee_id    uuid NOT NULL REFERENCES employees (id),
    operation_id   uuid NOT NULL REFERENCES operations (operation_id),
    recorded_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX purchase_history_product ON purchase_history (product_id, id);
CREATE INDEX purchase_history_line ON purchase_history (line_id, id);

CREATE TRIGGER purchase_history_append_only
    BEFORE UPDATE OR DELETE ON purchase_history
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER purchase_history_no_truncate
    BEFORE TRUNCATE ON purchase_history
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

-- ---------- Historique des états et des corrections d'un bon (ajout seul) ----------

CREATE TABLE supplier_note_history (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    note_id       uuid NOT NULL REFERENCES supplier_notes (id),
    event         text NOT NULL CHECK (event IN (
        'CREATION', 'LIGNE_AJOUTEE', 'LIGNE_MODIFIEE', 'LIGNE_ANNULEE', 'VALIDATION', 'ANNULATION', 'VERROU_FORCE')),
    status_before text,
    status_after  text NOT NULL,
    line_id       uuid REFERENCES supplier_note_lines (id),
    -- Phrase lisible par l'employé ; « details » garde les valeurs avant et après.
    description   text NOT NULL,
    details       jsonb NOT NULL DEFAULT '{}'::jsonb,
    employee_id   uuid NOT NULL REFERENCES employees (id),
    -- Absent pour une libération forcée du verrou, qui ne porte pas d'identifiant d'opération.
    operation_id  uuid REFERENCES operations (operation_id),
    occurred_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX supplier_note_history_note ON supplier_note_history (note_id, id);

CREATE TRIGGER supplier_note_history_append_only
    BEFORE UPDATE OR DELETE ON supplier_note_history
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER supplier_note_history_no_truncate
    BEFORE TRUNCATE ON supplier_note_history
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();
