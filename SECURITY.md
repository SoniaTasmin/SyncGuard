# Security notes

SyncGuard is a **local demonstration with synthetic data**. It is not hardened for production, and it should not be exposed
to a network or used with real customer data.

## Secrets and credentials

* No secrets are committed. The only credential is the SQL Server `sa` password, supplied through `.env`
  (git-ignored and docker-ignored). `.env.example` contains a placeholder that you are told to change.
* `appsettings.json` files contain connection strings with the placeholder `SET_VIA_ENVIRONMENT`. The real value
  comes from environment variables (`ConnectionStrings__Source`, `ConnectionStrings__Destination`).
* The `db-init` script passes the password through `SQLCMDPASSWORD`, not on the command line.
* Applications never log connection strings, credentials or order payloads. Failure logs record the HTTP status or a
  transport reason only; destination response bodies are stored in the `LastError` column for operators, not written to logs.
* All published ports bind to `127.0.0.1`.

## Known production gaps (deliberate, for scope)

| Area | Demo behaviour | Production expectation |
|---|---|---|
| Database identity | Connects as `sa` | Least-privilege logins per service (the worker needs only DML on the outbox tables); managed identity or a secret store (e.g. Azure Key Vault, AWS Secrets Manager) |
| Transport | Plain HTTP between containers; `TrustServerCertificate=True` for SQL | TLS everywhere, with validated certificates |
| API authentication | None, including `/ops/*` and `/api/orders/seed` | Authenticated callers; ops endpoints on an internal network; no seed endpoint |
| Destination auth | None | OAuth2 client credentials or mTLS, with secrets rotated |
| Fault injection | `/fault-injection/rules` lets anyone break the destination | Must never exist in a real deployment. It is **off by default** (`FaultInjection:Enabled=false`) and endpoints are not mapped unless enabled, with a warning logged at startup. Only `.env.example` enables it for the local demo. |
| Data at rest | Docker volume, unencrypted | TDE / encrypted storage; retention policy for the outbox and `LastError` |
| Input limits | Basic validation and size limits on order fields | Request size limits, rate limiting, schema validation at the edge |

## Demo tooling (`demo/`)

* `demo/sg` and `demo/demo.py` are local developer tools. `sg sql` runs arbitrary SQL as `sa` against the local demo
  databases by design, and `demo.py` / `sg reset` run `docker compose down -v`, which **deletes the local demo databases**
  (`demo.py` asks first unless given `--yes`). Never point them at a real environment.
* The SQL password is only ever expanded inside the SQL Server container; the tools never print or log it.
  `sg test` copies the repository into a throwaway SDK container **excluding** `.env` and `.git`.
* `demo/narrate.py` downloads the Kokoro TTS model from its GitHub release and runs it in a container with networking disabled.
* Everything under `demo/out/` (recordings, logs, model files, videos) is generated locally and git-ignored.

## Dependencies

`TreatWarningsAsErrors` is enabled, so NuGet vulnerability audit warnings fail the build. A transitive dependency of
`Microsoft.Data.SqlClient` (`System.Security.Cryptography.Pkcs`) is pinned to a patched version in `Directory.Build.props` for this reason.

## Reporting

This is a portfolio project. If you notice a problem, please open an issue on the repository rather than sharing details publicly elsewhere.
