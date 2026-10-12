---
name: "git-app"
description: "Verify, commit and push work in the goat-ticket-app repository, and keep develop, release/*, main, hotfix/* and feature/* in step the Git Flow way. Writes the commit message itself, explains each git step in plain Vietnamese, and has a sync mode that shows how the branches relate and fast-forwards what is safe. Use for any commit, push, sync, release, promote, hotfix or feature branch in the application repository."
argument-hint: "nothing (commit current work) | status | sync | feature <name> | release <version> | promote | hotfix <name> | finish-hotfix <name>"
user-invocable: true
disable-model-invocation: false
---

# git-app

One repository: `qlap04/goat-ticket-app`, work branch `develop`. The infrastructure repository has its own skill,
`git-infra`. This skill never touches it: the folder `infra/` and `claude-skill/` inside this checkout are ignored by
this repository on purpose.

The owner is not fluent in git. Report in Vietnamese, say in one plain sentence what each git step does and why, and
never stop at a bare command or a bare error. Technical terms stay in English.

Locate the repository by its remote URL, not by directory name:

```
for d in . infra ../goat-ticket-app; do
  git -C "$d" remote get-url origin 2>/dev/null | sed "s|^|$d -> |"
done
```

Take the one whose URL ends in `goat-ticket-app`; call its path `<repo>`. If none matches, stop. Run every git command
with `-C <repo>` and give every file path as `<repo>/...`: the shell's working directory persists between calls and has
produced wrong-repo commands here.

## What each branch is for

| Branch | Lives | Delivered to | Receives work from |
|---|---|---|---|
| `develop` | long-lived | DEV | feature branches, direct commits |
| `release/<version>` | transient | SIT, then UAT | cut from `develop` |
| `main` | long-lived | Pre-PROD, then PROD | fast-forward from a `release/*` or `hotfix/*` only |
| `hotfix/<name>` | transient | one chosen environment, never PROD | cut from `main` |
| `feature/<name>` | transient | nothing (a pull request builds and tests only) | cut from `develop` |

Both pipelines are `trigger: none`: pushing a branch deploys nothing, a person starts every run. Production is delivered
from `main` only.

## Modes

| Argument | What it does |
|---|---|
| *(none)* | Commit the current work on the branch it is on (`develop` or a `feature/*`), then push |
| `status` | Report only: the branch map and the next sensible step. Changes nothing, pushes nothing |
| `sync` | Fetch, then fast-forward every local branch that is only behind its remote; report the rest, change nothing else |
| `feature <name>` | Cut `feature/<name>` from `develop` and push it |
| `release <version>` | Cut `release/<version>` from `develop` and push it |
| `promote` | Fast-forward `main` from the current `release/*`, push, then bring `main` back into `develop` |
| `hotfix <name>` | Cut `hotfix/<name>` from `main` and push it |
| `finish-hotfix <name>` | Fast-forward `main` from `hotfix/<name>`, push, then bring `main` back into `develop` |

---

## Step 1 - Establish the real remote state. Never skip this.

```
git -C <repo> fetch --prune origin
git -C <repo> status --short
git -C <repo> for-each-ref --format='%(refname:short) %(objectname:short) -> %(upstream:short) %(upstream:track)' refs/heads
git -C <repo> branch -r
```

For the branch about to be changed, prove the local branch descends from its remote:

```
git -C <repo> merge-base --is-ancestor @{upstream} HEAD
```

If that fails, **stop and report**: the local and the remote branch are unrelated lines of history, not merely behind,
and pushing would need `--force`. Only trust the output of `fetch` and `ls-remote`, never a decoration in `git log`.

## Step 2 - Check what exists before creating anything

| Mode | Check, and what to do |
|---|---|
| none | `git status --short` empty: report "không có gì để commit" and stop. No empty commits |
| `feature`, `release`, `hotfix` | The branch name must not exist locally (`git branch --list`) or on the remote (`git ls-remote --heads origin <name>`). If it does, stop and report |
| `promote` | List `release/*` on the remote. None: stop. More than one: ask which. Exactly one: use it |
| `finish-hotfix` | `hotfix/<name>` must exist on the remote and `main` must be an ancestor of it (`git merge-base --is-ancestor origin/main origin/hotfix/<name>`). If not, stop and report: `main` moved since the cut |

## Step 3 - Refuse to commit key material

```
git -C <repo> diff --name-only HEAD
git -C <repo> ls-files --others --exclude-standard
```

Reject the commit if any path matches `*.pem`, `*.pfx`, `*.key`, `*.p12`, or if any changed file contains `BEGIN`
followed by `PRIVATE KEY`. `.gitignore` covers `*.pem`, `*.pfx` and `*.key` but **not** `*.p12`: say so and offer to add
it. A private key was once leaked from this repository, and deleting it in a later commit does not remove it from the
published history. Secrets belong in the `goat-ticket-secrets-<env>` and `goat-app-integration` variable groups.

## Step 4 - Verify before committing

Run what applies to the changed files and report the real output. Do not commit on a failure unless the user says to.

**C# code or project files changed:**

```
dotnet build <repo>/GoatTicket.sln -c Release
dotnet format <repo>/GoatTicket.sln --verify-no-changes
for p in $(find <repo>/tests -name '*UnitTests.csproj'); do dotnet test "$p" -c Release --no-build; done
```

Unit tests must stay free of Docker. `GoatTicket.ContractTests` and `GoatTicket.IntegrationTests` need containers and
run only in the pipeline; do not run them locally unless Docker is up.

**Any file under `<repo>/deploy/pipeline/` changed:**

```
REPO=<repo> python3 - <<'EOF'
import glob, os, yaml
for f in glob.glob(os.environ['REPO'] + '/deploy/pipeline/**/*.yml', recursive=True):
    try:
        yaml.safe_load(open(f)); print('YAML OK', os.path.relpath(f, os.environ['REPO']))
    except Exception as e:
        print('YAML ERROR', f, str(e)[:200])
EOF
```

Then check that the infrastructure tag the pipeline pins to exists (read only):

```
ref=$(grep -m1 'ref: refs/tags/infra-templates-' <repo>/deploy/pipeline/azure-pipelines-app.yml | sed 's|.*refs/tags/||')
git ls-remote --tags https://github.com/qlap04/goat-ticket-infra.git "$ref"
```

An empty answer means the pinned tag does not exist: stop and report. This only proves the files parse and the tag
exists. It is not Azure DevOps: template expressions, macros and service connections are checked only when a pipeline
runs. Say so in the report when pipeline files changed.

## Step 5 - Commit

Write the message yourself from the diff, without asking. One commit.

- Conventional prefix: `feat`, `fix`, `refactor`, `ci`, `chore`, `docs`, `test`.
- A short subject in the imperative, then a body that says why, grouped by area. State the behaviour that changes, not
  the files that changed. Where a change fixes something wrong, say what was wrong.
- End with the attribution line the current session requires for commits.
- Add `[skip ci]` only when the change touches pipeline files alone and the Azure environment is torn down.

Show the message in the report. Do not stop to ask for approval of it. Stage with `git -C <repo> add -A`, which skips
the ignored folders, but read `git status --short` first and report any file you did not expect.

## Step 6 - Push

Fast-forward only:

```
git -C <repo> push origin HEAD:<branch>
```

Never `--force`. If the push is rejected, go back to step 1: the remote moved or the history diverged, and the answer is
to re-apply the work on top of the remote, not to overwrite it. `--force-with-lease` only when the user has been shown
what would be discarded and said to discard it. Report ranges, for example `341f052..a695938  develop`.

---

## status

Read only. Fetch, then build this table for `develop`, `main` and every `release/*`, `hotfix/*` and `feature/*` branch
on the remote:

```
git -C <repo> rev-list --left-right --count origin/main...origin/<branch>     # behind main / ahead of main
git -C <repo> rev-list --left-right --count <branch>...origin/<branch>        # local vs remote, when a local copy exists
```

| Branch | Local | Remote | Compared with `main` | Meaning |
|---|---|---|---|---|

Then say in plain Vietnamese what the picture means and what the next step is. Reading rules:

- `develop` ahead of `main` is normal: it is work waiting for a release.
- `main` ahead of `develop` is not normal: `develop` is missing something, bring `main` back into it.
- A `release/*` or `hotfix/*` that equals `main` is finished or a demo leftover; offer to delete it (ask first).
- A local branch that is only behind its remote is safe to fast-forward: that is `sync`.
- A local branch that is ahead has work not pushed yet; a diverged one needs a human decision.

## sync

Goal: make the local branches match the remote without losing anything. Changes only what is a pure fast-forward.

1. `git -C <repo> fetch --prune origin`.
2. For each local branch with an upstream, classify with `rev-list --left-right --count <branch>...<upstream>`:
   - `0 0` in step: nothing to do.
   - `0 N` behind: fast-forward it. The checked-out branch needs a clean working tree and `git -C <repo> pull --ff-only`;
     any other branch uses `git -C <repo> fetch origin <branch>:<branch>`, which refuses anything that is not a
     fast-forward.
   - `N 0` ahead: report "có commit chưa push" and do not push (that is the none or `feature` mode's job).
   - `N M` diverged: report and **do not touch**. Explain which side has what and ask.
3. A remote `develop` or `main` with no local copy: offer `git -C <repo> branch --track <name> origin/<name>`.
   `release/*` and `hotfix/*` are made locally only when the user asks.
4. A local branch whose remote was deleted: list it, never delete it without asking.
5. Finish with the `status` table and the flow gaps (`main` ahead of `develop`, hotfix not yet in `develop`).

A dirty working tree: do not stash, reset or checkout. Say which files block the sync and stop.

---

## Branch flow

```
feature/x ─► develop ──┬─► release/X ──► main ──► tag
                       │                  │
                       └◄─────────────────┘  main comes back into develop
main ──► hotfix/Y ──► main ──► develop
```

Rules that hold for this project:

- `main` only ever receives a fast-forward from a `release/*` or a `hotfix/*`. Never push work straight to it.
- A hotfix reaches **both** `main` and `develop`, or the fix is lost at the next release.
- `release/*`, `hotfix/*` and `feature/*` are transient: create when needed, delete after the merge.
- `hotfixEnv` has no `prod` value: a hotfix reaches Pre-PROD at most; production is delivered from `main` only.
- A branch is brought into another with `--ff-only`. If that is refused, report why and ask; never reach for a merge
  commit or a force push on your own.

### feature <name>

```
git -C <repo> checkout develop && git -C <repo> pull --ff-only
git -C <repo> checkout -b feature/<name>
git -C <repo> push -u origin feature/<name>
```

A finished feature reaches `develop` by a pull request on GitHub (it builds and tests, never deploys).

### release <version>

```
git -C <repo> checkout develop && git -C <repo> pull --ff-only
git -C <repo> checkout -b release/<version>
git -C <repo> push -u origin release/<version>
```

Pushing this branch deploys nothing. Run the pipeline by hand from the branch to reach SIT, then UAT.

### promote

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> merge --ff-only release/<version>
git -C <repo> push origin main
git -C <repo> checkout develop && git -C <repo> pull --ff-only
git -C <repo> merge --ff-only main
git -C <repo> push origin develop
```

If the first `--ff-only` refuses, `main` has commits the release does not: report it. If the second refuses,
`develop` has work of its own that `main` lacks (the usual case once development goes on): **stop and ask**. The options
are a merge commit or cherry-picking what `main` gained; explain both in plain words and let the owner choose.

### hotfix <name>

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> checkout -b hotfix/<name>
git -C <repo> push -u origin hotfix/<name>
```

Deliver it by running the pipeline by hand from the branch with `hotfixEnv` set.

### finish-hotfix <name>

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> merge --ff-only hotfix/<name>
git -C <repo> push origin main
```

Then bring `main` into `develop` exactly as in `promote`, with the same stop-and-ask rule.

---

## Report at the end

- Per branch touched: its name, the range pushed (`341f052..a695938`), the number of files, and the commit message used
- What was verified, with its result, and what it cannot prove
- The branch map after the change, in plain Vietnamese
- Whether the pinned infrastructure tag exists, and whether `git-infra` has a newer tag worth pinning
- Anything deliberately left out

## Raise with the user instead of deciding alone

- Unrelated histories, or any push that would need `--force`
- A verification step that fails
- A diverged branch, or `develop` refusing a fast-forward from `main`
- Fast-forwarding `main`, `release/*` or `hotfix/*` onto a new `develop`: those branches may hold an older pipeline on
  purpose (`release/1.0.0` and `hotfix/mock-test` are kept as pipeline demonstrations and equal `main`)
- Deleting any branch, local or remote. GitHub refuses to delete the default branch, so change the default in the
  repository settings first
- Bumping the infrastructure tag in `azure-pipelines-app.yml`: the version comes from the owner
