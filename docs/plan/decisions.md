# Product-Owner Decisions

Answers to the questions in [open-questions.md](open-questions.md). A decision here overrides the question's suggested default. Affected tickets must follow it.

| ID | Decision | Date |
|---|---|---|
| Q-01 (part 1) | **Self-hosted open-source product.** Organizations install and run opportuniTY in their own environment. It is **not** offered as a hosted/cloud service by the project. One installation serves one organization with many workspaces; workspace isolation boundaries remain mandatory. | 2026-10-02 |
| Q-01 (part 2) | **Lite = evaluation/development only.** Lite keeps simple defaults and its docs state it must not hold real client data. **Full is the only supported production profile**: TLS between all components, PostgreSQL RLS, OpenSearch security plugin, sandboxed render workers and malware scanning on by default. | 2026-10-02 |
