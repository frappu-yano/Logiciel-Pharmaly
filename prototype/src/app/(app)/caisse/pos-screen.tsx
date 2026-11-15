"use client";

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { PaymentMethod } from "@prisma/client";
import {
  createSale,
  findProductByBarcode,
  searchProducts,
  type PosProduct,
} from "@/actions/sales";
import { PAYMENT_LABELS, PAYMENT_METHODS } from "@/lib/labels";
import { formatMoney, parseMoney } from "@/lib/money";
import { buttonPrimary, buttonSecondary, inputClass, labelClass } from "@/components/ui";

type CartLine = { product: PosProduct; quantity: number };

type CompletedSale = { saleId: number; number: string; total: number; change: number };

const SEARCH_DEBOUNCE_MS = 200;
const AUTO_PRINT_KEY = "pharmaly.autoPrint";

const AUTO_PRINT_EVENT = "pharmaly:auto-print";

const subscribeAutoPrint = (onChange: () => void) => {
  window.addEventListener(AUTO_PRINT_EVENT, onChange);
  return () => window.removeEventListener(AUTO_PRINT_EVENT, onChange);
};

const readAutoPrint = (): boolean => {
  try {
    return window.localStorage.getItem(AUTO_PRINT_KEY) !== "0";
  } catch {
    return true;
  }
};

const isEditable = (element: Element | null): boolean =>
  element instanceof HTMLInputElement ||
  element instanceof HTMLTextAreaElement ||
  element instanceof HTMLSelectElement;

export const PosScreen = () => {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PosProduct[]>([]);
  const [cart, setCart] = useState<CartLine[]>([]);
  const [discountInput, setDiscountInput] = useState("");
  const [method, setMethod] = useState<PaymentMethod>("ESPECES");
  const [receivedInput, setReceivedInput] = useState("");
  const [customerName, setCustomerName] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [completed, setCompleted] = useState<CompletedSale | null>(null);
  const autoPrint = useSyncExternalStore(subscribeAutoPrint, readAutoPrint, () => true);
  const [printTarget, setPrintTarget] = useState<string | null>(null);

  const searchRef = useRef<HTMLInputElement>(null);
  const searchSequence = useRef(0);

  // Un lecteur de codes-barres se comporte comme un clavier : toute frappe hors
  // d'un champ est redirigée vers la recherche.
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.key.length !== 1) return;
      if (isEditable(document.activeElement)) return;
      searchRef.current?.focus();
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, []);

  useEffect(() => {
    const term = query.trim();
    if (!term) return;
    const sequence = ++searchSequence.current;
    const timer = window.setTimeout(async () => {
      const found = await searchProducts(term);
      if (sequence === searchSequence.current) setResults(found);
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [query]);

  const addToCart = useCallback((product: PosProduct) => {
    setCart((lines) => {
      const existing = lines.find((line) => line.product.id === product.id);
      if (!existing) return [...lines, { product, quantity: 1 }];
      return lines.map((line) =>
        line.product.id === product.id
          ? { ...line, quantity: line.quantity + 1 }
          : line,
      );
    });
    setQuery("");
    setResults([]);
    setError(null);
    setNotice(null);
    setCompleted(null);
    searchRef.current?.focus();
  }, []);

  const handleSearchSubmit = async () => {
    const term = query.trim();
    if (!term) return;
    searchSequence.current += 1;
    const byBarcode = await findProductByBarcode(term);
    if (byBarcode) {
      addToCart(byBarcode);
      return;
    }
    const found = await searchProducts(term);
    const onlyMatch = found.length === 1 ? found[0] : undefined;
    if (onlyMatch) {
      addToCart(onlyMatch);
      return;
    }
    setResults(found);
    if (found.length === 0) setNotice(`Aucun produit trouvé pour « ${term} ».`);
  };

  const setQuantity = (productId: number, quantity: number) => {
    setCart((lines) =>
      quantity <= 0
        ? lines.filter((line) => line.product.id !== productId)
        : lines.map((line) =>
            line.product.id === productId ? { ...line, quantity } : line,
          ),
    );
  };

  const subtotal = cart.reduce(
    (sum, line) => sum + line.product.sellPrice * line.quantity,
    0,
  );
  const discount = discountInput.trim() ? parseMoney(discountInput) : 0;
  const total = subtotal - (discount ?? 0);
  const received = receivedInput.trim() ? parseMoney(receivedInput) : null;
  const change = received !== null ? received - total : null;

  const validationError = ((): string | null => {
    if (cart.length === 0) return "Le panier est vide.";
    if (discount === null) return "La remise est invalide.";
    if (discount > subtotal) return "La remise dépasse le montant de la vente.";
    if (method === "ESPECES" && receivedInput.trim()) {
      if (received === null) return "Le montant reçu est invalide.";
      if (received < total) return "Le montant reçu est inférieur au total.";
    }
    return null;
  })();

  const handleValidate = async () => {
    if (validationError) {
      setError(validationError);
      return;
    }
    setPending(true);
    setError(null);
    try {
      const result = await createSale({
        items: cart.map((line) => ({
          productId: line.product.id,
          quantity: line.quantity,
        })),
        discount: discount ?? 0,
        method,
        received: method === "ESPECES" ? received : null,
        customerName: customerName.trim() || null,
      });
      if (!result.ok) {
        setError(result.error);
        return;
      }
      setCompleted(result);
      setCart([]);
      setDiscountInput("");
      setReceivedInput("");
      setCustomerName("");
      setMethod("ESPECES");
      if (autoPrint) setPrintTarget(`/ticket/${result.saleId}?auto=1&t=${Date.now()}`);
      searchRef.current?.focus();
    } catch {
      setError("La vente n'a pas pu être enregistrée. Vérifiez la connexion et réessayez.");
    } finally {
      setPending(false);
    }
  };

  const toggleAutoPrint = (enabled: boolean) => {
    try {
      window.localStorage.setItem(AUTO_PRINT_KEY, enabled ? "1" : "0");
    } catch {
      // Stockage local indisponible : la préférence n'est pas mémorisée.
    }
    window.dispatchEvent(new Event(AUTO_PRINT_EVENT));
  };

  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_380px]">
      <div className="space-y-4">
        <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <label htmlFor="pos-search" className={labelClass}>
            Scanner un code-barres ou rechercher un produit
          </label>
          <input
            id="pos-search"
            ref={searchRef}
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
              setNotice(null);
              if (!event.target.value.trim()) {
                searchSequence.current += 1;
                setResults([]);
              }
            }}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                event.preventDefault();
                void handleSearchSubmit();
              }
            }}
            autoFocus
            autoComplete="off"
            placeholder="Code-barres, nom ou référence"
            className={`${inputClass} text-base`}
          />
          {notice ? <p className="mt-2 text-sm text-amber-700">{notice}</p> : null}
          {results.length > 0 ? (
            <ul className="mt-3 max-h-72 divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200">
              {results.map((product) => (
                <li key={product.id}>
                  <button
                    type="button"
                    onClick={() => addToCart(product)}
                    className="flex w-full items-center justify-between gap-3 px-3 py-2.5 text-left text-sm hover:bg-brand-50"
                  >
                    <span>
                      <span className="font-medium text-slate-900">{product.name}</span>
                      <span className="block text-xs text-slate-500">
                        {product.barcode ?? "Sans code-barres"} · stock : {product.stock}
                      </span>
                    </span>
                    <span className="font-semibold tabular-nums">
                      {formatMoney(product.sellPrice)}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </section>

        <section className="rounded-xl border border-slate-200 bg-white shadow-sm">
          <h2 className="border-b border-slate-200 px-4 py-3 font-semibold">
            Panier ({cart.reduce((sum, line) => sum + line.quantity, 0)} article(s))
          </h2>
          {cart.length === 0 ? (
            <p className="px-4 py-10 text-center text-sm text-slate-500">
              Scannez un produit pour commencer la vente.
            </p>
          ) : (
            <ul className="divide-y divide-slate-100">
              {cart.map((line) => (
                <li
                  key={line.product.id}
                  className="flex flex-wrap items-center gap-3 px-4 py-3"
                >
                  <div className="min-w-0 basis-full sm:flex-1 sm:basis-0">
                    <p className="font-medium text-slate-900 sm:truncate">{line.product.name}</p>
                    <p className="text-xs text-slate-500">
                      {formatMoney(line.product.sellPrice)} l&apos;unité
                      {line.quantity > line.product.stock ? (
                        <span className="ml-2 font-semibold text-amber-700">
                          Stock insuffisant ({line.product.stock})
                        </span>
                      ) : null}
                    </p>
                  </div>
                  <div className="flex items-center gap-1">
                    <button
                      type="button"
                      aria-label={`Diminuer la quantité de ${line.product.name}`}
                      onClick={() => setQuantity(line.product.id, line.quantity - 1)}
                      className="h-10 w-10 rounded-lg border border-slate-300 text-lg font-semibold hover:bg-slate-50"
                    >
                      −
                    </button>
                    <input
                      aria-label={`Quantité de ${line.product.name}`}
                      inputMode="numeric"
                      value={line.quantity}
                      onChange={(event) => {
                        const value = Number(event.target.value.replace(/\D/g, ""));
                        setQuantity(line.product.id, Math.min(Math.max(value, 1), 9999));
                      }}
                      className="h-10 w-14 rounded-lg border border-slate-300 text-center text-sm font-semibold tabular-nums"
                    />
                    <button
                      type="button"
                      aria-label={`Augmenter la quantité de ${line.product.name}`}
                      onClick={() => setQuantity(line.product.id, line.quantity + 1)}
                      className="h-10 w-10 rounded-lg border border-slate-300 text-lg font-semibold hover:bg-slate-50"
                    >
                      +
                    </button>
                  </div>
                  <p className="ml-auto w-28 text-right font-semibold tabular-nums sm:ml-0">
                    {formatMoney(line.product.sellPrice * line.quantity)}
                  </p>
                  <button
                    type="button"
                    aria-label={`Retirer ${line.product.name} du panier`}
                    onClick={() => setQuantity(line.product.id, 0)}
                    className="h-10 w-10 rounded-lg text-slate-400 hover:bg-red-50 hover:text-red-600"
                  >
                    ✕
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <aside className="space-y-4 lg:sticky lg:top-28 lg:self-start">
        {completed ? (
          <section
            role="status"
            className="rounded-xl border border-brand-600 bg-brand-50 p-4"
          >
            <p className="font-semibold text-brand-800">
              Vente {completed.number} enregistrée
            </p>
            <p className="mt-1 text-sm text-brand-800">
              Total : {formatMoney(completed.total)}
            </p>
            {completed.change > 0 ? (
              <p className="mt-2 text-2xl font-bold tabular-nums text-brand-800">
                À rendre : {formatMoney(completed.change)}
              </p>
            ) : null}
            <button
              type="button"
              onClick={() =>
                setPrintTarget(`/ticket/${completed.saleId}?auto=1&t=${Date.now()}`)
              }
              className={`${buttonSecondary} mt-3 w-full`}
            >
              Imprimer le ticket
            </button>
          </section>
        ) : null}

        <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <dl className="space-y-1 text-sm">
            <div className="flex justify-between">
              <dt className="text-slate-500">Sous-total</dt>
              <dd className="tabular-nums">{formatMoney(subtotal)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt>
                <label htmlFor="pos-discount" className="text-slate-500">
                  Remise (DA)
                </label>
              </dt>
              <dd>
                <input
                  id="pos-discount"
                  inputMode="decimal"
                  value={discountInput}
                  onChange={(event) => setDiscountInput(event.target.value)}
                  placeholder="0"
                  className="w-28 rounded-lg border border-slate-300 px-2 py-1.5 text-right text-sm tabular-nums"
                />
              </dd>
            </div>
          </dl>
          <div className="flex items-baseline justify-between border-t border-slate-200 pt-3">
            <span className="font-semibold">Total à payer</span>
            <span className="text-3xl font-bold tabular-nums text-slate-900">
              {formatMoney(Math.max(total, 0))}
            </span>
          </div>

          <fieldset>
            <legend className={labelClass}>Mode de paiement</legend>
            <div className="grid grid-cols-2 gap-2">
              {PAYMENT_METHODS.map((option) => (
                <button
                  key={option}
                  type="button"
                  aria-pressed={method === option}
                  onClick={() => setMethod(option)}
                  className={`rounded-lg border px-3 py-2.5 text-sm font-semibold transition-colors ${
                    method === option
                      ? "border-brand-600 bg-brand-50 text-brand-800"
                      : "border-slate-300 text-slate-700 hover:bg-slate-50"
                  }`}
                >
                  {PAYMENT_LABELS[option]}
                </button>
              ))}
            </div>
          </fieldset>

          {method === "ESPECES" ? (
            <div>
              <label htmlFor="pos-received" className={labelClass}>
                Montant reçu (DA)
              </label>
              <input
                id="pos-received"
                inputMode="decimal"
                value={receivedInput}
                onChange={(event) => setReceivedInput(event.target.value)}
                placeholder="Montant exact"
                className={`${inputClass} text-right text-base tabular-nums`}
              />
              {change !== null && change >= 0 ? (
                <p className="mt-2 flex justify-between text-sm">
                  <span className="text-slate-500">Monnaie à rendre</span>
                  <span className="font-bold tabular-nums">{formatMoney(change)}</span>
                </p>
              ) : null}
            </div>
          ) : null}

          <div>
            <label htmlFor="pos-customer" className={labelClass}>
              Client (facultatif)
            </label>
            <input
              id="pos-customer"
              value={customerName}
              onChange={(event) => setCustomerName(event.target.value)}
              maxLength={120}
              className={inputClass}
            />
          </div>

          {error ? (
            <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
              {error}
            </p>
          ) : null}

          <button
            type="button"
            onClick={() => void handleValidate()}
            disabled={pending || cart.length === 0}
            className={`${buttonPrimary} w-full py-3.5 text-base`}
          >
            {pending ? "Enregistrement…" : "Valider la vente"}
          </button>

          <label className="flex items-center gap-2 text-sm text-slate-600">
            <input
              type="checkbox"
              checked={autoPrint}
              onChange={(event) => toggleAutoPrint(event.target.checked)}
              className="h-4 w-4 accent-brand-600"
            />
            Imprimer le ticket automatiquement
          </label>
        </section>
      </aside>

      {printTarget ? (
        <iframe
          key={printTarget}
          src={printTarget}
          title="Impression du ticket"
          aria-hidden="true"
          className="pointer-events-none fixed bottom-0 right-0 h-0 w-0 border-0 opacity-0"
        />
      ) : null}
    </div>
  );
};
