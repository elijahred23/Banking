# Banking.Web guidance

This file supplements the repository-level `AGENTS.md` for everything under `Banking.Web`.

## Security and scoping

- Controllers require authentication unless an action is intentionally public.
- Resolve the active bank through `ActiveBank` and include its ID in every payment, account, message, learning-progress, and operations query.
- Apply role authorization at the mutation boundary. Hiding a button in Razor is not authorization.
- Keep anti-forgery validation on state-changing form posts. Use confirmation for destructive lab actions and describe exactly which data is affected.
- Never accept account, bank, or wire ownership from posted form data without checking it against the active bank.

## MVC conventions

- Keep controllers responsible for orchestration and validation; place reusable payment rules in `Banking.Domain`.
- Re-populate all select lists and value pickers when returning an invalid form.
- Use view models rather than exposing broad entity graphs to forms.
- Keep Razor accessible: associated labels, semantic headings, keyboard-operable controls, status text that does not rely only on color, and responsive tables.
- Put shared browser behavior in `wwwroot/js`; avoid large inline scripts. Put feature styles in `site.css` unless a separate bundle becomes justified.
- Maintain the existing bank/persona language and clearly label simulated or learning-only behavior.

## Learning features

- A mission should follow: objective, prediction, action, evidence, explanation, and assessment.
- Durable progress is bank-scoped so persona switching does not break a coordinated exercise.
- Completion must be evidence-based. Do not award completion merely for opening a page.
- Keep answer explanations and hints instructional; avoid exposing the final answer before the learner attempts the task.
- Instructor views require the Operations role. Learning resets must not delete payment, message, account, or journal data.
- Production-versus-lab limitations should appear near the relevant workflow, not only in the README.

## Live payment views

- Preserve SignalR refresh behavior and rebind JavaScript handlers after fragment replacement.
- Treat XML as untrusted display content. Parse it safely and escape values before inserting generated HTML.
- Timeline replay is presentation-only; it must not reprocess payments or republish messages.
- Explain holds, settlement, and balanced journals without changing their underlying accounting semantics.

## Verification

Build the full solution because Razor compilation catches view errors. When changing authentication, sessions, schema startup, or learning routes, perform a login/startup smoke test in addition to the repository test commands.

