# Runbook — Provision Narrative AOAI

## What it does

The `Provision Narrative AOAI` workflow (`.github/workflows/provision-narrative-aoai.yml`)
provisions the Azure OpenAI resource behind the Phase 2 Sprint 1 narrative feature, wires it
into the `ltl-uat-api` App Service, and flips the `AI:NarrativeEnabled` feature flag to
`true`. In order it: logs in to Azure via OIDC, verifies the subscription, verifies/creates
the AOAI Cognitive Services account (`--kind OpenAI --sku S0`), deploys the chat model,
enables a system-assigned managed identity on the API app, grants that identity the
`Cognitive Services OpenAI User` role on the AOAI scope, sets the four `AI__*` app settings
(`AI__NarrativeEnabled`, `AI__AzureOpenAI__Endpoint`, `AI__AzureOpenAI__Deployment`,
`AI__AzureOpenAI__ApiVersion`), prints the resulting settings, and restarts the app.

This replaces a local `az` runbook that could not run from the corporate network: every
`az` call there died with `ConnectionResetError(10054)` behind the corporate proxy.
GitHub-hosted runners sit outside that proxy and reach Azure cleanly. The workflow
authenticates with the same **OIDC federated credential** used by the existing UAT deploy
and infra workflows (`azure/login@v2` + `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` /
`AZURE_SUBSCRIPTION_ID`) — **no client secret**. Every mutating step is guarded by a `show`
read first, so the workflow is safely re-runnable: an already-provisioned environment prints
"already provisioned" per step and exits 0.

## How to run it

1. GitHub → **Actions** tab.
2. Select **Provision Narrative AOAI** in the left sidebar.
3. Click **Run workflow**.
4. Accept the defaults (or override any input), then click the green **Run workflow** button.

Tip: run once with `dry_run = true` first to see every command that would execute (with
variables expanded) without changing any state.

## Inputs and defaults

| Input | Default | Purpose |
| --- | --- | --- |
| `rg` | `ltl-uat-rg` | Resource group (the only RG this workflow touches). |
| `api_app` | `ltl-uat-api` | API App Service to wire the AOAI resource into. |
| `aoai_name` | `ltl-uat-openai-eastus2` | Azure OpenAI account name. |
| `aoai_location` | `eastus2` | Azure region for the AOAI resource. |
| `deployment_name` | `gpt-5-4-mini` | Model deployment name (the name the app calls). |
| `model_name` | `gpt-5.4-mini` | Underlying model name. |
| `model_version` | `2026-03-17` | Underlying model version. |
| `api_version` | `2024-06-01` | Azure OpenAI API version (config parity). |
| `sku_capacity` | `20` | Deployment SKU capacity (K tokens/min). |
| `dry_run` | `false` | When `true`, prints commands but sets no state. |

## Verify

Wait ~30s after the run for the app to restart, then hit:

```
GET https://<api-host>/api/ai/consolidation/narrative?planId=<id>
```

## Rollback / disable

To turn the feature off without deleting any Azure resources, flip the flag back to `false`.
Either re-run an equivalent workflow-dispatch that sets the flag, or run manually once your
network can reach Azure:

```bash
az webapp config appsettings set \
  --name ltl-uat-api -g ltl-uat-rg \
  --settings AI__NarrativeEnabled=false
az webapp restart --name ltl-uat-api -g ltl-uat-rg
```

The AOAI resource, deployment, managed identity, and role assignment can be left in place;
the kill switch (`AI:NarrativeEnabled=false`) fully disables the narrative path.
