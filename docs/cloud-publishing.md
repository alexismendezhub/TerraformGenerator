# Publishing Terraform Generator in the cloud — architecture, cost and open risks

Written 2026-11-04 from a read of the code (not from a deployed test). Prices are list prices, pay as you go, East US.

## Why the app cannot be published as it is

Terraform Generator is built as a tool for one person on their own computer. Each point below must change before it faces
the internet:

| # | Finding (from the code) | Risk in the cloud | Fix |
| --- | --- | --- | --- |
| 1 | No sign-in on any `/api` endpoint (`Server/Endpoints/SketchEndpoints.cs`). | Anyone can use the AI endpoints with our Anthropic key. | Sign-in (Entra External ID or another OpenID Connect provider) on every endpoint except static files. |
| 2 | `/ai/propose`, `/ai/recommend`, `/import` (UseAi) call Claude with `MaxTokens = 16000`; no limit per user. | Unbounded AI bill. | Per-user quota and rate limiter (`AddRateLimiter`), monthly budget alert, plan limits. |
| 3 | `/validate` runs `terraform init` + `validate` on generated files; **Extra HCL** (`GenericGenerator.ExtraHcl`) is inserted raw into a resource block, and `/import` accepts any `.tf`. | **Not tested, inferred from code:** raw text can close the block and add `terraform { required_providers }` or `module` blocks; `init` would then download and `validate` would start a provider binary chosen by the user → code execution in our container, and outbound requests from it. | Run validation in an isolated, short-lived sandbox (Container Apps job or dynamic session) with no network, providers from a read-only filesystem mirror baked into the image (`-plugin-dir`), and reject `terraform`, `provider`, `module` and unbalanced braces in Extra HCL. Test with a malicious payload before launch. |
| 4 | `TerraformRunner` has one global `SemaphoreSlim(1,1)` and steps of up to 4 minutes. | One user's validation blocks every other user. | Queue + jobs (point 3) with per-user concurrency. |
| 5 | The palette preferences (pins and section names) are kept in each person's browser (`localStorage`), like the drawing. | Nothing on the server to lose or share between users; a person on another browser starts with an empty palette. | **Done (2026-11-21):** no database. If preferences must follow the person across browsers, store them per signed-in user once sign-in exists (point 1). |
| 6 | `ProviderSchemas` downloads providers at runtime into `%LOCALAPPDATA%`; `/schemas/{platform}/refresh` runs `init -upgrade`. | Slow cold start, egress, any user can trigger upgrades. | Generate the three schema files at image build; make refresh an admin-only build step. |
| 7 | Listens on `http://localhost:5080` unless `ASPNETCORE_URLS` is set. | — | Set `ASPNETCORE_URLS=http://+:8080` in the container. |
| 8 | Anthropic key from `Ai:ApiKey` or `ANTHROPIC_API_KEY`. | Key in plain config. | Key Vault secret referenced by the container app's managed identity. |

## Target architecture (Azure)

- Azure Container Apps environment (Consumption): `terraformgenerator-web` (API + Angular static files, 1 vCPU / 2 GiB, min 1 replica) and a `terraform-validate` job (sandbox, no egress).
- Azure Container Registry Basic, Key Vault, Log Analytics + Application Insights.
- Entra External ID for sign-in; custom domain with a Container Apps managed certificate.
- Optional later: Front Door with WAF.

## Monthly cost estimate (low traffic)

| Item | Estimate | Basis |
| --- | --- | --- |
| Container Apps (the app with min 1 replica, mostly idle, plus the validation job) | ~$25–38 | idle $0.000003 per vCPU-s and per GiB-s; active $0.000024 per vCPU-s; free grant 180k vCPU-s / 360k GiB-s |
| Container Registry Basic | ~$5 | not verified |
| Key Vault, DNS, logs (under 5 GB) | ~$1–5 | not verified |
| Entra External ID | $0 under 50k monthly users | not verified |
| **Fixed total** | **~$31–48 / month** | |
| Claude Opus 5.5 | ~$0.10–0.30 per AI call | $4 / $20 per million tokens in/out; prompt ~3–4k tokens + sketch + output up to 16k (assumption — measure) |

Next step: measure real tokens per call (usage field of the response) and validate the sandbox (point 3) with a test payload.
