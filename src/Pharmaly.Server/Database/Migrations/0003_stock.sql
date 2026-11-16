-- Stock : emplacements vendables ou mis à l'écart, soldes, journal des mouvements en
-- ajout seul, idempotence des commandes et historique des prix de vente.

-- ---------- Emplacements ----------

ALTER TABLE stock_locations
    ADD COLUMN kind text NOT NULL DEFAULT 'VENDABLE' CHECK (kind IN ('VENDABLE', 'ECARTE'));

-- Les sites déjà créés reçoivent leur emplacement des produits mis à l'écart.
INSERT INTO stock_locations (store_id, code, name, kind)
SELECT id, 'ECARTE', 'Produits mis à l''écart', 'ECARTE' FROM stores
ON CONFLICT (store_id, code) DO NOTHING;

-- ---------- Idempotence ----------

-- Une ligne par commande d'écriture, inscrite dans la transaction de la commande.
-- Rejouer le même identifiant renvoie « result » sans rien refaire.
CREATE TABLE operations (
    operation_id uuid PRIMARY KEY,
    kind         text NOT NULL,
    employee_id  uuid REFERENCES employees (id),
    -- Empreinte de la demande : le même identifiant ne peut pas servir à une autre demande.
    request_hash text NOT NULL,
    -- Renseigné avant la fin de la transaction : une ligne validée a toujours son résultat.
    result       jsonb,
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- ---------- Soldes ----------

CREATE TABLE stock_levels (
    product_id  uuid NOT NULL REFERENCES products (id),
    location_id uuid NOT NULL REFERENCES stock_locations (id),
    -- Jamais négatif : la survente est impossible, même si le logiciel a un défaut.
    quantity    numeric(12, 3) NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (product_id, location_id)
);

CREATE INDEX stock_levels_location ON stock_levels (location_id);

-- ---------- Journal des mouvements ----------

CREATE TABLE stock_movements (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id    uuid NOT NULL,
    location_id   uuid NOT NULL,
    -- Positive : entrée ; négative : sortie.
    quantity      numeric(12, 3) NOT NULL CHECK (quantity <> 0),
    movement_type text NOT NULL CHECK (movement_type IN (
        'ENTREE_BON', 'CORRECTION_BON', 'ANNULATION_BON', 'VENTE', 'ANNULATION_VENTE', 'RETOUR',
        'TRANSFERT_SORTIE', 'TRANSFERT_ENTREE', 'AJUSTEMENT')),
    -- Document à l'origine du mouvement (bon, vente, transfert, ajustement…).
    source_type   text,
    source_id     text,
    employee_id   uuid NOT NULL REFERENCES employees (id),
    operation_id  uuid NOT NULL REFERENCES operations (operation_id),
    note          text,
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    -- Solde de l'emplacement juste après ce mouvement.
    balance_after numeric(12, 3) NOT NULL CHECK (balance_after >= 0),
    FOREIGN KEY (product_id, location_id) REFERENCES stock_levels (product_id, location_id)
);

CREATE INDEX stock_movements_level ON stock_movements (product_id, location_id, id);
CREATE INDEX stock_movements_operation ON stock_movements (operation_id);
CREATE INDEX stock_movements_source ON stock_movements (source_type, source_id) WHERE source_id IS NOT NULL;

-- ---------- Historique des prix de vente ----------

CREATE TABLE product_sale_prices (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id   uuid NOT NULL REFERENCES products (id),
    -- NULL : prix fixé à la création du produit.
    old_price    numeric(12, 2),
    new_price    numeric(12, 2) NOT NULL CHECK (new_price >= 0),
    employee_id  uuid NOT NULL REFERENCES employees (id),
    operation_id uuid NOT NULL REFERENCES operations (operation_id),
    changed_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX product_sale_prices_product ON product_sale_prices (product_id, id);

-- ---------- Ajout seul ----------

-- Ni modification ni suppression d'une ligne. Le vidage n'est toléré que sur une table
-- vide (il n'efface alors rien) : dès qu'une ligne existe, il est refusé.
CREATE FUNCTION forbid_history_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    has_rows boolean;
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I.%I)', TG_TABLE_SCHEMA, TG_TABLE_NAME) INTO has_rows;
        IF NOT has_rows THEN
            RETURN NULL;
        END IF;
    END IF;

    RAISE EXCEPTION 'La table % est un historique : ni modification ni suppression.', TG_TABLE_NAME
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;

CREATE TRIGGER stock_movements_append_only
    BEFORE UPDATE OR DELETE ON stock_movements
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER stock_movements_no_truncate
    BEFORE TRUNCATE ON stock_movements
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER product_sale_prices_append_only
    BEFORE UPDATE OR DELETE ON product_sale_prices
    FOR EACH ROW EXECUTE FUNCTION forbid_history_change();

CREATE TRIGGER product_sale_prices_no_truncate
    BEFORE TRUNCATE ON product_sale_prices
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_history_change();

-- ---------- Jamais de fraction sur un produit vendu à l'unité ----------

CREATE FUNCTION forbid_fraction_for_unit_product() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.quantity <> trunc(NEW.quantity)
       AND (SELECT sold_by_unit FROM products WHERE id = NEW.product_id) THEN
        RAISE EXCEPTION 'Quantité fractionnaire interdite pour un produit vendu à l''unité.'
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER stock_levels_no_fraction
    BEFORE INSERT OR UPDATE OF quantity ON stock_levels
    FOR EACH ROW EXECUTE FUNCTION forbid_fraction_for_unit_product();

CREATE TRIGGER stock_movements_no_fraction
    BEFORE INSERT ON stock_movements
    FOR EACH ROW EXECUTE FUNCTION forbid_fraction_for_unit_product();

-- Un produit ne devient pas « vendu à l'unité » tant qu'il reste un solde fractionnaire.
CREATE FUNCTION forbid_unit_sale_with_fractional_stock() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.sold_by_unit AND NOT OLD.sold_by_unit
       AND EXISTS (SELECT 1 FROM stock_levels
                   WHERE product_id = NEW.id AND quantity <> trunc(quantity)) THEN
        RAISE EXCEPTION 'Ce produit a un stock fractionnaire : il ne peut pas devenir vendu à l''unité.'
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER products_unit_sale_needs_whole_stock
    BEFORE UPDATE OF sold_by_unit ON products
    FOR EACH ROW EXECUTE FUNCTION forbid_unit_sale_with_fractional_stock();
