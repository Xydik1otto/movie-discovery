// Перевірка парності ключів словників i18n: uk ↔ en ↔ ru.
// Запуск: node scripts/check-i18n-parity.mjs (з каталогу frontend/).
// Exit code 1, якщо є розбіжності — можна вмикати як гейт у CI.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const files = {
  uk: join(here, "..", "i18n", "uk.ts"),
  en: join(here, "..", "i18n", "en.ts"),
  ru: join(here, "..", "i18n", "ru.ts"),
};

// Ключі словника — рядки на початку рядка виду  "key.name": "value",
function keysOf(path) {
  const text = readFileSync(path, "utf8");
  const keys = new Set();
  for (const line of text.split(/\r?\n/)) {
    const m = /^\s*"([^"]+)"\s*:/.exec(line);
    if (m) keys.add(m[1]);
  }
  return keys;
}

const dicts = Object.fromEntries(Object.entries(files).map(([n, p]) => [n, keysOf(p)]));
const [uk, en, ru] = [dicts.uk, dicts.en, dicts.ru];

let ok = true;
for (const [a, b] of [
  ["uk", "en"],
  ["uk", "ru"],
  ["en", "ru"],
]) {
  const A = dicts[a];
  const B = dicts[b];
  const missingInB = [...A].filter((k) => !B.has(k));
  const extraInB = [...B].filter((k) => !A.has(k));
  if (missingInB.length || extraInB.length) ok = false;
  console.log(`${a} (${A.size}) vs ${b} (${B.size}):`);
  for (const k of missingInB) console.log(`  MISSING in ${b}: ${k}`);
  for (const k of extraInB) console.log(`  EXTRA in ${b}: ${k}`);
  if (!missingInB.length && !extraInB.length) console.log("  ✓ идентичні");
}

console.log(`\nПідсумок: uk=${uk.size} ключів, en=${en.size}, ru=${ru.size}`);
console.log(ok ? "PARITY OK" : "PARITY FAILED");
process.exit(ok ? 0 : 1);
