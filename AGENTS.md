# Workspace Guardrail

- Treat `origin/` as read-only.
- Do not create, modify, move, rename, or delete anything under `origin/`.
- If a task appears to require changes inside `origin/`, stop and ask the user for explicit permission first.
- Prefer working in a separate scratch or output directory outside `origin/`.
