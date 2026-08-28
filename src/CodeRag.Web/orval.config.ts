import { defineConfig } from "orval";

export default defineConfig({
  coderag: {
    input: {
      target: "http://localhost:8080/openapi/v1.json",
      // fallback local snapshot if Api not running:
      // target: "./openapi.json",
    },
    output: {
      mode: "split",
      target: "src/api/generated/coderag.ts",
      schemas: "src/api/generated/model",
      client: "react-query",
      prettier: true,
      override: {
        mutator: {
          path: "src/api/client.ts",
          name: "apiClient",
        },
        query: {
          useQuery: true,
          useMutation: true,
          signal: true,
        },
      },
    },
    hooks: {
      afterAllFilesWrite: "prettier --write",
    },
  },
});
