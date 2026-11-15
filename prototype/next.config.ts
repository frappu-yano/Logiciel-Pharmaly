import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Toutes les pages dépendent de l'utilisateur connecté et de données en temps réel
  // (stock, caisse) : elles sont rendues à chaque requête, sans cache de composants.
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
