# goat-ticket-app

.NET 8 application (API on App Service for Linux, worker on Azure Functions) and its delivery pipeline.
Read `docs/PROJECT_CONTEXT.md` first: it says what is done, what is only designed, and the mentor's hard rules.

## Layout

```
deploy/pipeline/azure-pipelines-app.yml          root pipeline, manual run, five environment stages
deploy/pipeline/templates/build-test.yml         build once, unit tests, container tests, artifact "drop"
deploy/pipeline/templates/deploy-env-stage.yml   one environment: optional infra, application, smoke test
src/  tests/  GoatTicket.sln                     application code
```

## Branches

`develop` leads to DEV; `release/*` to SIT then UAT; `main` to Pre-PROD then PROD; `hotfix/*` to one chosen
environment (dev, sit, uat or preprod; **never prod**, production is delivered from `main` only). Pull requests build and test only. Merge between the long-lived branches with `--ff-only`. Add `[skip ci]`
to commits that change only pipeline files while the Azure environment is not running.

## Rules

- Only what varies is a template parameter. The service connection, the variable groups and the stack are the same
  for every stage, so they are fixed in the template. Resource names and addresses come from the `goat-app-<env>`
  variable group at run time, because Azure decides what an instance is called.
- No pipeline-level variable group. Each job loads only the group it needs: `goat-app-<env>` for deployment,
  `goat-app-integration` for tests. Application settings are one JSON string, `appSettingsJson`, in the group.
- Infra comes from the infra repo (`InfraRepo`, pinned to a tag) through
  `deploy/pipeline/templates/deploy-infra-jobs.yml@InfraRepo`, only when `deployInfraAll` is true. It runs first in the
  stage: an infrastructure deploy replaces the application settings the pipeline wrote.
- Secrets reach Key Vault through the `createKeyVaultSecrets` toggle, off by default. No secret passes through Bicep.
- Slot delivery exists in the template but is off (`deploymentSlot: none`). Do not turn it on for prod until the
  infra repo's Bicep slot work is deployed.
- No automatic rollback in the pipeline. The smoke test only reports.
- Built-in tasks over scripts, native Environment approvals, no hardcoded names in logic, display names in plain
  English without icons, comments in English.
- Never commit secrets or keys.

## Working style

Reply in Vietnamese, concise, technical terms in English. Show files one by one in full. Do not change application code
or deploy unless asked. Cite official documentation when asked and say plainly when a page does not state something.
