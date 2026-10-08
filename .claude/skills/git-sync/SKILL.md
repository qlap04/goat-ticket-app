---
name: "git-sync"
description: "Verify, commit and push work across the goat-ticket-app and goat-ticket-infra repositories, and move changes between develop, release/*, main and hotfix/* the Git Flow way. Use for any commit, push, release, promote or hotfix in this project."
argument-hint: "nothing (commit current work) | release <version> | promote | hotfix <name> | status"
user-invocable: true
disable-model-invocation: false
---

# git-sync

Two repositories, one delivery flow:

| Repository | Work branch |
|---|---|
| `qlap04/goat-ticket-app` | `develop` |
| `qlap04/goat-ticket-infra` | `develop` |

Locate both before doing anything, rather than assuming a layout. They have been
seen nested (`infra/` inside the app repository, ignored by it) and side by side
(`../goat-ticket-infra`):

```
for d in . infra ../goat-ticket-infra ../infra; do
  git -C "$d" remote get-url origin 2>/dev/null | sed "s|^|$d -> |"
done
```

Match on the remote URL, not on the directory name. Then run every git command
with `-C <repo path>`: the shell's working directory persists between calls and
has produced wrong-repo commands here before.

## Modes

| Argument | What it does |
|---|---|
| *(none)* | Commit the current work on `develop` in whichever repository changed, then push |
| `release <version>` | Cut `release/<version>` from `develop` and push it |
| `promote` | Fast-forward `main` from the current `release/*`, tag, push, merge back into `develop` |
| `hotfix <name>` | Cut `hotfix/<name>` from `main` and push it |
| `status` | Report only. Change nothing, push nothing |

---

## Step 1 — Establish the real remote state. Never skip this.

For each repository:

```
git -C <repo> fetch --prune origin
git -C <repo> status --short
git -C <repo> for-each-ref --format='%(refname) %(objectname:short)' refs/heads refs/remotes
git -C <repo> rev-list --left-right --count HEAD...@{upstream}
```

Then **prove the local branch descends from its remote**:

```
git -C <repo> merge-base --is-ancestor @{upstream} HEAD
```

If that fails, **stop and report**. It means the local branch and the remote
branch are unrelated lineages, not merely behind. This has happened in this
project: the workspace once held a freshly initialised repository with one commit
pointing at the same remote URL as a 39-commit history. Pushing would have needed
`--force` and destroyed those 39 commits.

Two further traps to check for, both of which have occurred here:

- **A stale remote ref.** `git log` can show `origin/develop` at a commit the
  remote no longer has, because the remote was rewritten. Only trust the output
  of `fetch` and `ls-remote`, never a decoration in `git log`.
- **The local baseline being older than the remote.** Before copying any file
  between lineages, diff the remote's version against the local committed version
  of the same file. If they differ, re-apply the intended change to the remote's
  version instead of overwriting it. Copying a whole file once came close to
  reverting the Cosmos serializer fix in `AzureClients.cs`.

## Step 2 — Refuse to commit key material

Before staging anything:

```
git -C <repo> diff --cached --name-only
git -C <repo> ls-files --others --exclude-standard
```

Reject the commit if any path matches `*.pem`, `*.pfx`, `*.key`, `*.p12`, or if
any staged file's first line contains `BEGIN` and `PRIVATE KEY`. Confirm
`.gitignore` still covers those patterns in both repositories; a private key was
leaked from the app repository once, and deleting it in a later commit did not
remove it from the published history.

Signing keys belong in the `goat-ticket-secrets-<env>` variable group, written to
Key Vault by the application pipeline's `createKeyVaultSecrets` toggle. No secret
passes through Bicep.

## Step 3 — Verify before committing

Run what applies to the changed files, and report real output. Do not commit on a
failure unless the user says to.

**Infrastructure repository**

```
cd infra/deploy/bicep  && az bicep build --file main.bicep --outfile /dev/null
cd infra/deploy/variables && for f in dev sit uat preprod prod; do \
  az bicep build-params --file $f.bicepparam --stdout >/dev/null; done
```

`az bicep build-params` is the guard that catches a parameter assigned in a
`.bicepparam` but no longer declared in `main.bicep` (BCP258/BCP259). It has
caught dead parameters here twice.

**Application repository**

```
dotnet build GoatTicket.sln -c Release
dotnet test tests/GoatTicket.UnitTests/GoatTicket.UnitTests.csproj -c Release --no-build
```

`GoatTicket.UnitTests` must stay free of Docker. The suites that need containers
live in `GoatTicket.IntegrationTests` and `GoatTicket.ContractTests`; those only
run in the pipeline, so do not try to run them locally unless Docker is up.

**Either repository, when any pipeline YAML or `.bicepparam` changed**

```
```

All checks must pass. Occasionally inject one deliberate mistake into a throwaway
copy and confirm the checker still catches it; it is a subset simulator of Azure
DevOps, not Azure DevOps.

## Step 4 — The cross-repository contract

If anything under `infra/deploy/pipeline/templates/` changed, the application pipeline is
still pinned to the old templates and will not see it. Both of these, or neither:

```
git -C infra tag infra-templates-vX.Y.Z
git -C infra push origin infra-templates-vX.Y.Z
```

and bump `ref: refs/tags/infra-templates-vX.Y.Z` in `deploy/pipeline/azure-pipelines-app.yml`
under `resources.repositories`. Raise it with the user before choosing the
version. Changing one without the other is silently wrong: the pipeline keeps
compiling, against the previous templates.

## Step 5 — Commit

One commit per repository. Conventional prefix (`feat`, `fix`, `refactor`, `ci`,
`chore`, `docs`), then a body that says **why**, grouped by area. State the
behaviour that changes, not the files that changed — `git show --stat` already
lists files. Where a change fixes something that was wrong, say what was wrong.

End every commit message with:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

Add `[skip ci]` only when the change cannot affect a pipeline run and the Azure
environment is torn down.

## Step 6 — Push

Fast-forward only:

```
git -C <repo> push origin HEAD:<branch>
```

Never `--force`. If the push is rejected, go back to step 1: the rejection means
the remote moved or the lineage diverged, and the answer is to rebase or to
re-apply the work on top of the remote, not to overwrite it.

`--force-with-lease` is allowed in exactly one case: the user has been shown what
would be discarded and has said to discard it.

Report what moved, as ranges: `341f052..a695938  develop`.

---

## Branch flow

```
develop ──┬─► release/X ──► main ──► tag
          │                  │
          └◄─────────────────┘  merge back
main ──► hotfix/Y ──► main + develop
```

Rules that hold for this project:

- `develop` is where work lands. Neither pipeline runs on a push: both are
  `trigger: none`, so every run is started by hand. Pull requests into `develop`,
  `main` and `release/*` still build and test, and never deploy.
- `release/*` and `hotfix/*` are **transient**: create them when releasing or
  fixing, delete them after the merge. The app repository happens to keep
  `release/1.0.0` and `hotfix/mock-test` for demonstrating the pipeline; that is
  a demo artefact, not the pattern to copy.
- `main` only ever receives a merge from a `release/*` or a `hotfix/*`, always
  `--ff-only`. Never push work straight to `main`.
- A hotfix merges into **both** `main` and `develop`, or the fix is lost at the
  next release.
- `main`, `release/*` and `hotfix/*` do **not** trigger the pipeline. Those runs
  are started by hand, which is deliberate: a release is a decision.
- Production is delivered from `main` only. `hotfixEnvironment` has no `prod`
  value, in the root pipeline and in the stage template.

### `release <version>`

```
git -C <repo> checkout develop && git -C <repo> pull --ff-only
git -C <repo> checkout -b release/<version>
git -C <repo> push -u origin release/<version>
```

Pushing this branch does not deploy. Run the pipeline by hand to see SIT and UAT,
with the approval on UAT.

### `promote`

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> merge --ff-only release/<version>
git -C <repo> push origin main
git -C <repo> checkout develop && git -C <repo> merge --ff-only main
git -C <repo> push origin develop
```

If `--ff-only` refuses, `main` has commits `release/*` does not. Report it; do not
reach for a merge commit or a force push without asking.

### `hotfix <name>`

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> checkout -b hotfix/<name>
git -C <repo> push -u origin hotfix/<name>
```

Deliver it by running the pipeline by hand with `hotfixEnvironment` set. It
reaches pre-production at most. Then merge into `main` **and** `develop`.

---

## Report at the end

- Per repository: branch, commit range pushed, number of files
- Verification actually run, with its result
- Whether the infra template tag was bumped, and why or why not
- Anything deliberately left out

## Things to raise rather than decide alone

- A divergent lineage, or any push that would need `--force`
- A verification step that fails
- Which version number to use for an infra template tag
- Fast-forwarding `main`, `release/*` or `hotfix/*` onto a new `develop`: those
  branches may hold an older pipeline on purpose
- Deleting a remote branch. GitHub refuses to delete the default branch, so the
  default must be changed in the repository settings first
