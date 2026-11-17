import nextVitals from "eslint-config-next/core-web-vitals";
import nextTypescript from "eslint-config-next/typescript";

const eslintConfig = [
  ...nextVitals,
  ...nextTypescript,
  {
    rules: {
      // Українські апострофи в JSX («п'ять», «м'ясо») ламають цим правилом білд.
      "react/no-unescaped-entities": "off",
      // Постери йдемо звичайними <img>: next/image у standalone-контейнері тягне sharp.
      "@next/next/no-img-element": "off",
      // React Compiler is not enabled. Keep its new effect diagnostics visible
      // during the Next 16 migration while retaining the existing hooks checks.
      "react-hooks/set-state-in-effect": "warn",
    },
  },
  {
    ignores: [
      "node_modules/**",
      ".next/**",
      ".open-next/**",
      ".wrangler/**",
      "out/**",
      "build/**",
      "next-env.d.ts",
    ],
  },
];

export default eslintConfig;
