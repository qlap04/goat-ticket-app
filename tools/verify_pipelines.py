#!/usr/bin/env python3
"""Offline checks for the two pipeline repositories.

This is NOT Azure DevOps. It expands only the template syntax these files use
(parameters, if, each, template includes with @alias, convertToJson) and then checks:

  1. every YAML file parses
  2. every template call passes exactly the declared parameters, with allowed values and types
  3. the expanded stage and job graph is consistent (names, dependsOn, condition references)
  4. which stages run for each branch (develop, release, main, hotfix, pull request, other)
  5. templates hold no concrete names, and the environment files agree with each other

Usage: python3 verify_pipelines.py <app-repo-root> <infra-repo-root>
"""
import json
import re
import shutil
import sys
import tempfile
from pathlib import Path

import yaml

ENVS = ["dev", "sit", "uat", "preprod", "prod"]
HOTFIX_ENVS = ["dev", "sit", "uat", "preprod"]  # a hotfix never reaches production
EXPR = re.compile(r"\$\{\{\s*(.*?)\s*\}\}")
FULL_EXPR = re.compile(r"^\$\{\{\s*(.*?)\s*\}\}$")
DIRECTIVE = re.compile(r"^\$\{\{\s*(if|each)\s+(.*?)\s*\}\}$")


class CompileError(Exception):
    pass


# --------------------------------------------------------------------------- expressions
TOKEN = re.compile(
    r"""\s*(?:
        (?P<str>'(?:[^']|'')*')
      | (?P<num>-?\d+(?:\.\d+)?)
      | (?P<ref>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+|\['[^']*'\])*)
      | (?P<punc>[(),])
    )""",
    re.X,
)


def tokenize(text):
    pos, out = 0, []
    while pos < len(text):
        if not text[pos:].strip():
            break
        m = TOKEN.match(text, pos)
        if not m or m.end() == pos:
            raise CompileError(f"cannot tokenize {text[pos:pos + 40]!r} in {text!r}")
        pos = m.end()
        out.append((m.lastgroup, m.group(m.lastgroup)))
    return out


def _s(v):
    return v.lower() if isinstance(v, str) else v


def call(name, args):
    n = name.lower()
    if n == "eq":
        return _s(args[0]) == _s(args[1])
    if n == "ne":
        return _s(args[0]) != _s(args[1])
    if n == "and":
        return all(args)
    if n == "or":
        return any(args)
    if n == "not":
        return not args[0]
    if n == "in":
        return _s(args[0]) in [_s(a) for a in args[1:]]
    if n == "startswith":
        return str(args[0]).lower().startswith(str(args[1]).lower())
    if n == "converttojson":
        return json.dumps(args[0])
    if n == "upper":
        return str(args[0]).upper()
    if n == "lower":
        return str(args[0]).lower()
    if n == "contains":
        return _s(args[1]) in _s(args[0])
    if n == "coalesce":
        return next((a for a in args if a not in (None, "")), "")
    raise CompileError(f"unsupported function {name}")


def evaluate(text, resolve):
    tokens = tokenize(text)
    pos = [0]

    def peek():
        return tokens[pos[0]] if pos[0] < len(tokens) else (None, None)

    def take():
        t = peek()
        pos[0] += 1
        return t

    def expr():
        kind, val = take()
        if kind == "str":
            return val[1:-1].replace("''", "'")
        if kind == "num":
            return float(val) if "." in val else int(val)
        if kind == "ref":
            if peek() == ("punc", "("):
                take()
                args = []
                if peek() == ("punc", ")"):
                    take()
                else:
                    while True:
                        args.append(expr())
                        k = take()
                        if k == ("punc", ")"):
                            break
                        if k != ("punc", ","):
                            raise CompileError(f"expected , or ) in {text!r}")
                return call(val, args)
            low = val.lower()
            if low == "true":
                return True
            if low == "false":
                return False
            if low == "null":
                return None
            return resolve(val)
        raise CompileError(f"unexpected token {kind}:{val} in {text!r}")

    result = expr()
    if pos[0] != len(tokens):
        raise CompileError(f"trailing tokens in {text!r}")
    return result


def make_resolver(scope, strict=True):
    def resolve(ref):
        parts = re.findall(r"[A-Za-z_][A-Za-z0-9_]*|\['[^']*'\]", ref)
        cur = scope
        for p in parts:
            key = p[2:-2] if p.startswith("['") else p
            if isinstance(cur, dict) and key in cur:
                cur = cur[key]
            elif strict:
                raise CompileError(f"unresolved reference {ref!r} (missing {key!r})")
            else:
                return ""
        return cur

    return resolve


def render(v):
    if isinstance(v, bool):
        return "True" if v else "False"
    if v is None:
        return ""
    if isinstance(v, (list, dict)):
        return json.dumps(v)
    return str(v)


# --------------------------------------------------------------------------- expansion
class Compiler:
    def __init__(self, roots):
        self.roots = roots  # alias -> repo root path ("self" is the repo of the root pipeline)
        self.warnings = []
        self.templates_used = []

    def repo_of(self, path):
        for alias, root in self.roots.items():
            try:
                path.resolve().relative_to(root.resolve())
                return root
            except ValueError:
                continue
        raise CompileError(f"{path} is outside every known repository")

    def expand_str(self, s, scope):
        m = FULL_EXPR.match(s)
        if m:
            return evaluate(m.group(1), make_resolver(scope))
        return EXPR.sub(lambda mm: render(evaluate(mm.group(1), make_resolver(scope))), s)

    def expand(self, node, scope, file):
        if isinstance(node, str):
            return self.expand_str(node, scope)
        if isinstance(node, list):
            return self.expand_list(node, scope, file)
        if isinstance(node, dict):
            return self.expand_dict(node, scope, file)
        return node

    def expand_list(self, items, scope, file):
        out = []
        for item in items:
            if isinstance(item, dict) and len(item) == 1:
                key = next(iter(item))
                d = DIRECTIVE.match(key) if isinstance(key, str) else None
                if d:
                    kind, rest = d.groups()
                    body = item[key]
                    body = body if isinstance(body, list) else [body]
                    if kind == "if":
                        if evaluate(rest, make_resolver(scope)):
                            out.extend(self.expand_list(body, scope, file))
                    else:
                        var, _, src = rest.partition(" in ")
                        for value in evaluate(src.strip(), make_resolver(scope)):
                            inner = dict(scope)
                            inner[var.strip()] = value
                            out.extend(self.expand_list(body, inner, file))
                    continue
            if isinstance(item, dict) and "template" in item:
                out.extend(self.include(item, scope, file))
                continue
            out.append(self.expand(item, scope, file))
        return out

    def expand_dict(self, node, scope, file):
        out = {}
        for key, value in node.items():
            d = DIRECTIVE.match(key) if isinstance(key, str) else None
            if d:
                kind, rest = d.groups()
                if kind == "if":
                    if evaluate(rest, make_resolver(scope)):
                        out.update(self.expand_dict(value, scope, file))
                else:
                    var, _, src = rest.partition(" in ")
                    for v in evaluate(src.strip(), make_resolver(scope)):
                        inner = dict(scope)
                        inner[var.strip()] = v
                        out.update(self.expand_dict(value, inner, file))
                continue
            new_key = self.expand_str(key, scope) if isinstance(key, str) else key
            out[new_key] = self.expand(value, scope, file)
        return out

    def resolve_path(self, ref, file):
        path, alias = (ref.rsplit("@", 1) + [None])[:2] if "@" in ref else (ref, None)
        if alias:
            if alias not in self.roots:
                raise CompileError(f"repository alias {alias!r} is not declared (template {ref!r})")
            return self.roots[alias] / path.lstrip("/")
        if path.startswith("/"):
            return self.repo_of(file) / path.lstrip("/")
        return (file.parent / path).resolve()

    def include(self, item, scope, file):
        ref = self.expand_str(item["template"], scope)
        target = self.resolve_path(ref, file)
        if not target.exists():
            raise CompileError(f"template not found: {ref} -> {target}")
        self.templates_used.append(target)
        doc = yaml.safe_load(target.read_text())
        passed = self.expand(item.get("parameters", {}) or {}, scope, file)
        bound = bind_parameters(doc.get("parameters", []), passed, str(target.name))
        inner = {"parameters": bound}
        for section in ("stages", "jobs", "steps"):
            if section in doc:
                return self.expand_list(doc[section], inner, target)
        raise CompileError(f"{target} has no stages, jobs or steps section")


def bind_parameters(declared, passed, who):
    spec = {p["name"]: p for p in declared}
    unknown = set(passed) - set(spec)
    if unknown:
        raise CompileError(f"{who}: unexpected parameter(s) {sorted(unknown)}")
    bound = {}
    for name, p in spec.items():
        if name in passed:
            value = passed[name]
        elif "default" in p:
            value = p["default"]
        else:
            raise CompileError(f"{who}: required parameter {name!r} was not passed")
        kind = p.get("type", "string")
        if kind == "boolean" and not isinstance(value, bool):
            raise CompileError(f"{who}: {name} must be boolean, got {value!r}")
        if kind == "string" and not isinstance(value, str):
            raise CompileError(f"{who}: {name} must be string, got {value!r}")
        if kind == "object" and not isinstance(value, (list, dict)):
            raise CompileError(f"{who}: {name} must be an object, got {value!r}")
        if "values" in p and str(value).lower() not in [str(v).lower() for v in p["values"]]:
            raise CompileError(f"{who}: {name}={value!r} is not one of {p['values']}")
        bound[name] = value
    return bound


# --------------------------------------------------------------------------- graph checks
def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def job_name(job):
    return job.get("job") or job.get("deployment")


def check_graph(stages, label, errors):
    names = [s["stage"] for s in stages]
    if len(names) != len(set(names)):
        errors.append(f"[{label}] duplicate stage names {names}")
    for i, s in enumerate(stages):
        deps = as_list(s.get("dependsOn"))
        for d in deps:
            if d not in names:
                errors.append(f"[{label}] stage {s['stage']} depends on unknown stage {d}")
            elif names.index(d) >= i:
                errors.append(f"[{label}] stage {s['stage']} depends on {d}, which is listed later")
        cond = s.get("condition", "")
        for ref in set(re.findall(r"dependencies\.([A-Za-z0-9_]+)\.", cond)):
            if ref not in deps:
                errors.append(f"[{label}] condition of {s['stage']} reads {ref}, which is not in dependsOn")
        jobs = s.get("jobs", [])
        jnames = [job_name(j) for j in jobs]
        if len(jnames) != len(set(jnames)):
            errors.append(f"[{label}] duplicate job names in {s['stage']}: {jnames}")
        for j in jobs:
            for d in as_list(j.get("dependsOn")):
                if d not in jnames:
                    errors.append(f"[{label}] job {job_name(j)} depends on unknown job {d}")


def collect_requirements(stages):
    found = {"variable groups": set(), "environments": set(), "service connections": set(), "app services": set()}
    for s in stages:
        for j in s.get("jobs", []):
            for v in as_list(j.get("variables")):
                if isinstance(v, dict) and "group" in v:
                    found["variable groups"].add(v["group"])
            if j.get("environment"):
                found["environments"].add(j["environment"])
            steps = list(j.get("steps", []))
            deploy = j.get("strategy", {}).get("runOnce", {}).get("deploy", {}).get("steps", [])
            for st in steps + deploy:
                inputs = st.get("inputs", {}) if isinstance(st, dict) else {}
                for key in ("azureSubscription", "azureResourceManagerConnection"):
                    if inputs.get(key):
                        found["service connections"].add(inputs[key])
                if inputs.get("appName"):
                    found["app services"].add(inputs["appName"])
    return found


# --------------------------------------------------------------------------- run-time simulation
def simulate(stages, branch, reason, failed=()):
    status = {}
    for s in stages:
        deps = as_list(s.get("dependsOn"))
        if s["stage"] in failed:
            status[s["stage"]] = "Failed"
            continue
        scope = {
            "variables": {"Build.SourceBranch": branch, "Build.Reason": reason},
            "dependencies": {d: {"result": status.get(d, "")} for d in deps},
        }
        if "condition" in s:
            run = bool(evaluate(re.sub(r"\s+", " ", s["condition"]), make_resolver(scope, strict=False)))
        else:
            run = all(status.get(d) in ("Succeeded", "SucceededWithIssues") for d in deps)
        status[s["stage"]] = "Succeeded" if run else "Skipped"
    return status


# --------------------------------------------------------------------------- main
# Concrete values that must never appear inside a template. `goat-${{ ... }}` is allowed on purpose:
# the naming convention is derived from the environment parameter, which is what keeps it stated once.
FORBIDDEN_IN_TEMPLATE = [
    (r"goat-(?:dev|sit|uat|preprod|prod)\b", "a concrete environment name"),
    (r"\b(?:rg|kv|sql|cosmos|stack|plan)-goat", "a concrete resource name"),
    (r"\b(?:app|func)-goat", "a concrete resource name"),
    (r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "a GUID"),
    (r"southeastasia", "a region"),
    (r"kaidevops|azurewebsites\.net", "a host name"),
]


def strip_comments(text):
    return "\n".join(l for l in text.splitlines() if not l.lstrip().startswith("#"))


def steps_of(job):
    return list(job.get("steps") or []) + list(
        job.get("strategy", {}).get("runOnce", {}).get("deploy", {}).get("steps", []) or []
    )


def tasks_of(job, prefix):
    return [s for s in steps_of(job) if str(s.get("task", "")).startswith(prefix)]


def main(app_root, infra_root):
    errors, notes = [], []
    app_root, infra_root = Path(app_root), Path(infra_root)

    app_pipeline = app_root / "azure-pipelines-app.yml"
    infra_pipeline = infra_root / "deploy/pipeline/azure-pipelines-infra.yml"
    app_templates = sorted((app_root / "deploy/templates").glob("*.yml"))
    infra_templates = sorted((infra_root / "deploy/templates").glob("*.yml"))
    all_files = [app_pipeline, infra_pipeline] + app_templates + infra_templates

    # 1. the files exist and parse
    for f in all_files:
        if not f.exists():
            errors.append(f"missing file: {f}")
            continue
        try:
            yaml.safe_load(f.read_text())
        except yaml.YAMLError as exc:
            errors.append(f"YAML syntax error in {f.name}: {exc}")
    if errors:
        return errors, notes
    notes.append(f"1. {len(all_files)} pipeline files parse: 2 root pipelines, {len(app_templates) + len(infra_templates)} templates")

    # 2. templates hold no concrete values and no defaults, and every parameter is used
    for f in app_templates + infra_templates:
        text = f.read_text()
        code = strip_comments(text)
        for pattern, what in FORBIDDEN_IN_TEMPLATE:
            m = re.search(pattern, code)
            if m:
                errors.append(f"{f.name}: {what} in a template: {m.group(0)!r}")
        doc = yaml.safe_load(text)
        declared = {p["name"] for p in doc.get("parameters", [])}
        used = set(re.findall(r"parameters\.([A-Za-z_][A-Za-z0-9_]*)", text))
        for name in sorted(used - declared):
            errors.append(f"{f.name}: parameter {name!r} is used but not declared")
        for name in sorted(declared - used):
            errors.append(f"{f.name}: parameter {name!r} is declared but never used")
        defaults = [p["name"] for p in doc.get("parameters", []) if "default" in p]
        if defaults:
            errors.append(f"{f.name}: parameters with defaults {defaults} (templates must have none)")
    notes.append("2. templates: no concrete names, regions, GUIDs or hosts; every parameter declared, used, and without a default")

    # 3. every environment the pipelines accept has a Bicep parameter file, and every parameter file
    #    is addressed by some environment. A missing file would only fail at run time otherwise.
    infra_jobs_doc = yaml.safe_load((infra_root / "deploy/templates/deploy-infra-jobs.yml").read_text())
    allowed_envs = [p for p in infra_jobs_doc["parameters"] if p["name"] == "environment"][0].get("values", [])
    if sorted(allowed_envs) != sorted(ENVS):
        errors.append(f"deploy-infra-jobs.yml: environment allow-list is {allowed_envs}, expected {ENVS}")
    param_dir = infra_root / "deploy/variables"
    have = {f.stem for f in param_dir.glob("*.bicepparam")}
    for env in allowed_envs:
        if env not in have:
            errors.append(f"environment {env!r} is allowed but {param_dir.name}/{env}.bicepparam does not exist")
    for stem in sorted(have - set(allowed_envs)):
        errors.append(f"{stem}.bicepparam exists but {stem!r} is not an allowed environment (dead file)")
    notes.append(f"3. every allowed environment has a .bicepparam ({', '.join(sorted(have))}) and none is orphaned")

    # 3b. azureLocation is the only value the pipelines still restate from a .bicepparam, because
    #     Azure DevOps cannot read one at compile time. Everything else now comes from the variable
    #     group at run time, so there is nothing else left to drift.
    def param_defaults(doc):
        return {p["name"]: p.get("default") for p in doc.get("parameters", [])}

    app_defaults = param_defaults(yaml.safe_load(app_pipeline.read_text()))
    infra_defaults = param_defaults(yaml.safe_load(infra_pipeline.read_text()))
    for env in sorted(have):
        text = (param_dir / f"{env}.bicepparam").read_text()
        assigned = dict(re.findall(r"^param\s+(\w+)\s*=\s*'([^']*)'", text, re.M))
        for where, defaults in (("app", app_defaults), ("infra", infra_defaults)):
            if defaults.get("azureLocation") != assigned.get("location"):
                errors.append(f"{where} pipeline azureLocation={defaults.get('azureLocation')!r} but "
                              f"{env}.bicepparam assigns location={assigned.get('location')!r}")
    notes.append("3b. both pipelines agree with every .bicepparam on the location")

    # 4. compile the application pipeline for every combination of inputs
    root = yaml.safe_load(app_pipeline.read_text())
    results = {}
    for infra_on in (False, True):
        for hotfix_env in HOTFIX_ENVS:
            try:
                bound = bind_parameters(root["parameters"],
                                        {"deployInfraAll": infra_on, "hotfixEnvironment": hotfix_env},
                                        "azure-pipelines-app.yml")
                comp = Compiler({"self": app_root, "InfraRepo": infra_root})
                stages = comp.expand_list(root["stages"], {"parameters": bound}, app_pipeline)
            except CompileError as exc:
                errors.append(f"[infra={infra_on}, hotfix={hotfix_env}] compile error: {exc}")
                continue
            check_graph(stages, f"deployInfraAll={infra_on}, hotfix={hotfix_env}", errors)
            results[(infra_on, hotfix_env)] = stages
    if not results:
        return errors, notes
    notes.append(f"4. application pipeline compiles {len(results)} times (infrastructure off/on x {len(HOTFIX_ENVS)} hotfix targets); stage and job graph consistent")

    # 5. job shape. Both modes produce the same two jobs; in slot mode the swap is a step of the
    #    deployment job, placed after the slot check, so a bad build never reaches production.
    def slot_copy():
        tmp = Path(tempfile.mkdtemp())
        dst = tmp / "app"
        (dst / "deploy/templates").mkdir(parents=True)
        shutil.copy2(app_pipeline, dst / app_pipeline.name)
        for t in app_templates:
            shutil.copy2(t, dst / "deploy/templates" / t.name)
        f = dst / app_pipeline.name
        text = f.read_text()
        # Flip only the prod row of the environments list to slot mode.
        head, sep, tail = text.partition("      - environment: prod\n")
        if not sep:
            raise CompileError("cannot find the prod row in the environments list")
        tail = tail.replace("deploymentSlot: none", "deploymentSlot: staging", 1)
        f.write_text(head + sep + tail)
        return dst

    slot_root = slot_copy()
    for infra_on in (False, True):
        infra_jobs = [f"InfraDeploy_dev_Checks", "InfraDeploy_dev"] if infra_on else []
        dev = next(s for s in results[(infra_on, "dev")] if s["stage"] == "Deploy_dev")
        got = [job_name(j) for j in dev["jobs"]]
        want = infra_jobs + ["DeployApp_dev", "SmokeTest_dev"]
        if got != want:
            errors.append(f"dev jobs (infra={infra_on}): {got}, expected {want}")
        jobs = {job_name(j): j for j in dev["jobs"]}
        if as_list(jobs["DeployApp_dev"].get("dependsOn")) != (["InfraDeploy_dev"] if infra_on else []):
            errors.append(f"DeployApp_dev dependsOn {jobs['DeployApp_dev'].get('dependsOn')}")
        if as_list(jobs["SmokeTest_dev"].get("dependsOn")) != ["DeployApp_dev"]:
            errors.append("SmokeTest_dev must depend on DeployApp_dev")
        if infra_on and as_list(jobs["InfraDeploy_dev"].get("dependsOn")) != ["InfraDeploy_dev_Checks"]:
            errors.append("InfraDeploy_dev must depend on InfraDeploy_dev_Checks")

        # direct mode must not name a slot anywhere
        direct = jobs["DeployApp_dev"]
        for s in tasks_of(direct, "AzureAppServiceSettings"):
            if "slotName" in s.get("inputs", {}):
                errors.append("direct mode writes settings to a slot")
        if tasks_of(direct, "AzureAppServiceManage"):
            errors.append("direct mode contains a slot swap")

        # slot mode
        sdoc = yaml.safe_load((slot_root / app_pipeline.name).read_text())
        bound = bind_parameters(sdoc["parameters"], {"deployInfraAll": infra_on, "hotfixEnvironment": "dev"}, "slot copy")
        comp = Compiler({"self": slot_root, "InfraRepo": infra_root})
        sstages = comp.expand_list(sdoc["stages"], {"parameters": bound}, slot_root / app_pipeline.name)
        check_graph(sstages, f"slot copy infra={infra_on}", errors)
        prod = next(s for s in sstages if s["stage"] == "Deploy_prod")
        pj = {job_name(j): j for j in prod["jobs"]}
        want = (["InfraDeploy_prod_Checks", "InfraDeploy_prod"] if infra_on else []) + ["DeployApp_prod", "SmokeTest_prod"]
        if [job_name(j) for j in prod["jobs"]] != want:
            errors.append(f"prod jobs in slot mode (infra={infra_on}): {[job_name(j) for j in prod['jobs']]}, expected {want}")
        if "SwapSlot_prod" in pj or "DeploySlot_prod" in pj:
            errors.append("slot mode must not add a separate slot or swap job")
        sjob = pj["DeployApp_prod"]
        order = [s.get("task") or "script" for s in steps_of(sjob)]
        swap = next((s for s in tasks_of(sjob, "AzureAppServiceManage")), None)
        if swap is None:
            errors.append("slot mode has no swap task")
        else:
            i = order.index(swap["task"])
            before = [st for st in steps_of(sjob)[:i] if "script" in st]
            checks = [st for st in before if "SMOKE_URL" in (st.get("env") or {})]
            if not checks:
                errors.append("the slot check must run before the swap, so a failing build never reaches production")
            elif checks[-1]["env"]["SMOKE_URL"] != "$(slotSmokeTestUrl)":
                errors.append(f"the check before the swap reads {checks[-1]['env']['SMOKE_URL']!r}, "
                              f"not the slot address variable $(slotSmokeTestUrl)")
            si = swap["inputs"]
            if si.get("SourceSlot") != "staging" or si.get("SwapWithProduction") is not True or si.get("Action") != "Swap Slots":
                errors.append(f"swap task inputs wrong: {si}")
        web = next((s for s in tasks_of(sjob, "AzureWebApp")), None)
        if not web or web["inputs"].get("deployToSlotOrASE") is not True or web["inputs"].get("slotName") != "staging":
            errors.append("slot mode must deploy the API to the slot")
        settings = [s for s in tasks_of(sjob, "AzureAppServiceSettings") if "slotName" in s.get("inputs", {})]
        if not settings:
            errors.append("slot mode must write the API settings to the slot, not to production")
        for env in ("dev", "sit", "uat", "preprod"):
            names = [job_name(j) for j in next(s for s in sstages if s["stage"] == f"Deploy_{env}")["jobs"]]
            if f"DeployApp_{env}" not in names:
                errors.append(f"{env} must stay in direct mode in the slot copy: {names}")
    notes.append("5. job shape: two jobs in both modes; in slot mode the swap is a step placed after the slot check, and no second Environment is needed")

    # 5b. secrets reach Key Vault through an explicit, off-by-default toggle in the application
    #     pipeline, never through Bicep and never on every run.
    stage_text = (app_root / "deploy/templates/deploy-env-stage.yml").read_text()
    root_params = {p["name"]: p for p in root["parameters"]}
    if "createKeyVaultSecrets" not in root_params:
        errors.append("the application pipeline has no createKeyVaultSecrets toggle")
    elif root_params["createKeyVaultSecrets"].get("default") is not False:
        errors.append("createKeyVaultSecrets must default to false, so a routine run does not rewrite secrets")
    if "if eq(parameters.createKeyVaultSecrets, true)" not in stage_text:
        errors.append("the secret step is not guarded by the createKeyVaultSecrets toggle")
    if "keyvault secret set" not in stage_text:
        errors.append("no step writes a secret to Key Vault")
    for infra_on in (False, True):
        for st in results[(infra_on, "dev")]:
            for j in st.get("jobs", []):
                for t in steps_of(j):
                    if "keyvault secret set" in str(t.get("inputs", {}).get("inlineScript", "")):
                        errors.append("the secret step runs with the toggle off")
    notes.append("5b. the Key Vault secret step exists, is off by default, and is absent when the toggle is off")

    # 6. no rollback anywhere, and the smoke test is a plain reporting job
    dev = next(s for s in results[(False, "dev")] if s["stage"] == "Deploy_dev")
    if re.search(r"rollback", json.dumps(results[(False, "dev")]), re.I):
        errors.append("there must be no rollback job or step")
    smoke = next(j for j in dev["jobs"] if job_name(j) == "SmokeTest_dev")
    script = next(s for s in smoke["steps"] if "script" in s)
    if script.get("env", {}).get("SMOKE_URL") != "$(smokeTestUrl)":
        errors.append(f"the smoke test reads {script.get('env', {}).get('SMOKE_URL')!r}, "
                      f"not the live address variable $(smokeTestUrl)")
    if script["env"].get("SMOKE_INSECURE") != "--insecure":
        errors.append("the smoke test should accept the self-signed certificate while the mock certificate is in use")
    notes.append("6. smoke test is a plain reporting job over https; no rollback anywhere")

    # 7. every Environment name is fixed at compile time. Azure DevOps creates an Environment it does
    #    not recognise, and a new one carries no approval, so a name resolved at run time could
    #    deliver without a gate.
    for key, stages in results.items():
        for s in stages:
            for j in s.get("jobs", []):
                env = j.get("environment")
                if env is None:
                    continue
                if "$(" in str(env) or "${{" in str(env):
                    errors.append(f"job {job_name(j)}: environment {env!r} is not resolved at compile time")
                if env != f"goat-{s['stage'].split('_')[1]}":
                    errors.append(f"job {job_name(j)}: environment {env!r} does not match its stage {s['stage']}")
    notes.append("7. every deployment job binds a compile-time Environment name that matches its stage")

    # 7b. a job that reads a pipeline variable must load a variable group, or the reference resolves
    #     to literal text at run time and the task is handed a meaningless value.
    BUILT_IN = ("Build.", "Pipeline.", "Agent.", "System.", "Environment.", "Release.")
    for stages in results.values():
        for st in stages:
            for j in st.get("jobs", []):
                refs = set()
                for ref in re.findall(r"\$\(([A-Za-z_][A-Za-z0-9_.]*)\)", json.dumps(j)):
                    if not ref.startswith(BUILT_IN):
                        refs.add(ref)
                if not refs:
                    continue
                groups = [v["group"] for v in as_list(j.get("variables"))
                          if isinstance(v, dict) and "group" in v]
                if not groups:
                    errors.append(f"job {job_name(j)} reads {sorted(refs)} but loads no variable group")
    notes.append("7b. every job that reads a pipeline variable loads a variable group")

    # 8. which stages run for each branch
    base = results[(False, "dev")]
    scenarios = [
        ("develop", "refs/heads/develop", "IndividualCI", {"Deploy_dev"}),
        ("release/1.0.0", "refs/heads/release/1.0.0", "IndividualCI", {"Deploy_sit", "Deploy_uat"}),
        ("main", "refs/heads/main", "IndividualCI", {"Deploy_preprod", "Deploy_prod"}),
        ("feature/x", "refs/heads/feature/x", "IndividualCI", set()),
        ("pull request into develop", "refs/pull/7/merge", "PullRequest", set()),
        ("PR build reporting branch main", "refs/heads/main", "PullRequest", set()),
        ("manual run on main", "refs/heads/main", "Manual", {"Deploy_preprod", "Deploy_prod"}),
    ] + [(f"hotfix, environment={e}", "refs/heads/hotfix/mock-test", "IndividualCI", {f"Deploy_{e}"}) for e in HOTFIX_ENVS]
    table = []
    for name, branch, reason, want in scenarios:
        stages = results[(False, name.split("=")[1])] if name.startswith("hotfix") else base
        status = simulate(stages, branch, reason)
        ran = {s for s, r in status.items() if r == "Succeeded" and s.startswith("Deploy_")}
        table.append((name, ", ".join(sorted(ran, key=lambda x: ENVS.index(x.split("_")[1]))) or "none"))
        if ran != want:
            errors.append(f"branch scenario {name!r}: ran {sorted(ran)}, expected {sorted(want)}")
    if simulate(base, "refs/heads/main", "IndividualCI", failed={"Deploy_preprod"})["Deploy_prod"] != "Skipped":
        errors.append("PROD must be skipped when PRE-PROD fails")
    if any(r == "Succeeded" for s, r in simulate(base, "refs/heads/develop", "IndividualCI", failed={"Build"}).items() if s.startswith("Deploy_")):
        errors.append("no deployment may run when Build fails")
    notes.append("8. stages that run per branch (infrastructure off):")
    for name, ran in table:
        notes.append(f"      {name:<30} -> {ran}")
    notes.append("      PRE-PROD failing skips PROD; Build failing skips everything")

    # 9. a hotfix can never reach production
    try:
        bind_parameters(root["parameters"], {"hotfixEnvironment": "prod"}, "root")
        errors.append("the root pipeline accepts hotfixEnvironment=prod; a hotfix must never reach production")
    except CompileError:
        pass
    stage_doc = yaml.safe_load((app_root / "deploy/templates/deploy-env-stage.yml").read_text())
    if "prod" in [p for p in stage_doc["parameters"] if p["name"] == "hotfixEnvironment"][0].get("values", []):
        errors.append("the stage template allows hotfixEnvironment=prod")
    for infra_on in (False, True):
        for env in HOTFIX_ENVS:
            if simulate(results[(infra_on, env)], "refs/heads/hotfix/mock-test", "IndividualCI").get("Deploy_prod") == "Succeeded":
                errors.append(f"a hotfix with environment={env} deployed to production")
    notes.append("9. hotfixEnvironment rejects prod in the root pipeline and in the stage template; no hotfix run reaches PROD")

    # 10. build and test happen once, and the artifact contract matches on both sides
    build = next(s for s in base if s["stage"] == "Build")
    bjobs = {job_name(j): j for j in build["jobs"]}
    if sorted(bjobs) != ["BuildAndTest", "ContainerTests"]:
        errors.append(f"Build stage jobs are {sorted(bjobs)}, expected BuildAndTest and ContainerTests")
    for name, j in bjobs.items():
        for st in j.get("steps", []):
            if st.get("continueOnError"):
                errors.append(f"{name}: a step has continueOnError, so a failing test would not stop the pipeline")
        if any(isinstance(v, dict) and "group" in v for v in as_list(j.get("variables"))):
            groups = [v["group"] for v in as_list(j.get("variables")) if isinstance(v, dict) and "group" in v]
            errors.append(f"{name}: loads variable group(s) {groups} but nothing reads them")
    published = [st for st in bjobs["BuildAndTest"].get("steps", []) if "publish" in st]
    artifacts = {st.get("artifact") for st in published}
    downloaded = set()
    for s in base:
        for j in s.get("jobs", []):
            for st in steps_of(j):
                if st.get("download") == "current":
                    downloaded.add(st.get("artifact"))
    if artifacts != downloaded:
        errors.append(f"artifact mismatch: build publishes {artifacts}, deployment downloads {downloaded}")
    pkgs = {re.search(r"/([^/]+)/\*\.zip", s["inputs"]["package"]).group(1)
            for s in steps_of(next(j for j in next(x for x in base if x["stage"] == "Deploy_dev")["jobs"]
                                   if job_name(j) == "DeployApp_dev"))
            if s.get("inputs", {}).get("package")}
    out_dirs = {re.search(r"ArtifactStagingDirectory\)/(\w+)", st["inputs"]["arguments"]).group(1)
                for st in bjobs["BuildAndTest"]["steps"]
                if "ArtifactStagingDirectory" in str(st.get("inputs", {}).get("arguments", ""))}
    if pkgs != out_dirs:
        errors.append(f"package folders disagree: build writes {out_dirs}, deployment reads {pkgs}")
    notes.append(f"10. build runs once in two jobs; artifact {artifacts} and package folders {sorted(out_dirs)} agree on both sides")

    # 11. infrastructure pipeline on its own, for every environment, always as a deployment stack
    infra_doc = yaml.safe_load(infra_pipeline.read_text())
    for env in ENVS:
        try:
            bound = bind_parameters(infra_doc["parameters"],
                                    {"environments": [{"environment": env, "environmentName": f"goat-{env}"}]},
                                    "azure-pipelines-infra.yml")
            comp = Compiler({"self": infra_root})
            stages = comp.expand_list(infra_doc["stages"], {"parameters": bound}, infra_pipeline)
        except CompileError as exc:
            errors.append(f"infra pipeline, environment={env}: {exc}")
            continue
        check_graph(stages, f"infra {env}", errors)
        jobs = stages[0]["jobs"]
        if [job_name(j) for j in jobs] != [f"InfraDeploy_{env}_Checks", f"InfraDeploy_{env}"]:
            errors.append(f"infra pipeline jobs for {env}: {[job_name(j) for j in jobs]}")
        if "PullRequest" not in jobs[1].get("condition", ""):
            errors.append("the infrastructure deploy job does not exclude pull request builds")
        if jobs[0]["steps"][0].get("checkout") != "self":
            errors.append(f"the infrastructure pipeline must check out self, got {jobs[0]['steps'][0]}")
        bic = [t["inputs"] for j in jobs for t in tasks_of(j, "BicepDeploy")]
        if [b.get("operation") for b in bic] != ["validate", "whatIf", "create"]:
            errors.append(f"infra {env}: Bicep operations are {[b.get('operation') for b in bic]}, expected validate, whatIf, create")
        for b in bic:
            if b.get("type") != "deploymentStack":
                errors.append(f"infra {env}: a Bicep task is {b.get('type')!r}, not a deployment stack")
            if b.get("scope") != "subscription":
                errors.append(f"infra {env}: scope must be subscription")
            for need in ("name", "actionOnUnmanageResources", "actionOnUnmanageResourceGroups",
                         "denySettingsMode", "bypassStackOutOfSyncError", "bicepVersion",
                         "location", "subscriptionId", "templateFile", "parametersFile"):
                if need not in b or b[need] in ("", None):
                    errors.append(f"infra {env}: Bicep task {b.get('operation')} lacks {need}")
            if not re.fullmatch(r"\d+\.\d+\.\d+", str(b.get("bicepVersion", ""))):
                errors.append(f"infra {env}: bicepVersion must be an exact version, got {b.get('bicepVersion')!r}")
            if f"/{env}.bicepparam" not in str(b.get("parametersFile", "")):
                errors.append(f"infra {env}: parametersFile is {b.get('parametersFile')!r}, which is not this environment's file")
            if b.get("env", {}):
                pass
        if len({b.get("name") for b in bic}) != 1:
            errors.append(f"infra {env}: validate, preview and deploy must address the same stack")
        # No secret may travel through Bicep. The delivery pipeline writes secrets straight into Key
        # Vault, so a secret handed to a Bicep task would also end up in plain text in the compiled
        # parameters file.
        for j in jobs:
            for t in tasks_of(j, "BicepDeploy"):
                if t.get("env"):
                    errors.append(f"infra {env}: a Bicep task passes {sorted(t['env'])} through its environment; "
                                  f"secrets belong in Key Vault, written by the application pipeline")
    notes.append("11. infrastructure pipeline compiles alone for all 5 environments; every Bicep task is a deployment stack (validate, whatIf, create) with a pinned CLI, never on a pull request")

    # 12. infrastructure runs sequentially when several environments are asked for
    bound = bind_parameters(infra_doc["parameters"],
                            {"environments": [{"environment": e, "environmentName": f"goat-{e}"} for e in ENVS]},
                            "azure-pipelines-infra.yml")
    comp = Compiler({"self": infra_root})
    seq = comp.expand_list(infra_doc["stages"], {"parameters": bound}, infra_pipeline)
    if [s["stage"] for s in seq] != [f"Infra_{e}" for e in ENVS]:
        errors.append(f"infrastructure stages are {[s['stage'] for s in seq]}, expected one per environment in order")
    if any("dependsOn" in s for s in seq):
        errors.append("infrastructure stages declare dependsOn; they rely on the implicit order instead")
    notes.append("12. asking for all 5 environments produces 5 stages in order, relying on the implicit stage-to-stage dependency")

    # 13. what has to exist before the first run
    need = collect_requirements(results[(True, "dev")])
    notes.append("13. things the pipelines expect to exist (create before the first run):")
    for k, v in need.items():
        notes.append(f"      {k}: {', '.join(sorted(v)) or 'none'}")
    return errors, notes


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    errs, notes = main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
    print("\n".join(notes))
    print()
    if errs:
        print(f"FAILED: {len(errs)} problem(s)")
        for e in errs:
            print(" -", e)
        sys.exit(1)
    print("ALL CHECKS PASSED")
