// -*- coding: utf-8 -*-
// CR-03 фінал — модульна регресія на ФАКТИЧНОМУ production-коді frontend/lib/guestLists.ts
// (transpile реального сорсу через typescript, жодних копій логіки).
// Сценарії: same-key rating edit / remove+readd / identical confirmed / інший key / mixed.
const fs = require("fs");
const path = require("path");
const ts = require(path.join("G:/Projects/movie-discovery/frontend", "node_modules", "typescript"));

const SRC = path.join("G:/Projects/movie-discovery/frontend/lib/guestLists.ts");
const src = fs.readFileSync(SRC, "utf8");
const js = ts.transpileModule(src, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;

// Map-стаб window.localStorage (як у браузері)
const backing = new Map();
global.window = {
  localStorage: {
    getItem: (k) => (backing.has(k) ? backing.get(k) : null),
    setItem: (k, v) => backing.set(k, String(v)),
    removeItem: (k) => backing.delete(k),
  },
};

// Реальний модуль (імпорт "./types" — type-only, transpile стирає)
const mod = { exports: {} };
new Function("module", "exports", js)(mod, mod.exports);
const gl = mod.exports;

let pass = 0, fail = 0;
function check(name, cond, detail = "") {
  if (cond) { pass++; console.log(`PASS  ${name}`); }
  else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
function seed(items) {
  backing.set("md_guest_lists_v1", JSON.stringify({ version: 1, items }));
}
function current() {
  return (JSON.parse(backing.get("md_guest_lists_v1") || "null") || { items: [] }).items;
}
// «Інша вкладка»: мутувати і ЗАПИСАТИ назад у backing (current() віддає копію).
function mutate(fn) {
  const items = current();
  fn(items);
  backing.set("md_guest_lists_v1", JSON.stringify({ version: 1, items }));
}

// ── P1: same-key rating edit під час in-flight → оновлений запис ЛИШАЄТЬСЯ ──
seed([{ kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "2026-11-09T10:00:00Z", meta: null }]);
mutate((items) => { items[0].rating = 9; }); // «інша вкладка» змінила рейтинг, addedAt той самий
gl.removeGuestEntries([{ kind: "watched", titleId: 1, rating: 7, addedAt: "2026-11-09T10:00:00Z" }]);
const p1 = current();
check("M1 same-key rating edit (7->9) preserved", p1.length === 1 && p1[0].rating === 9, JSON.stringify(p1));

// ── P2: remove+readd того самого ключа (новий addedAt) → ЛИШАЄТЬСЯ ──
seed([{ kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "2026-11-09T10:00:00Z", meta: null }]);
mutate((items) => { items[0].addedAt = "2026-11-09T10:05:00Z"; }); // повторно доданий запис
gl.removeGuestEntries([{ kind: "watched", titleId: 1, rating: 7, addedAt: "2026-11-09T10:00:00Z" }]);
const p2 = current();
check("M2 remove+readd same key (new addedAt) preserved", p2.length === 1 && p2[0].addedAt === "2026-11-09T10:05:00Z", JSON.stringify(p2));

// ── P3: точно ідентичний підтверджений запис ВИДАЛЯЄТЬСЯ ──
seed([{ kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "2026-11-09T10:00:00Z", meta: null }]);
gl.removeGuestEntries([{ kind: "watched", titleId: 1, rating: 7, addedAt: "2026-11-09T10:00:00Z" }]);
check("M3 identical confirmed entry removed", current().length === 0, JSON.stringify(current()));

// ── P4: інший ключ не чіпається ──
seed([
  { kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "T1", meta: null },
  { kind: "watchlist", titleId: 2, tmdbId: null, rating: null, addedAt: "T2", meta: null },
]);
gl.removeGuestEntries([{ kind: "watched", titleId: 1, rating: 7, addedAt: "T1" }]);
const p4 = current();
check("M4 other key preserved", p4.length === 1 && p4[0].titleId === 2, JSON.stringify(p4));

// ── P5: mixed — ідентичні видаляються; відредагований + новий інший лишаються ──
seed([
  { kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "T1", meta: null },   // identical → drop
  { kind: "watched", titleId: 3, tmdbId: null, rating: 5, addedAt: "T3", meta: null },   // edited during flight → keep
  { kind: "watchlist", titleId: 4, tmdbId: null, rating: null, addedAt: "T4", meta: null }, // new other key → keep
]);
mutate((items) => { items[1].rating = 6; }); // редакція того самого ключа під час in-flight
gl.removeGuestEntries([
  { kind: "watched", titleId: 1, rating: 7, addedAt: "T1" },            // confirmed identical
  { kind: "watched", titleId: 3, rating: 5, addedAt: "T3" },            // confirmed SUBMITTED value
]);
const p5 = current();
check("M5 mixed: identical dropped, edited(6) + new(other key) kept",
  p5.length === 2 && p5.some((i) => i.titleId === 3 && i.rating === 6) && p5.some((i) => i.titleId === 4),
  JSON.stringify(p5));

// ── P6: rejected взагалі не передаються у confirmed — нічого не видаляється ──
seed([{ kind: "watchlist", titleId: 999999999, tmdbId: null, rating: null, addedAt: "T9", meta: null }]);
gl.removeGuestEntries([]); // rejected не входять у confirmed
check("M6 empty confirmed list = no-op (rejected never passed)", current().length === 1, JSON.stringify(current()));

// ── P7: full-wipe clearGuestSnapshot більше не існує в API ──
check("M7 clearGuestSnapshot removed from module API", typeof gl.clearGuestSnapshot === "undefined");

console.log(`\n== module probe: PASS=${pass} FAIL=${fail} ==`);
fs.writeFileSync(
  "G:/Projects/movie-discovery/docs/audits/2026-11-09/cr03-final/module-probe.json",
  JSON.stringify({ source: "frontend/lib/guestLists.ts (transpiled production code)", pass, fail }, null, 2),
);
process.exit(fail ? 1 : 0);
