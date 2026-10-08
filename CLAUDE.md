# goat-ticket-app

.NET 8 application (API on App Service for Linux, worker on Azure Functions) and its delivery pipeline.
Read `docs/PROJECT_CONTEXT.md` first: it says what is done, what is only designed, and the mentor's hard rules.

## Layout

```
azure-pipelines-app.yml                  root pipeline (Git Flow, build once, five environment stages)
deploy/templates/deploy-env-stage.yml    generic stage: optional infra, application (direct or slot), smoke test
deploy/environments/<env>.yml            values for one environment, passed to the stage template as parameters
src/  tests/  GoatTicket.sln             application code
```

## Branches

`develop` leads to DEV; `release/*` to SIT then UAT; `main` to Pre-PROD then PROD; `hotfix/*` to one chosen
environment (dev, sit, uat or preprod; **never prod**, production is delivered from `main` only). Pull requests build and test only. Merge between the long-lived branches with `--ff-only`. Add `[skip ci]`
to commits that change only pipeline files while the Azure environment is not running.

## Rules

- The stage template holds no environment values and no defaults. Values come from `deploy/environments/<env>.yml`.
  The root file only passes the position in the pipeline (`dependsOn`, `previousStage`, `branchMatch`).
- No pipeline-level variable group. Each job loads only the group it needs: `goat-app-<env>` for deployment,
  `goat-app-integration` for tests. Application settings are one JSON string, `appSettingsJson`, in the group.
- Infra comes from the infra repo (`InfraRepo`, pinned to a tag) through `deploy/environments/<env>.yml@InfraRepo`,
  only when `deployInfraAll` is true. Run it before the application: infra deploys overwrite Bicep-managed settings.
- Slot delivery exists in the template but is off (`deploymentSlot: none`). Do not turn it on for prod until the
  infra repo's Bicep slot work is deployed.
- No automatic rollback in the pipeline. The smoke test only reports.
- Built-in tasks over scripts, native Environment approvals, no hardcoded names in logic, display names in plain
  English without icons, comments in English.
- Never commit secrets or keys.

## Before you commit pipeline YAML

```
python3 tools/verify_pipelines.py . ../goat-ticket-infra
```

## Working style

Reply in Vietnamese, concise, technical terms in English. Show files one by one in full. Do not change application code
or deploy unless asked. Cite official documentation when asked and say plainly when a page does not state something.
