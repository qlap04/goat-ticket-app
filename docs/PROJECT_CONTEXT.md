# GOAT Ticket: project context and handover

Last updated: 2026-10-08 (day 13 of the project; the pipelines are being implemented on Azure DevOps today). Written so that a fresh Claude Code session can continue
without the earlier chat history. Put this file at `docs/PROJECT_CONTEXT.md` in both repositories.

Read sections 4, 9 and 10 before changing anything. Section 4 says what is real and what is only designed.

---

## 1. What the project is

A portfolio project: an event ticket booking platform for a footballer's 1000th-goal match. About 20,000 seats,
flash-sale traffic. The goal is a full, secure Azure architecture with enterprise-grade CI/CD that the owner can
defend in interviews and in review by a mentor.

- Application: .NET 8 (API on App Service for Linux, worker on Azure Functions), Cosmos DB, Key Vault, Entra ID.
- Network: hub-and-spoke style with Azure Firewall, Application Gateway with WAF, private endpoints.
- Domain: `goatticket.kaidevops.online` (Cloudflare, DNS only, A record to the Application Gateway public IP).
  The certificate is self-signed (CN equals the domain), stored in Key Vault as `cert-goatticket`.
- Region: `southeastasia`. Infrastructure is Bicep at subscription scope.

Cost warning: Azure Firewall costs about 20 USD per day. The Azure free budget is nearly used up, so the working
pattern is: write and verify offline first, deploy only for demos, then delete both resource groups.

## 2. Repositories, branches, tags

| Repo | Default work branch | Notes |
|---|---|---|
| `qlap04/goat-ticket-infra` | `dev` (plan: rename to `develop`) | Bicep, infra pipeline, per-environment files |
| `qlap04/goat-ticket-app` | `develop` | Git Flow: `develop`, `main`, `release/1.0.0`, `hotfix/mock-test` all exist |

- Azure DevOps: `dev.azure.com/qlap04/goat-ticket`.
- The app pipeline pins the infra templates to the tag `infra-templates-v1.0.0`. Every template change needs a
  new tag and a bump in the app pipeline. While iterating, point `ref` at `refs/heads/dev` temporarily.
- Older infra tags `infra-v0.38.0` to `infra-v0.49.0` belong to the previous tag-triggered pipeline.

## 3. Azure and Azure DevOps objects

| Kind | Name | Notes |
|---|---|---|
| Resource groups | `rg-goat-network-dev`, `rg-goat-app-dev` | Deleted at the end of each session to save cost |
| Deployment stack | `stack-goat-dev` | One subscription-scope stack. In mock mode all five environments share it because they manage the same dev resources |
| Key Vault | `kv-goat-dev` | RBAC mode, public access normally Disabled. Soft-delete on: purge before recreating |
| Web apps | `app-goat-api-dev`, `func-goat-worker-dev` (Bicep creates a staging slot for each; only the API's is planned for use) | App Service Plan is Standard S1 (Basic has no slots) |
| Service connection (ARM) | `goat-ticket-connection` | Workload identity federation |
| Service connection (GitHub) | `qlap04` | |
| Variable groups | `goat-ticket-secrets-dev` (exists), `goat-ticket-secrets-prod` (exists, not used by the mock) | Infra secrets: `ticketQrSigningKeyValue`, `keyVaultCertSecretUri` |
| Variable groups to create | `goat-app-dev`, `goat-app-integration` | Each needs a variable `appSettingsJson` (a JSON string, see 5.6) |
| Environments | `goat-dev`, `goat-sit`, `goat-uat`, `goat-preprod`, `goat-prod` | Approval on uat, preprod, prod |
| Environment to create when slots are on | `goat-prod-slot` | No approval |
| Entra ID apps | `GoatTicket-Api`, `GoatTicket-Swagger` | IDs are in the Bicep files, not here |

Another project's Key Vaults (`kv-inventory-*`) exist in the same subscription. Never touch them.

## 4. Status

### 4.1 Done and working in Azure (days 1 to 12)

- All Bicep modules deployed (about 74 resources across the two resource groups).
- App pipeline deploys API and worker with zip deploy.
- Entra ID: Swagger OAuth2 login works (authorization code with PKCE). Lessons are in section 8.
- Application Gateway health probe fixed (custom probe on `/swagger/index.html`, referenced from the backend settings).
- WAF is in Detection mode because OWASP rules produced false positives on the OAuth redirect URL.
- Application settings moved into the Bicep `appSettings` array so redeploys do not wipe them.
- Infra repository restructured by a spec-driven run (`/speckit-*`): `deploy/{bicep,pipeline,script,variables}`,
  RBAC parent module with child modules using `for` loops over principal arrays, shared network ACLs through an
  `@export()` constant, staging slots, S1 plan.
- A leaked private key was removed from the whole Git history with `git filter-repo`; a new key was generated.
  `.gitignore` now excludes key material.

### 4.2 Written and verified offline; being implemented on Azure DevOps on 2026-10-08

The pipeline set described in section 5 (14 YAML files), now using Azure deployment stacks for infrastructure. It passes
deliberate mistakes injected into a copy (17, 21 and 17 cases, overlapping) every one was caught after the checker was
strengthened where the first round missed one. That checker is a subset simulator, not Azure DevOps, so the real
first runs may still surface differences (section 9, list B).

The repositories may still contain an earlier intermediate version of these files. Diff before overwriting.

### 4.3 Last known Azure state (verify with `az` before relying on it)

- Both resource groups exist with partial infrastructure.
- `kv-goat-dev` was purged and recreated; the certificate `cert-goatticket` was imported; the owner has the
  `Key Vault Certificates Officer` role on it. Public access was meant to be closed again.
- An infra deployment failed with `FirewallPolicyUpdateFailed` (see 8). The firewall was deleted; the firewall
  policy `afwp-goat-dev` was about to be deleted. The next step was to delete it, confirm both return
  `ResourceNotFound`, then rerun the infra pipeline.

### 4.4 Not started

- Bicep changes that slots need (section 5.5).
- `/health` endpoint and the matching probe and warm-up settings.
- Self-hosted agent inside the VNet (needed to deploy through the private SCM endpoint).
- Real certificate; production-grade secrets handling with Key Vault references.

## 5. CI/CD workflow

### 5.1 Branches to stages

| Branch | Stages that run (the other stages show as Skipped) | Approval |
|---|---|---|
| `develop` | DEV | none |
| `release/*` | SIT, then UAT | UAT |
| `main` | Pre-PROD, then PROD | Pre-PROD, PROD |
| `hotfix/*` | one environment chosen at run time: dev, sit, uat or preprod (default DEV). **Never prod** | per that environment |
| any other branch | build and integration tests only | none |
| pull request | build and integration tests only, never deploys | none |

Production is delivered from `main` only; the allow-list of `hotfixEnvironment` has no `prod`, both in the root file and
in the stage template. All five deployment stages are always defined; the branch decides which run. Mock mode: every environment still
delivers to the dev resources, so only the stage name, Environment and approval differ.

### 5.2 Application pipeline (`deploy/pipeline/azure-pipelines-app.yml`)

```
Stage Build (jobs run in parallel)
  BuildAndTest       restore, build, format check, vulnerability check, credential scan, unit tests,
                     publish API and worker zips as artifact "drop"
  IntegrationTests   containers, test-only settings from group goat-app-integration
Stage Deploy_<env>   dev, sit, uat, preprod, prod  (chained, each skippable)
  [optional] infra checks + infra deploy        only when parameter deployInfraAll is true
  direct mode (deploymentSlot none):  DeployApp_<env>  then  SmokeTest_<env>
  slot mode  (deploymentSlot staging): DeploySlot_<env>  then  SwapSlot_<env> (approval)  then  SmokeTest_<env>
```

- Parameters at run time: `deployInfraAll` (default false), `hotfixEnvironment` (dev, sit, uat, preprod).
- `trigger: none`: no push starts the application pipeline. Every run is started by hand, so a delivery is
  always a decision. Pull requests into `develop`, `main` and `release/*` still build and test, and the stage
  template refuses to deploy a pull request build.
- No automatic rollback (mentor decision). A red smoke test only reports. A person swaps the slots again or
  reruns an earlier run.

### 5.3 Infrastructure pipeline (`deploy/pipeline/azure-pipelines-infra.yml`, infra repo)

Independent of the app pipeline. Manual run, parameter `environment`. Jobs: `<name>_Checks` (validate, security scan,
what-if) then `<name>` (deployment job bound to the Environment).

All Bicep steps use `BicepDeploy@0` with `type: deploymentStack` at subscription scope, same stack name for validate,
preview and deploy, and the Bicep CLI pinned with `bicepVersion`. Stack values come from the environment file:
`stackName`, `actionOnUnmanageResources` and `actionOnUnmanageResourceGroups` (`detach` or `delete`),
`denySettingsMode` (`none`, `denyDelete`, `denyWriteAndDelete`) and `bypassStackOutOfSyncError`. Mock values:
`detach`, `detach`, `none`, `false`. Keep `denySettingsMode: none` while resources are repaired or deleted by hand:
deny settings would block the firewall repair in section 7 and a manual resource group delete. Pull requests run the checks only; the template
refuses to deploy a pull request build. Deployment is manual because of the firewall cost.

### 5.4 How values reach the templates (rule: templates hold no values)

```
deploy/pipeline/azure-pipelines-app.yml   ->  deploy/pipeline/templates/deploy-env-stage.yml    (app repo)
deploy/pipeline/azure-pipelines-infra.yml ->  deploy/pipeline/templates/deploy-infra-jobs.yml   (infra repo)
                                              deploy/variables/<env>.bicepparam  (Bicep values)
                                              goat-app-<env> variable group      (application values)
```

- Each environment file passes that environment's values (service connection, resource names, Environment names,
  variable group, smoke test URL) as template parameters.
- Template parameters have no defaults and `environment` has an allow-list, so a missing or misspelled value fails
  at compile time. Adding a real environment means editing one file.
- Contract between the repos: the app declares the infra repo as resource `InfraRepo`, calls
  `deploy/pipeline/templates/deploy-infra-jobs.yml@InfraRepo` with `repository: InfraRepo`, and then
  `dependsOn: InfraDeploy_<env>`.

### 5.5 Slot strategy (designed, switched off)

- Only the API uses a slot. The worker is a queue consumer and is deployed directly, before the API goes live, so it
  must read messages from both the old and the new API.
- Flow: write settings to the staging slot, deploy to the staging slot, approval, swap into production, smoke test.
  The approval sits on the swap job so it is asked after the slot is ready; this needs a second Environment without
  approval for the slot deploy job.
- Turn it on for prod by setting `deploymentSlot: staging` and `slotEnvironmentName: goat-prod-slot` in the app
  repo's `deploy/pipeline/azure-pipelines-app.yml`, in the prod row of `environments`. Do this only after the Bicep work below is deployed.
- Bicep work required first: the slot needs the same settings list as production (otherwise production receives
  missing settings after the swap), its own VNet integration, its own system-assigned identity with the same roles
  on Key Vault and Cosmos and anything else the app reaches by identity (add the slot principal to the RBAC arrays), and warm-up settings.
- Official documentation facts used: settings that are not slot settings move with the code; managed identity and
  VNet integration are not swapped; swap applies the production slot's sticky settings to the staging slot, restarts
  it and warms it up; warm-up default accepts any HTTP response, so set `WEBSITE_SWAP_WARMUP_PING_PATH` and
  `WEBSITE_SWAP_WARMUP_PING_STATUSES` to make it a real gate; auto swap is not supported on Linux; private endpoints
  are not cloned to slots. Source: learn.microsoft.com/azure/app-service/deploy-staging-slots.

### 5.6 Application settings strategy

- The pipeline writes application settings through the task `AzureAppServiceSettings@1`, whose input `appSettings`
  is one JSON string. That string is the variable `appSettingsJson`, defined in each variable group:
  - `goat-app-dev`: `[{"name":"Keyword","value":"A","slotSetting":false}]`
  - `goat-app-integration`: same names, mock values (for example `B`).
- YAML contains no setting names. If the variable is missing the task receives a non-JSON string and fails loudly.
- Ownership: the pipeline writes only settings that travel with the code (not slot-sticky). Settings that describe
  where the app runs (for example a database name) are owned by Bicep, together with `slotConfigNames`.
  Every Bicep infra deploy overwrites the settings Bicep manages, so run infra before the application.
- How to tell the two kinds apart: a value describing the place it runs is sticky; a value describing the code
  version (for example a feature flag) is not.
- Secrets do not belong in this JSON; use Key Vault references.
- Integration test input is still undecided (section 9, A1): either the test reads `APP_SETTINGS_JSON`, or the
  group holds one variable per setting and non-secret variables reach the test as environment variables.

### 5.7 Smoke test

A plain job after the application is live. It calls `smokeTestUrl` with `curl --fail` and retries. The mock domain
uses a self-signed certificate, so `--insecure` is on through `smokeTestAllowSelfSignedCertificate`; turn that off
when a real certificate exists. It needs the infrastructure running, so it fails when the resource groups are deleted.

## 6. One-time Azure DevOps setup checklist

| Item | Detail |
|---|---|
| Tag the infra templates | `git tag infra-templates-v1.0.0 && git push origin infra-templates-v1.0.0` (the app pipeline fails without it) |
| Variable groups | Create `goat-app-dev` and `goat-app-integration` with `appSettingsJson`; authorize both for the pipelines |
| Environments | Five exist; add `goat-prod-slot` only when slots are enabled |
| Approvals | On `goat-uat`, `goat-preprod`, `goat-prod`; allow approvers to approve their own runs |
| Branch control (Environment check) | prod: `refs/heads/main` only; preprod: `refs/heads/main`, `refs/heads/hotfix/*`; sit and uat: `refs/heads/release/*`, `refs/heads/hotfix/*` |
| Pipelines | Infra: `deploy/pipeline/azure-pipelines-infra.yml`. App: `azure-pipelines-app.yml` |
| First permits | The first run asks to permit variable groups, Environments and service connections |
| Delete old files | Infra: old stage template, `deploy/script/*.sh`, demo templates. App: demo and test pipelines |

First-run order: infra pipeline with `dev` (proves the template alone), then the app pipeline from `develop` with
`deployInfraAll` on, then `release/1.0.0` to see SIT and UAT.

## 7. Runbooks

### Fixes that worked in this project (commands as actually used)

CONFIRMED means the output was seen in the session; OBSERVED means a later step only succeeded because of it.

**1. Key Vault name still taken after the resource group was deleted** (soft-delete keeps the name). OBSERVED.
```
az keyvault list-deleted --query "[].name" -o tsv
az keyvault purge --name kv-goat-dev --location southeastasia      # only this vault, never kv-inventory-*
az keyvault list-deleted --query "[?name=='kv-goat-dev'].name" -o tsv   # empty means the name is free
```
The purge printed `InProgress`, and the next deployment created the vault again.

**2. Application Gateway fails with `ApplicationGatewayKeyVaultSecretNotFound`** because a new vault has no certificate.
CONFIRMED for the import (the output showed the certificate id and subject). Work in a temporary directory outside every repo:
```
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 365 -nodes -subj "/CN=goatticket.kaidevops.online"
openssl pkcs12 -export -out cert.pfx -inkey key.pem -in cert.pem -passout pass:
az keyvault update --name kv-goat-dev --public-network-access Enabled
az keyvault network-rule add --name kv-goat-dev --ip-address $(curl -s ifconfig.me)
az keyvault certificate import --vault-name kv-goat-dev --name cert-goatticket --file cert.pfx
```
Then close and clean up: `az keyvault network-rule remove --name kv-goat-dev --ip-address $(curl -s ifconfig.me)`,
`az keyvault update --name kv-goat-dev --public-network-access Disabled`, delete the temporary directory.

**3. `403 ForbiddenByRbac` on that import** (the vault is RBAC-only and the new vault has no role assignment for the owner).
```
az role assignment create --assignee-object-id <owner-object-id> --assignee-principal-type User \
  --role "Key Vault Certificates Officer" --scope $(az keyvault show --name kv-goat-dev --query id -o tsv)
```
Wait about 90 seconds, then import again. `az keyvault secret show` still returns 403 with this role (it lacks `getSecret`);
verify with `az keyvault certificate show --vault-name kv-goat-dev --name cert-goatticket` instead.

**4. How the Key Vault values reach Bicep** (CONFIRMED for the create operation: the deployment reached the
Application Gateway). Group `goat-ticket-secrets-<env>` holds `ticketQrSigningKeyValue` (secret) and
`keyVaultCertSecretUri` (secret URI without a version). Each Bicep task maps them with `env:` to
`TICKET_QR_SIGNING_KEY_VALUE` and `KEY_VAULT_CERT_SECRET_URI`, which the `.bicepparam` file reads with
`readEnvironmentVariable()`. Secret variables are not exported automatically, so the `env:` mapping is required.

**5. `FirewallPolicyUpdateFailed ... Put on Firewall Policy afwp-goat-dev Failed with 1 faulted referenced firewalls`**
(recurring race between the policy and the firewall). CONFIRMED for the firewall deletion (`ResourceNotFound` twice);
the policy deletion worked the same way on day 12.
```
az resource show -g rg-goat-network-dev -n afw-goat-dev --resource-type Microsoft.Network/azureFirewalls --query properties.provisioningState -o tsv
az resource delete -g rg-goat-network-dev -n afw-goat-dev --resource-type Microsoft.Network/azureFirewalls
az resource show -g rg-goat-network-dev -n afw-goat-dev --resource-type Microsoft.Network/azureFirewalls    # repeat until ResourceNotFound
az resource delete -g rg-goat-network-dev -n afwp-goat-dev --resource-type Microsoft.Network/firewallPolicies
az resource show -g rg-goat-network-dev -n afwp-goat-dev --resource-type Microsoft.Network/firewallPolicies   # until ResourceNotFound
```
Order matters: the firewall first, the policy second. `az network firewall delete ... --yes` fails with
`unrecognized arguments: --yes`; leave the flag out or use `az resource delete`. After this, rerun the infra pipeline.
Because resources were deleted outside the stack, if the next stack run reports that the stack is out of sync, set
`bypassStackOutOfSyncError: true` in that environment file for one run, then set it back to `false` (the input is
documented; whether this exact situation raises the error has not been observed).

**Redeploy from empty (order):** purge the vault (1), run the infra pipeline for `dev`, import the certificate (2, 3) as soon
as `kv-goat-dev` exists, rerun the infra pipeline, then run the app pipeline.

**Save money:** delete `rg-goat-network-dev` and `rg-goat-app-dev` after every demo (works while `denySettingsMode` is none).
Tearing down through the stack (`BicepDeploy@0` with `operation: delete`, or `az stack sub delete`) is the cleaner way once
the stack owns the resources; it has not been tried here, and the exact CLI flags were not checked.

## 8. Known issues and lessons

- The API and worker have `publicNetworkAccess: Disabled` in Bicep, so a Microsoft-hosted agent gets 403 on the
  deployment endpoint (SCM). Real fix: a self-hosted agent in the VNet. Until then open access by hand for a deploy.
- Bicep deploys overwrite app settings; keep them in Bicep or write them after infra.
- Service principals must be created separately for Entra app registrations made with the CLI; the owner must be added
  by hand; "Allow public client flows" is a separate switch from the SPA platform; OWASP rules in Prevention mode block
  the OAuth redirect URL.
- Bicep: `@export()` constants are the way to share a value when a module output cannot feed a `for` loop property
  (BCP178). Application Gateway: a defined probe has no effect unless the backend settings reference it.
- Azure DevOps: a template included with `@alias` is resolved at compile time and needs no checkout; a task that reads
  real files (`.bicep`, `.bicepparam`) does need a checkout, and a single checked-out repo lands directly in
  `$(Build.SourcesDirectory)` with no repo-named subfolder. A skipped stage or job makes dependants skip unless their
  condition allows `Skipped`. An unreplaced macro `$(x)` stays as literal text.
- `az network firewall delete` rejects `--yes`.
- Run `git status` before committing; never commit keys, `.pem`, `.pfx`.

## 9. Open decisions and unverified items

A. Decisions
1. Integration test settings: parse `APP_SETTINGS_JSON` (single source of truth, needs test code) or one group variable
   per setting (no test code, but the list lives in two places and can drift).
2. Sticky settings: is the `slotSetting` field of `AzureAppServiceSettings@1` reliable? There is a user report that it
   keeps its first value. Test it on the staging slot, or declare sticky names in Bicep with `slotConfigNames`.
3. Whether one approval per stage is asked once or twice when infra deploy and swap share an Environment.
4. Rename the infra branch `dev` to `develop`.
5. Whether the mentor accepts the smoke test as report-only.
6. Layering the infrastructure (proposal from the owner): deploy the network layer once and redeploy only the App Service and
   Function layer. Assessment so far: right direction, split by change frequency and risk, one deployment stack per layer
   (a resource can be managed by one stack). Constraints to resolve first: the Application Gateway depends on the Key Vault
   certificate and on the App Service host name, so Key Vault and the certificate must exist before it; private endpoints of
   the apps belong with the app layer; the app pipeline's `deployInfraAll` would deploy only the app layer and never the
   network. The mock deletes both resource groups after each session to save cost, so the network is recreated each time and
   the benefit appears only within a session or in a real environment. Do not start this before the single-stack pipeline has
   run end to end once. Needed work: split `main.bicep`, a `layer` parameter in the infra template, stack and parameter
   files per layer.

B. Unverified until the first real runs
- Nested relative template paths when the app calls the infra repo's files; expression plus `@InfraRepo` in a path.
- `checkout: ${{ parameters.repository }}` landing files in `$(Build.SourcesDirectory)`, also inside a deployment job.
- `BicepDeploy@0` reading secrets from `env:` for validate and what-if (it worked for create).
- Input names of `AzureAppServiceManage@0` on Linux; `AzureAppServiceSettings@1` on a Function App and with a slot.
- Deployment stacks: the task page lists create, validate and delete for stacks while the Bicep quickstart says to set
  `operation: whatIf` to preview a stack; the preview step uses `whatIf` and may need to fall back to `type: deployment`.
  Also unverified: the service connection's identity can create stacks, and a resource shared by five stack names in
  mock mode (all five environment files use one stack name on purpose).

## 10. Mentor requirements (treat as hard rules)

- Enterprise best practice for 2026, careful and complete; the mentor reviews and finds every sloppy detail.
- Plain professional English display names, no icons, English comments.
- Built-in tasks over inline scripts; native Environment approvals instead of manual validation gates.
- One infrastructure template, with its own independent pipeline; no values or environment defaults in templates;
  values come from per-environment files; no hardcoded names in logic.
- Variable groups scoped to the job that needs them; none at pipeline level.
- Pin shared template versions; pull requests never deploy; deployments need approval at uat, preprod, prod.
- Prod uses a slot and swaps to reduce downtime; the pipeline does not roll back.
- A hotfix can reach pre-production at most; production comes from `main` only.
- Infrastructure is delivered as Azure deployment stacks (not plain deployments), with the Bicep CLI pinned.
- Infrastructure is not redeployed on every app run; it is an explicit toggle.

## 11. How to work with the owner

- The owner writes Vietnamese mixed with English technical terms. Answer in Vietnamese, concise and direct.
- Show files one by one in full so they can be reviewed and edited; do not hand over zip archives.
- Do not change code or deploy unless asked: often the owner only wants the pipeline written.
- Cite official documentation (learn.microsoft.com, code.claude.com) when asked, with the exact page; if a page
  does not say something, say so. Mark anything unverified as unverified.
  occasionally to confirm the checker still catches it.
- Ask before widening scope. Prefer reducing parameters and duplication.

## 12. Tooling

expands the template subset used here, checks parameters, stage and job graph, which stages run for each branch,
that templates contain no concrete names, and that the environment files agree. It is not Azure DevOps and does not
check task behaviour.
