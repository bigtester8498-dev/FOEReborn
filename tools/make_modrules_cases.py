#!/usr/bin/env python3
"""Writes the inputs of launcher/FOEReLauncher.Tests/cases/modrules-cases.json (stdlib only).

  python tools/make_modrules_cases.py [--out FILE]

Each case is a small game folder (files under Content/ and Mods/; a JSON object is written as a manifest, a
string as the raw file text), the "mods" section of the game's settings (or null: everything enabled), whether the
defaultEnabled mods are applied first (the game does it at every normal start), and the ids whose enable/disable
cascades are asked for. The expected answers ("expect") are filled by the launcher test
ModRulesCasesTests.FillExpected (FOERE_FILL_CASES=1) and checked against the game by its EditMode test
L1bModRulesCases; where the two disagree the game is right and the launcher is fixed.
"""

import argparse
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, "..", "launcher", "FOEReLauncher.Tests", "cases", "modrules-cases.json")

CLASSIC = {"id": "classic", "name": "Classic", "version": "1.0.2", "requires": [], "loadAfter": [], "assemblies": [],
           "generated": {"by": "tools/import_classic.py"}}
REBORN = {"id": "reborn", "name": "Reborn texts", "version": "0.1.0", "requires": [], "loadAfter": ["classic"], "assemblies": []}


def m(id, version="1.0.0", **kw):
    d = {"id": id, "name": "Mod " + id, "version": version}
    for k, v in kw.items():
        d[k] = v
    return d


def req(id, version=None):
    return {"id": id, "version": version} if version is not None else {"id": id}


def game(*mods, extra=None):
    files = {"Content/classic/package.json": CLASSIC, "Content/reborn/package.json": REBORN}
    for mod in mods:
        files["Mods/%s/package.json" % mod["id"]] = mod
    files.update(extra or {})
    return files


def sel(enabled=(), disabled=(), order=None):
    return {"enabled": list(enabled), "disabled": list(disabled), "order": list(order if order is not None else enabled)}


CASES = []


def case(name, files, settings, defaults=True, to_enable=(), to_disable=(), note=""):
    CASES.append({"name": name, "note": note, "files": files, "settings": settings, "defaults": defaults,
                  "toEnable": list(to_enable), "toDisable": list(to_disable)})


def build():
    a, b, c = m("a"), m("b"), m("c")
    case("content_only", game(), sel(), note="no mods: classic and reborn load, reborn after classic")
    case("mod_off_by_default", game(a), sel(), note="a new mod starts disabled")
    case("mod_enabled", game(a), sel(["a"]))
    case("no_selection_enables_all", game(a, b), None, note="tools and tests: settings null = every package on")
    case("default_enabled_no_choice", game(m("d", defaultEnabled=True)), sel(), note="defaultEnabled turns on without a choice")
    case("default_enabled_player_off", game(m("d", defaultEnabled=True)), sel(disabled=["d"]),
         note="a player who switched it off keeps it off")
    case("default_enabled_player_on", game(m("d", defaultEnabled=True), a), sel(["a", "d"], order=["d", "a"]))
    case("default_enabled_not_applied", game(m("d", defaultEnabled=True)), sel(), defaults=False,
         note="scripted runs (-autotest) do not apply defaults")
    case("default_enabled_requires_disabled", game(m("d", defaultEnabled=True, requires=[req("a")]), a), sel(disabled=["a"]),
         to_enable=["d"])
    case("requires_classic_ok", game(m("a", requires=[req("classic", ">=1.0.2")])), sel(["a"]))
    case("requires_classic_too_new", game(m("a", requires=[req("classic", ">=2.0")])), sel(["a"]))
    case("missing_dependency", game(m("a", requires=[req("zzz", "^1.0")])), sel(["a"]))
    case("missing_dependency_no_range", game(m("a", requires=[req("zzz")])), sel(["a"]))
    case("dependency_disabled", game(m("a", requires=[req("b")]), b), sel(["a"]), to_enable=["a"])
    case("dependency_broken", game(m("a", requires=[req("b")]), m("b", version="one")), sel(["a", "b"]),
         note="the reason of a quotes the reason of b")
    chain = [m("a"), m("b", requires=[req("a", ">=1.0")]), m("c", requires=[req("b", "^1")])]
    case("chain_of_three_on", game(*chain), sel(["c", "b", "a"]), to_enable=["c"], to_disable=["a"],
         note="requirements load first whatever the player's order")
    case("chain_of_three_middle_off", game(*chain), sel(["a", "c"], disabled=["b"]), to_enable=["c"], to_disable=["a", "b"])
    case("chain_of_three_bottom_wrong_version",
         game(m("a", version="0.9"), chain[1], chain[2]), sel(["a", "b", "c"]))
    case("chain_of_three_cascade_from_nothing", game(*chain), sel(), to_enable=["c", "b", "a"], to_disable=["a"])
    case("cycle_requires", game(m("a", requires=[req("b")]), m("b", requires=[req("a")])), sel(["a", "b"]))
    case("cycle_load_after", game(m("a", loadAfter=["b"]), m("b", loadAfter=["a"]), c), sel(["a", "b", "c"]),
         note="the cycle is skipped, c still loads")
    case("cycle_of_three_mixed",
         game(m("a", loadAfter=["c"]), m("b", requires=[req("a")]), m("c", loadBefore=["x"], loadAfter=["b"]), m("x")),
         sel(["a", "b", "c", "x"]))
    case("load_after", game(m("a", loadAfter=["b"]), b), sel(["a", "b"]))
    case("load_before", game(a, m("b", loadBefore=["a"])), sel(["a", "b"]))
    case("load_after_unknown_ignored", game(m("a", loadAfter=["nothere", "classic"])), sel(["a"]))
    case("player_order_tie_break", game(a, b, c), sel(["a", "b", "c"], order=["c", "a", "b"]))
    case("order_partial", game(a, b, c), sel(["a", "b", "c"], order=["c"]),
         note="mods missing from the order follow in discovery order")
    case("incompatible_later_loses", game(m("a", incompatible=[req("b")]), b), sel(["a", "b"]))
    case("incompatible_declarer_later_loses", game(m("a", incompatible=[req("b")]), b), sel(["a", "b"], order=["b", "a"]))
    case("incompatible_with_core", game(m("a", incompatible=[req("reborn")])), sel(["a"]), note="a core package always wins")
    case("incompatible_range_not_matching", game(m("a", incompatible=[req("b", "<1.0")]), b), sel(["a", "b"]))
    case("incompatible_with_disabled", game(m("a", incompatible=[req("b")]), b), sel(["a"]))
    case("incompatible_drop_leaves_user",
         game(m("a", incompatible=[req("b")]), b, m("c", requires=[req("b")])), sel(["a", "b", "c"]))
    case("group_folder",
         game(m("z"), extra={"Mods/examples/a/package.json": m("a"), "Mods/examples/b/package.json": m("b", requires=[req("a")])}),
         sel(["a", "b", "z"], order=[]), note="Mods/examples/* is one level of group folder; discovery is by folder name")
    case("group_two_levels_ignored", game(extra={"Mods/g1/g2/a/package.json": m("a")}), None)
    case("content_group_not_scanned", game(extra={"Content/extra/sub/package.json": m("sub")}), None)
    case("dot_folder_ignored", game(extra={"Mods/.hidden/package.json": m("hidden")}), None)
    case("duplicate_id", game(extra={"Mods/a1/package.json": m("a"), "Mods/a2/package.json": m("a", version="2.0")}),
         sel(["a"]), note="the second folder is skipped")
    case("broken_syntax", game(extra={"Mods/bad/package.json": "{ \"id\": \"bad\", "}), sel(["bad"]))
    case("broken_empty_file", game(extra={"Mods/bad/package.json": ""}), sel(["bad"]))
    case("broken_json_null", game(extra={"Mods/bad/package.json": "null"}), sel(["bad"]))
    case("broken_not_object", game(extra={"Mods/bad/package.json": "[1, 2]"}), sel(["bad"]))
    case("broken_wrong_type", game(extra={"Mods/bad/package.json": {"id": "bad", "version": "1.0", "requires": "classic"}}),
         sel(["bad"]))
    case("broken_no_id", game(extra={"Mods/noid/package.json": {"version": "1.0"}}), sel(["noid"]),
         note="the folder name becomes the id")
    case("broken_bad_id", game(extra={"Mods/badid/package.json": m("Bad Id")}), sel(["Bad Id"]))
    case("broken_no_version", game(extra={"Mods/a/package.json": {"id": "a"}}), sel(["a"]))
    case("broken_bad_version", game(m("a", version="1.a")), sel(["a"]))
    case("broken_requires_no_id", game(m("a", requires=[{"version": "1.0"}])), sel(["a"]))
    case("broken_requires_self", game(m("a", requires=[req("a")])), sel(["a"]))
    case("broken_requires_range", game(m("a", requires=[req("classic", ">>1")])), sel(["a"]))
    case("broken_incompatible_range", game(m("a", incompatible=[req("b", "1.0 ||")]), b), sel(["a", "b"]))
    case("broken_game_range", game(m("a", game="abc")), sel(["a"]))
    case("broken_two_errors", game(extra={"Mods/x/package.json": {"id": "X", "version": "v1"}}), sel(["X"]),
         note="the first error is the reason, every issue is listed")
    case("warning_game_range", game(m("a", game="<1.0")), sel(["a"]), note="a warning, the mod still loads")
    case("warning_unknown_field", game(m("a", tags=["x"], url="http://e", homepage="h", colour=1)), sel(["a"]))
    case("code_mod_ok", game(m("a", assemblies=["Code/A.dll"]), extra={"Mods/a/Code/A.dll": "MZ"}), sel(["a"]))
    case("code_mod_missing_dll", game(m("a", assemblies=["Code/A.dll"])), sel(["a"]))
    case("code_mod_dll_escape", game(m("a", assemblies=["../A.dll"])), sel(["a"]))
    case("numbers_for_text", game(extra={"Mods/n/package.json": {"id": "n", "version": 2, "name": 5}}), sel(["n"]))
    case("key_case", game(extra={"Mods/k/package.json": {"ID": "k", "Version": "1.0.0", "Requires": [{"Id": "classic"}]}}),
         sel(["k"]))
    case("cascade_enable_skips_core_and_missing",
         game(m("a", requires=[req("classic"), req("zzz"), req("b")]), b), sel(), to_enable=["a"])
    case("cascade_disable_ignores_off_users",
         game(a, m("b", requires=[req("a")]), m("c", requires=[req("a")])), sel(["a", "b"], disabled=["c"]),
         to_disable=["a"])
    case("cascade_diamond",
         game(a, m("b", requires=[req("a")]), m("c", requires=[req("a")]), m("d", requires=[req("b"), req("c")])),
         sel(["a", "b", "c", "d"]), to_enable=["d"], to_disable=["a", "b"])
    case("cascade_enable_cycle", game(m("a", requires=[req("b")]), m("b", requires=[req("a")])), sel(),
         to_enable=["a"], to_disable=["a"])


VERSIONS = [
    ("1.0.2", "", True), ("1.0.2", "*", True), ("1.0.2", "1.0.2", True), ("1.0.2", "1.0", False),
    ("1.0", "1.0.0", True), ("1.0.2", "=1.0.2", True), ("1.0.2", ">=1.0.2", True), ("1.0.1", ">=1.0.2", False),
    ("1.0.2", ">1.0", True), ("1.0", ">1.0", False), ("2.0", "<=2", True), ("2.0.1", "<=2", False),
    ("1.9.9", "<2", True), ("2", "<2", False), ("1.5.0", "^1.2", True), ("2.0.0", "^1.2", False),
    ("0.2.5", "^0.2.3", True), ("0.3.0", "^0.2.3", False), ("1.2.9", "~1.2", True), ("1.3.0", "~1.2", False),
    ("1.9", "~1", True), ("2.0", "~1", False), ("1.4.2", "1.x", True), ("2.0", "1.x", False),
    ("1.2.7", "1.2.*", True), ("1.3", "1.2.*", False), ("7.0", "x", True), ("1.5", ">=1.0 <2.0", True),
    ("2.5", ">=1.0 <2.0", False), ("1.5", ">= 1.0 < 2", True), ("3.1", "<1 || >=3", True), ("2.0", "<1 || >=3", False),
    ("1.0.0-beta", "1.0.0", True), ("1.0.0+build", ">=1.0.0", True), ("1.0.2", ">=1.0.2,<2", True),
]

RANGES = [
    ("", True), ("*", True), (">=1.0.2", True), ("^1", True), ("~1.2.3", True), ("1.x", True), ("1.2.*", True),
    (">= 1.0 <2", True), ("1 || 2", True), ("1.0.0-beta", True), (">>1", False), ("abc", False), ("1.0 ||", False),
    ("v1", False), (">=", False), ("1..2", False), ("=> 1", False),
]

IS_VERSION = [("1", True), ("1.0.2", True), ("10.20.30.40", True), ("1.0.0-rc.1", True), ("1.0+b", True), ("1.a", False),
              ("v1", False), ("", False), ("1.", False), (" 1.0 ", True)]

COMPARE = [("1.0", "1.0.0", 0), ("1.2", "1.10", -1), ("2", "1.9.9", 1), ("1.0-x", "1.0", 0), ("", "0", 0)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=DEFAULT_OUT)
    a = ap.parse_args()
    build()
    old = {}
    if os.path.exists(a.out):
        with open(a.out, encoding="utf-8") as f:
            for c in json.load(f).get("cases", []):
                if "expect" in c:
                    old[c["name"]] = c["expect"]
    for c in CASES:
        if c["name"] in old:
            c["expect"] = old[c["name"]]
    data = {
        "format": 1,
        "about": "Shared cases of the mod rules: run by the launcher (ModRulesCasesTests) and by the game "
                 "(EditMode L1bModRulesCases). Inputs from tools/make_modrules_cases.py; answers checked by both.",
        "gameVersion": "1.0.2",
        "cases": CASES,
        "versions": [{"version": v, "range": r, "satisfies": s} for v, r, s in VERSIONS],
        "ranges": [{"range": r, "valid": ok} for r, ok in RANGES],
        "isVersion": [{"version": v, "valid": ok} for v, ok in IS_VERSION],
        "compare": [{"a": x, "b": y, "result": r} for x, y, r in COMPARE],
    }
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
        f.write("\n")
    print("%d cases, %d version checks -> %s" % (len(CASES), len(VERSIONS) + len(RANGES) + len(IS_VERSION) + len(COMPARE), a.out))


if __name__ == "__main__":
    main()
