// Space Hunter's public totals: a Cloudflare Worker with a D1 database (both on Cloudflare's free plan).
// Only copies whose owner said yes ever call it. What it keeps per install: a random install number the app made
// up, the system (windows / mac / linux), the version, the total freed and number of cleans so far, and the first
// and last day it was seen. No IP addresses, names, paths or anything about what was cleaned are stored.
//
//   POST /ping   {"id","v","os","freed","cleans"}   from the app, at most daily and after a clean
//   GET  /stats  [?days=30]                         the totals, as JSON, for anyone
//   GET  /badge/<freed|active|sharing>              a shields.io badge for the README

const DAY = 86400000;

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const cors = { 'Access-Control-Allow-Origin': '*' };

    if (req.method === 'POST' && url.pathname === '/ping') {
      let b;
      try { b = await req.json(); } catch { return new Response('bad request', { status: 400 }); }
      const id = String(b.id || '');
      if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) return new Response('bad request', { status: 400 });
      const os = ['windows', 'mac', 'linux'].includes(b.os) ? b.os : 'other';
      const v = String(b.v || '').replace(/[^0-9.]/g, '').slice(0, 16);
      // a sane ceiling, so one broken or hostile copy can't wreck the totals
      const freed = Math.min(Math.max(0, Math.floor(Number(b.freed) || 0)), 1e14);
      const cleans = Math.min(Math.max(0, Math.floor(Number(b.cleans) || 0)), 100000);
      const now = Date.now(), day = new Date(now).toISOString().slice(0, 10);

      const old = await env.DB.prepare('SELECT freed FROM installs WHERE id = ?1').bind(id).first();
      const gained = old ? Math.max(0, freed - old.freed) : freed;
      await env.DB.batch([
        env.DB.prepare(`INSERT INTO installs (id, os, v, freed, cleans, first_seen, last_seen) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?6)
                        ON CONFLICT(id) DO UPDATE SET os = ?2, v = ?3, freed = max(freed, ?4), cleans = max(cleans, ?5), last_seen = ?6`)
          .bind(id, os, v, freed, cleans, now),
        // what was freed on each day, so "freed in the last N days" can be answered
        env.DB.prepare(`INSERT INTO days (day, freed) VALUES (?1, ?2) ON CONFLICT(day) DO UPDATE SET freed = freed + ?2`).bind(day, gained),
      ]);
      return new Response(null, { status: 204 });
    }

    if (req.method === 'GET' && (url.pathname === '/stats' || url.pathname.startsWith('/badge/'))) {
      const now = Date.now();
      const days = Math.min(Math.max(1, parseInt(url.searchParams.get('days') || '30', 10) || 30), 3650);
      const since = new Date(now - days * DAY).toISOString().slice(0, 10);
      const t = await env.DB.prepare(`SELECT count(*) AS n, coalesce(sum(freed), 0) AS f, coalesce(sum(cleans), 0) AS c,
                                             coalesce(sum(last_seen > ?1), 0) AS a7, coalesce(sum(last_seen > ?2), 0) AS a30
                                      FROM installs`).bind(now - 7 * DAY, now - 30 * DAY).first();
      const byOs = await env.DB.prepare('SELECT os, count(*) AS n FROM installs GROUP BY os').all();
      const recent = await env.DB.prepare('SELECT coalesce(sum(freed), 0) AS f FROM days WHERE day > ?1').bind(since).first();
      const stats = {
        sharing: t.n, active_7d: t.a7, active_30d: t.a30,
        freed_bytes: t.f, cleans: t.c,
        freed_last_days: { days, bytes: recent.f },
        by_os: Object.fromEntries(byOs.results.map(r => [r.os, r.n])),
      };
      const headers = { ...cors, 'Cache-Control': 'public, max-age=600' };
      if (url.pathname === '/stats') return Response.json(stats, { headers });
      const which = url.pathname.slice('/badge/'.length);
      const badge = which === 'freed' ? { label: 'space freed', message: size(stats.freed_bytes) }
                  : which === 'active' ? { label: 'active this month', message: String(stats.active_30d) }
                  : { label: 'counted installs', message: String(stats.sharing) };
      return Response.json({ schemaVersion: 1, color: 'ff5b1f', ...badge }, { headers });
    }

    return new Response('Space Hunter public totals: GET /stats', { status: 404, headers: cors });
  },
};

function size(b) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']; let i = 0;
  while (b >= 1000 && i < u.length - 1) { b /= 1024; i++; }
  return (i === 0 ? b : b.toFixed(1)) + ' ' + u[i];
}
