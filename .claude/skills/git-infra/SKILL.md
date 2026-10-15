---
name: "git-infra"
description: "Verify, commit and push work in the goat-ticket-infra repository, and move changes between develop, release/*, main and hotfix/* the Git Flow way. Writes the commit message itself from the diff. Use for any commit, push, release, promote, hotfix or infra template tag in the infra repository."
argument-hint: "nothing (commit current work) | release <version> | promote | hotfix <name> | tag <version> | status"
user-invocable: true
disable-model-invocation: false
---

# git-infra

One repository: `qlap04/goat-ticket-infra`, work branch `develop`. The application repository has its own
skill, `git-app`. This skill never touches it.

Locate the repository by its remote URL, not by directory name. It has been seen nested (`infra/` inside the
application repository, ignored by it) and side by side:

```
for d in . infra ../goat-ticket-infra ../infra; do
  git -C "$d" remote get-url origin 2>/dev/null | sed "s|^|$d -> |"
done
```

Take the one whose URL ends in `goat-ticket-infra`; call its path `<repo>`. If none matches, stop. This skill lives
at the workspace root and is started from there, so run every git command with `-C <repo>` and give every file
path as `<repo>/...`: the shell's working directory persists between calls and has produced wrong-repo commands here.

## Modes

| Argument | What it does |
|---|---|
| *(none)* | Commit the current work on `develop`, then push |
| `release <version>` | Cut `release/<version>` from `develop` and push it |
| `promote` | Fast-forward `main` from the current `release/*`, tag, push, merge back into `develop` |
| `hotfix <name>` | Cut `hotfix/<name>` from `main` and push it |
| `tag <version>` | Create and push `infra-templates-v<version>` |
| `status` | Report only. Change nothing, push nothing |

---

## Step 1 - Establish the real remote state. Never skip this.

```
git -C <repo> fetch --prune origin
git -C <repo> status --short
git -C <repo> for-each-ref --format='%(refname) %(objectname:short)' refs/heads refs/remotes
git -C <repo> rev-list --left-right --count HEAD...@{upstream}
git -C <repo> merge-base --is-ancestor @{upstream} HEAD
```

If the last command fails, **stop and report**: the local branch and the remote branch are unrelated lineages,
not merely behind, and pushing would need `--force`. Only trust the output of `fetch` and `ls-remote`, never a
decoration in `git log`.

## Step 2 - Check what exists before creating anything

| Mode | Check, and what to do |
|---|---|
| none | `git status --short` empty: report "nothing to commit" and stop. No empty commits |
| `release <version>` | `git -C <repo> ls-remote --heads origin release/<version>` and `git -C <repo> branch --list release/<version>`: if either exists, stop and report |
| `promote` | List `release/*` on the remote (`git -C <repo> ls-remote --heads origin 'release/*'`). None: stop. More than one: ask which. Exactly one: use it |
| `hotfix <name>` | Same existence check as `release`, for `hotfix/<name>` |
| `tag <version>` | `git -C <repo> ls-remote --tags origin infra-templates-v<version>` and `git -C <repo> tag --list`: if it exists, stop. A pushed tag is never moved or overwritten, the application pipeline pins to it |

## Step 3 - Refuse to commit key material

```
git -C <repo> diff --name-only HEAD
git -C <repo> ls-files --others --exclude-standard
```

Reject the commit if any path matches `*.pem`, `*.pfx`, `*.key`, `*.p12`, or if any changed file contains
`BEGIN` followed by `PRIVATE KEY`. Confirm `.gitignore` still covers those patterns. Deleting a leaked key in a
later commit does not remove it from published history.

## Step 4 - Verify before committing

Run what applies to the changed files and report the real output. Do not commit on a failure unless the user says to.

```
az bicep build --file <repo>/deploy/bicep/main.bicep --stdout >/dev/null
for f in <repo>/deploy/variables/*.bicepparam; do
  ABC_JSON='{}' az bicep build-params --file "$f" --stdout >/dev/null || echo "FAILED $f"
done
```

`build-params` catches a parameter assigned in a `.bicepparam` but not declared in `main.bicep`. It runs over every
file in the folder, so a new environment is covered without editing this skill. Any Bicep warning is reported.

When any file under `<repo>/deploy/pipeline/` changed:

```
REPO=<repo> python3 - <<'EOF'
import glob, os, yaml
for f in glob.glob(os.environ['REPO'] + '/deploy/pipeline/**/*.yml', recursive=True):
    try:
        data = yaml.safe_load(open(f))
    except Exception as e:
        print('YAML ERROR', f, str(e)[:200]); continue
    # A variables template must start with a "variables:" key.
    if f.endswith(('environment-variables.yml',)) and 'variables' not in (data or {}):
        print('MISSING variables: KEY', f)
EOF
```

This only proves the files parse. It is not Azure DevOps: template expressions, macros and service connections
are checked only when the pipeline runs. Say so in the report when pipeline files changed.

## Step 5 - The cross-repository contract

If anything under `<repo>/deploy/pipeline/templates/` changed, the application pipeline is still pinned to the old
templates. The infra repository needs a new tag, and the application repository needs `ref` bumped in
`deploy/pipeline/azure-pipelines-app.yml`. This skill creates the tag (mode `tag`, or offered after a commit) and
**reports** that `git-app` must bump `ref`. Never edit the application repository from here.

Propose the version, then wait for the user to choose it:

- A parameter removed, renamed or made required, or a template removed: MAJOR
- A parameter added with a default, or a new capability: MINOR
- A fix with no change in how the template is called: PATCH

## Step 6 - Commit

Write the message yourself from the diff, without asking. One commit.

- Conventional prefix: `feat`, `fix`, `refactor`, `ci`, `chore`, `docs`.
- A short subject in the imperative, then a body that says why, grouped by area. State the behaviour that
  changes, not the files that changed. Where a change fixes something wrong, say what was wrong.
- End with the attribution line the current session requires for commits.
- Add `[skip ci]` only when the change touches pipeline files alone and the Azure environment is torn down.

Show the message in the report. Do not stop to ask for approval of it.

## Step 7 - Push

Fast-forward only:

```
git -C <repo> push origin HEAD:<branch>
```

Never `--force`. If the push is rejected, go back to step 1: the remote moved or the lineage diverged, and the
answer is to rebase or re-apply the work on top of the remote. `--force-with-lease` only when the user has been
shown what would be discarded and said to discard it. Report ranges, for example `341f052..a695938  develop`.

---

## Branch flow

```
develop ──┬─► release/X ──► main ──► tag
          │                  │
          └◄─────────────────┘  merge back
main ──► hotfix/Y ──► main + develop
```

- `develop` is where work lands. The infra pipeline is `trigger: none` and has no `pr:` block, so nothing runs on
  a push or a pull request; every run is started by hand.
- A run delivers the five stages (dev, sit, uat, preprod, prod) in that order, each after the one before it succeeds,
  and the Environment approval is the gate; the branch it starts from does not choose. Starting a run is a decision, not a side effect.
- `release/*` and `hotfix/*` are transient: create when needed, delete after the merge.
- `main` only receives a fast-forward merge from a `release/*` or `hotfix/*`. Never push work straight to it.
- A hotfix merges into both `main` and `develop`, or the fix is lost at the next release. It never goes to prod
  directly; production is delivered from `main` only.

### release <version>

```
git -C <repo> checkout develop && git -C <repo> pull --ff-only
git -C <repo> checkout -b release/<version>
git -C <repo> push -u origin release/<version>
```

### promote

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> merge --ff-only release/<version>
git -C <repo> push origin main
git -C <repo> checkout develop && git -C <repo> merge --ff-only main
git -C <repo> push origin develop
```

If `--ff-only` refuses, `main` has commits `release/*` does not. Report it; no merge commit or force push without asking.

### hotfix <name>

```
git -C <repo> checkout main && git -C <repo> pull --ff-only
git -C <repo> checkout -b hotfix/<name>
git -C <repo> push -u origin hotfix/<name>
```

### tag <version>

```
git -C <repo> tag infra-templates-v<version>
git -C <repo> push origin infra-templates-v<version>
```

---

## Report at the end

- Branch, commit range pushed, number of files, and the commit message used
- Verification actually run, with its result, and what it cannot prove
- Whether the template tag was created, and if `git-app` still has to bump `ref`
- Anything deliberately left out

## Raise with the user instead of deciding alone

- A divergent lineage, or any push that would need `--force`
- A verification step that fails
- The version number of a template tag
- Fast-forwarding `main`, `release/*` or `hotfix/*` onto a new `develop`: those branches may hold an older pipeline on purpose
- Deleting a remote branch. GitHub refuses to delete the default branch, so change the default in settings first
- Touching Key Vaults whose names start with `kv-inventory`: they belong to another project
