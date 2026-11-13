"use client";

import { switchSite } from "@/actions/auth";

type SiteOption = { id: number; name: string };

export const SiteSwitcher = ({
  sites,
  currentSiteId,
}: {
  sites: SiteOption[];
  currentSiteId: number;
}) => (
  <form action={switchSite}>
    <label htmlFor="site-switcher" className="sr-only">
      Site
    </label>
    <select
      id="site-switcher"
      name="siteId"
      defaultValue={currentSiteId}
      onChange={(event) => event.currentTarget.form?.requestSubmit()}
      className="rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm font-medium text-slate-800"
    >
      {sites.map((site) => (
        <option key={site.id} value={site.id}>
          {site.name}
        </option>
      ))}
    </select>
  </form>
);
