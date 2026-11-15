-- Socle : sites, emplacements et catalogue produits.
-- Le stock, les bons et les ventes arrivent dans les migrations suivantes.

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE stores (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code       text NOT NULL UNIQUE,
    name       text NOT NULL,
    kind       text NOT NULL CHECK (kind IN ('MAGASIN', 'DEPOT')),
    is_active  boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE stock_locations (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_id   uuid NOT NULL REFERENCES stores (id),
    code       text NOT NULL,
    name       text NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (store_id, code)
);

CREATE TABLE brands (
    id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE
);

CREATE TABLE categories (
    id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE
);

CREATE SEQUENCE product_internal_code_seq START WITH 100000;

CREATE TABLE products (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Identifiant interne lisible, unique et stable, attribué par Pharmaly Gestion.
    internal_code   bigint NOT NULL UNIQUE DEFAULT nextval('product_internal_code_seq'),
    name            text NOT NULL CHECK (btrim(name) <> ''),
    -- Libellé normalisé (minuscules, sans accents) pour la recherche.
    search_name     text NOT NULL,
    brand_id        uuid REFERENCES brands (id),
    category_id     uuid REFERENCES categories (id),
    sale_price      numeric(12, 2) NOT NULL CHECK (sale_price >= 0),
    -- Vrai : vendu uniquement à l'unité, les quantités fractionnaires sont refusées.
    sold_by_unit    boolean NOT NULL DEFAULT true,
    -- Ancien code BML, conservé pour la reprise. Volontairement non unique.
    legacy_bml_code text,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX products_search_name_trgm ON products USING gin (search_name gin_trgm_ops);
CREATE INDEX products_legacy_bml_code ON products (legacy_bml_code) WHERE legacy_bml_code IS NOT NULL;

CREATE TABLE product_barcodes (
    -- Collation "C" : l'index de la clé primaire sert aussi aux recherches par début de code.
    barcode    text COLLATE "C" PRIMARY KEY CHECK (barcode ~ '^[0-9A-Za-z.-]{4,64}$'),
    product_id uuid NOT NULL REFERENCES products (id) ON DELETE CASCADE,
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX product_barcodes_product ON product_barcodes (product_id);
