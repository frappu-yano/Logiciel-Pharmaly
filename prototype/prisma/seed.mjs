// Initialise la base : sites, caisses et compte administrateur.
// Usage : npm run db:seed            (structure de départ)
//         SEED_DEMO=1 npm run db:seed (ajoute des produits de démonstration)
import { randomBytes } from "node:crypto";
import { PrismaClient } from "@prisma/client";
import bcrypt from "bcryptjs";

const db = new PrismaClient();

const SITES = [
  { code: "B1", name: "Boutique 1", type: "BOUTIQUE" },
  { code: "B2", name: "Boutique 2", type: "BOUTIQUE" },
  { code: "WEB", name: "Boutique en ligne", type: "EN_LIGNE" },
];

const DEMO_PRODUCTS = [
  { name: "Crème hydratante visage 50 ml", barcode: "6130000000011", sellPrice: 185000, buyPrice: 130000, category: "Soins visage" },
  { name: "Écran solaire SPF 50+ 40 ml", barcode: "6130000000028", sellPrice: 240000, buyPrice: 175000, category: "Solaire" },
  { name: "Shampooing doux 250 ml", barcode: "6130000000035", sellPrice: 95000, buyPrice: 62000, category: "Cheveux" },
  { name: "Gel nettoyant 200 ml", barcode: "6130000000042", sellPrice: 120000, buyPrice: 84000, category: "Soins visage" },
  { name: "Vitamine C 1000 mg — 30 comprimés", barcode: "6130000000059", sellPrice: 78000, buyPrice: 51000, category: "Compléments" },
  { name: "Baume à lèvres", barcode: "6130000000066", sellPrice: 35000, buyPrice: 20000, category: "Soins visage" },
];

const main = async () => {
  for (const site of SITES) {
    await db.site.upsert({ where: { code: site.code }, update: {}, create: site });
  }
  const sites = await db.site.findMany();

  for (const site of sites.filter((candidate) => candidate.type === "BOUTIQUE")) {
    const existing = await db.register.count({ where: { siteId: site.id } });
    if (existing === 0) {
      await db.register.create({ data: { siteId: site.id, name: "Caisse 1" } });
    }
  }

  const adminExists = await db.user.findUnique({ where: { username: "admin" } });
  if (!adminExists) {
    const password = process.env.SEED_ADMIN_PASSWORD ?? randomBytes(9).toString("base64url");
    await db.user.create({
      data: {
        name: "Administrateur",
        username: "admin",
        role: "ADMIN",
        passwordHash: await bcrypt.hash(password, 12),
      },
    });
    console.log(`Compte administrateur créé — identifiant : admin — mot de passe : ${password}`);
    console.log("Notez ce mot de passe : il ne sera plus affiché.");
  }

  if (process.env.SEED_DEMO === "1") {
    const admin = await db.user.findUniqueOrThrow({ where: { username: "admin" } });
    for (const { category, ...product } of DEMO_PRODUCTS) {
      const savedCategory = await db.category.upsert({
        where: { name: category },
        update: {},
        create: { name: category },
      });
      const exists = await db.product.findUnique({ where: { barcode: product.barcode } });
      if (exists) continue;
      const created = await db.product.create({
        data: { ...product, categoryId: savedCategory.id },
      });
      for (const site of sites) {
        await db.stock.create({ data: { productId: created.id, siteId: site.id, quantity: 20 } });
        await db.stockMovement.create({
          data: { productId: created.id, siteId: site.id, quantity: 20, type: "ENTREE", note: "Stock initial (démo)", userId: admin.id },
        });
      }
    }
    console.log("Produits de démonstration ajoutés.");
  }
};

main()
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  })
  .finally(() => db.$disconnect());
