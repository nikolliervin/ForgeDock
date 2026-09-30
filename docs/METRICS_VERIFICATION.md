# Metrics and environment verification — 2026-09-30

- Full .NET solution build passed with zero warnings/errors. Production React TypeScript/Vite build passed.
- Backend tests: 73 passed. The existing opt-in Compose integration test was skipped. New tests cover Docker byte units, CPU/memory/network/disk parsing, aggregation across services, elapsed-time network rates, resets/restarts/gaps, range boundaries, and secret-safe environment parsing.
- Browser checks: 15 passed across metrics, environment, docs, domains, and navigation. Metrics checks cover charts/cards, uptime, range/service filters, stale data, mobile overflow, empty data, and error retries. Environment checks cover individual entry, overwrite conflicts, batch replacement, cleared editor content, hidden saved values, and mobile layout.
- A temporary host ran the actual independent MetricsCollector against existing managed applications. Two collections persisted measurements from six containers across one single-application project and two Compose stacks without restarting applications or altering deployments. Docker infrastructure was excluded. The collector stopped after the check.
- A temporary API instance verified live metrics for all three projects, all time ranges, network rates, service filtering, 401 for anonymous access, 400 for invalid ranges, and 404 for unknown projects. Browser tests use mocked APIs; these separate API checks cover real storage and querying.
- A temporary project verified bulk environment imports, encrypted database storage, name-only responses, conflict protection for existing names, atomic rejection of invalid batches, replacement of selected names, and preservation of other variables. The temporary project was deleted afterward.
- The metrics migration is applied locally. EF reports no pending model changes.
- Desktop/mobile screenshots were inspected; guides are available at `/docs/metrics` and the Environment guide includes imports.

Collection is approximately every 30 seconds plus command duration. Long-term retention over a full day, alerting, HTTP request/latency metrics, and external exporters were not tested or added. The existing launcher was left running; restart it to load the new API and worker assemblies and begin continuous collection.
