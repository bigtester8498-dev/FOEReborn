#!/usr/bin/env python3
"""Turn a FOE Reborn build folder into the files the launcher downloads (stdlib only).

  python tools/publish.py pack --build DIR --out DIR --version TAG --channel test|prod
                               [--notes-ru FILE --notes-en FILE] [--base-url URL] [--prev DIR]
                               [--repo OWNER/NAME] [--launcher EXE]
                               [--catalogue-mods LIST]
  python tools/publish.py mods --mods DIR_OR_LIST --out DIR [--base-url URL] [--prev DIR] [--texts FILE]
                               [--repo OWNER/NAME]
  python tools/publish.py upload --out DIR [--repo OWNER/NAME] [--dry-run]
  python tools/publish.py version [--game-repo DIR]

`pack` writes into --out exactly what is uploaded:
  site/    GitHub Pages content: the download page, downloads/[test/]manifest.json, version.json,
           notes.json, and (with --launcher) FOEReLauncher.exe + launcher-version.json
  assets/  packs to upload as release assets: only the packs the previous manifest does not have
  publish-state.json  local bookkeeping for the next --prev run (pack content keys, file hash cache);
           never uploaded

With --base-url http://127.0.0.1:PORT/ the pack urls are PORT-local (BASE/assets/NAME), so a static
server started in --out serves a complete test site at BASE/site/. Without it, pack urls are release
asset urls https://github.com/REPO/releases/download/TAG/NAME.

`pack --catalogue-mods` (comma list of folder globs under the build; default
"Mods/examples/*,Mods/reforged_balance"; "" keeps every mod) leaves those mod folders out of the game packs:
they are published in the mod catalogue instead (`mods`), so the game ships with the reborn_* mods only.

`mods` writes the catalogue: one deterministic zip per mod folder (package.json at the zip root), named
<id>-<version>-<sha256 first 16>.zip, into assets/, and site/downloads/mods/catalog.json. --mods takes a mod
folder, a folder of mod folders (one level of group folders, as the game scans Mods/), or a comma list of these.
Russian name and description come from catalog.ru.json beside the mod's package.json ({"name": .., "description":
..}) or from --texts (a JSON file {"<id>": {"name": .., "description": ..}}); English from the manifest. With
--prev the catalogue keeps the entries of mods not given this time, and an unchanged archive keeps its old url.

`--version auto` (or the `version` command) gives v0.1.N, N = commit count of the game repository.
Pack grouping: RULES below.
"""

import argparse
import datetime
import fnmatch
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

DEFAULT_REPO = "bigtester8498-dev/FOEReborn"
DEFAULT_GAME_REPO = os.environ.get("FOERE_GAME_REPO", ".")
GAME_EXE = "FOEReborn.exe"
MAX_PACK = 300 * 1024 * 1024
FORMAT = 1
CATALOG_FORMAT = 1
DEFAULT_CATALOGUE_MODS = "Mods/examples/*,Mods/reforged_balance"
MOD_ID = re.compile(r"^[a-z0-9][a-z0-9_.\-]*$")
# Metadata beside a mod's package.json that is not part of the mod archive.
MOD_SKIP = {"catalog.ru.json"}

# Pack grouping, first match wins. A pack name is the template with the regex groups filled in.
# Measured on the 2026-11-12 build (54k files, 1.1 GB): the game code (FOE.*.dll, ~4 MB) and the
# small Unity data files that a rebuild rewrites are packs of their own, so a code-only update
# downloads a few MB; every content area is its own pack; the 49k player frames are one pack per armour.
RULES = [
    (r"^FOEReborn_Data/Managed/FOE\.[^/]+$", "code"),
    (r"^FOEReborn_Data/Managed/", "engine-managed"),
    (r"^FOEReborn_Data/", "data"),
    (r"^Content/classic/sprites/player/([^/]+)/", "sprites-player-{0}"),
    (r"^Content/classic/sprites/player/", "sprites-player"),
    (r"^Content/classic/sprites/([^/]+)/", "sprites-{0}"),
    (r"^Content/classic/(music|sounds|rooms|ui|textures)/", "{0}"),
    (r"^Content/classic/", "classic-data"),
    (r"^Content/([^/]+)/", "content-{0}"),
    (r"^Mods/([^/]+)/", "mod-{0}"),
    (r"", "engine"),
]

# Already compressed data gains nothing from deflate; storing it keeps packing and unpacking fast.
STORED_EXT = {".png", ".jpg", ".jpeg", ".mp3", ".ogg", ".zip", ".gz"}

ZIP_DATE = (1980, 1, 1, 0, 0, 0)


def die(msg):
    print("error: " + msg, file=sys.stderr)
    sys.exit(1)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def group_of(rel):
    for pattern, template in RULES:
        m = re.match(pattern, rel)
        if m:
            name = template.format(*[g.lower() for g in m.groups()])
            return re.sub(r"[^a-z0-9._-]+", "-", name.lower())
    raise AssertionError(rel)


def game_version(game_repo):
    out = subprocess.run(["git", "-C", game_repo, "rev-list", "--count", "HEAD"],
                         capture_output=True, text=True, check=True).stdout.strip()
    return "v0.1." + out


def excluded_dirs(build, patterns):
    """Folders (relative, forward slashes) matched by the --catalogue-mods globs."""
    res = []
    for pat in [p.strip().strip("/") for p in (patterns or "").split(",") if p.strip()]:
        cands = [""]
        for part in pat.split("/"):
            nxt = []
            for c in cands:
                base = os.path.join(build, c) if c else build
                if not os.path.isdir(base):
                    continue
                for name in sorted(os.listdir(base)):
                    if fnmatch.fnmatchcase(name, part) and os.path.isdir(os.path.join(base, name)):
                        nxt.append((c + "/" + name) if c else name)
            cands = nxt
        res.extend(cands)
    return sorted(set(res))


def scan(build, cache, skip=()):
    """Every file under build: path -> (size, sha256, mtime_ns). Hashes are reused from the cache
    of the previous run when size and mtime are unchanged. Files under the folders of skip are left out."""
    files = {}
    hashed = 0
    skip = [s.rstrip("/") + "/" for s in skip]
    for dirpath, dirnames, filenames in os.walk(build):
        dirnames.sort()
        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, build).replace("\\", "/")
            if any(rel.startswith(s) for s in skip):
                continue
            st = os.stat(full)
            c = cache.get(rel)
            if c and c["size"] == st.st_size and c["mtime"] == st.st_mtime_ns:
                sha = c["sha256"]
            else:
                sha = sha256_file(full)
                hashed += 1
            files[rel] = (st.st_size, sha, st.st_mtime_ns)
    return files, hashed


def content_key(entries):
    h = hashlib.sha256()
    for rel, size, sha in entries:
        h.update(("%s\0%d\0%s\n" % (rel, size, sha)).encode("utf-8"))
    return h.hexdigest()


def write_zip(build, entries, target):
    """Deterministic zip: sorted entries, fixed timestamps and attributes."""
    with zipfile.ZipFile(target, "w", allowZip64=True) as zf:
        for rel, size, _sha in entries:
            zi = zipfile.ZipInfo(rel, date_time=ZIP_DATE)
            zi.create_system = 0
            zi.external_attr = 0
            ext = os.path.splitext(rel)[1].lower()
            zi.compress_type = zipfile.ZIP_STORED if ext in STORED_EXT else zipfile.ZIP_DEFLATED
            zi.file_size = size
            with open(os.path.join(build, rel), "rb") as src, zf.open(zi, "w", force_zip64=size > 0x7FFFFFFF) as dst:
                shutil.copyfileobj(src, dst, 1 << 20)


def read_lines(path):
    if not path:
        return []
    with open(path, encoding="utf-8-sig") as f:
        return [ln.strip().lstrip("-*• ").strip() for ln in f if ln.strip()]


def load_json(path, default):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def dump_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def fmt_mb(n):
    return "%.1f MB" % (n / 1048576.0)


def cmd_pack(a):
    t0 = datetime.datetime.now()
    build = os.path.abspath(a.build)
    out = os.path.abspath(a.out)
    if not os.path.isfile(os.path.join(build, GAME_EXE)):
        die("%s has no %s" % (build, GAME_EXE))
    version = game_version(a.game_repo) if a.version == "auto" else a.version
    if not re.match(r"^v\d+\.\d+\.\d+$", version):
        die("version must look like v0.1.N: " + version)
    if os.path.exists(out) and os.listdir(out):
        die("--out must be a new or empty folder: " + out)
    base_url = a.base_url.rstrip("/") + "/" if a.base_url else None
    repo = a.repo or DEFAULT_REPO

    prev_state = load_json(os.path.join(a.prev, "publish-state.json"), None) if a.prev else None
    if a.prev and prev_state is None:
        die("--prev has no publish-state.json: " + a.prev)
    prev_packs = {p["contentKey"]: p for p in (prev_state or {}).get("packs", [])}
    cache = (prev_state or {}).get("hashCache", {})

    skip = excluded_dirs(build, a.catalogue_mods)
    if skip:
        print("left out for the mod catalogue: " + ", ".join(skip))
    files, hashed = scan(build, cache, skip)
    t_scan = datetime.datetime.now()

    groups = {}
    for rel in sorted(files):
        groups.setdefault(group_of(rel), []).append((rel, files[rel][0], files[rel][1]))

    os.makedirs(os.path.join(out, "assets"), exist_ok=True)
    packs = []
    file_pack = {}
    new_packs = 0
    for group in sorted(groups):
        entries = groups[group]
        raw = sum(e[1] for e in entries)
        key = content_key(entries)
        reused = prev_packs.get(key)
        if reused:
            pack = dict(reused)
            pack["new"] = False
        else:
            tmp = os.path.join(out, "assets", group + ".zip.tmp")
            write_zip(build, entries, tmp)
            sha = sha256_file(tmp)
            name = "%s-%s.zip" % (group, sha[:16])
            os.replace(tmp, os.path.join(out, "assets", name))
            size = os.path.getsize(os.path.join(out, "assets", name))
            if size > MAX_PACK:
                die("pack %s is %s, over the 300 MB limit: add a rule to RULES" % (name, fmt_mb(size)))
            url = (base_url + "assets/" + name) if base_url else \
                "https://github.com/%s/releases/download/%s/%s" % (repo, version, name)
            pack = {"name": name, "url": url, "size": size, "sha256": sha, "contentKey": key, "new": True}
            new_packs += 1
        pack["group"] = group
        pack["files"] = len(entries)
        pack["raw"] = raw
        packs.append(pack)
        for e in entries:
            file_pack[e[0]] = pack["name"]
    t_zip = datetime.datetime.now()

    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    manifest = {
        "format": FORMAT,
        "version": version,
        "builtAt": now,
        "game": GAME_EXE,
        "packs": [{"name": p["name"], "url": p["url"], "size": p["size"], "sha256": p["sha256"], "files": p["files"]}
                  for p in packs],
        "files": [{"path": rel, "size": files[rel][0], "sha256": files[rel][1], "pack": file_pack[rel]}
                  for rel in sorted(files)],
    }

    # Site: start from the previous site so the other channel and the launcher stay, then add this one.
    site = os.path.join(out, "site")
    if a.prev and os.path.isdir(os.path.join(a.prev, "site")):
        shutil.copytree(os.path.join(a.prev, "site"), site)
    os.makedirs(site, exist_ok=True)
    site_src = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "site")
    for fn in os.listdir(site_src):
        src = os.path.join(site_src, fn)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(site, fn))
    chan_dir = os.path.join(site, "downloads", "test" if a.channel == "test" else "")
    chan_dir = os.path.normpath(chan_dir)

    notes_prev = load_json(os.path.join(chan_dir, "notes.json"), {"entries": []}).get("entries", [])
    entry = {"version": version, "date": now, "ru": read_lines(a.notes_ru), "en": read_lines(a.notes_en)}
    notes = {"entries": [entry] + [e for e in notes_prev if e.get("version") != version]}

    dump_json(os.path.join(chan_dir, "manifest.json"), manifest)
    dump_json(os.path.join(chan_dir, "version.json"),
              {"version": version, "builtAt": now, "channel": a.channel,
               "size": sum(p["size"] for p in packs)})
    dump_json(os.path.join(chan_dir, "notes.json"), notes)

    if a.launcher:
        exe = os.path.abspath(a.launcher)
        vfile = os.path.join(out, "launcher-version.txt")
        subprocess.run([exe, "--write-version", vfile], check=True)
        with open(vfile, encoding="utf-8-sig") as f:
            lver = f.read().strip()
        os.remove(vfile)
        dl = os.path.join(site, "downloads")
        shutil.copy2(exe, os.path.join(dl, "FOEReLauncher.exe"))
        dump_json(os.path.join(dl, "launcher-version.json"),
                  {"version": lver, "builtAt": now, "file": "FOEReLauncher.exe",
                   "sha256": sha256_file(exe), "size": os.path.getsize(exe)})

    state = {
        "kind": "game", "catalogueMods": skip,
        "version": version, "channel": a.channel, "repo": repo, "baseUrl": base_url, "builtAt": now,
        "packs": [{k: p[k] for k in ("name", "url", "size", "sha256", "contentKey", "group", "files")} for p in packs],
        "newPacks": [p["name"] for p in packs if p["new"]],
        "hashCache": {rel: {"size": v[0], "sha256": v[1], "mtime": v[2]} for rel, v in files.items()},
    }
    dump_json(os.path.join(out, "publish-state.json"), state)

    t_end = datetime.datetime.now()
    lines = ["%-34s %10s %10s %7s  %s" % ("pack", "zip", "raw", "files", "status")]
    for p in sorted(packs, key=lambda p: -p["size"]):
        lines.append("%-34s %10s %10s %7d  %s" % (p["group"], fmt_mb(p["size"]), fmt_mb(p["raw"]), p["files"],
                                                  "new" if p["new"] else "reused"))
    total = sum(p["size"] for p in packs)
    new_total = sum(p["size"] for p in packs if p["new"])
    lines.append("total: %d packs, %d files, %s zipped (%s raw); new packs %d, %s to upload"
                 % (len(packs), len(files), fmt_mb(total), fmt_mb(sum(p["raw"] for p in packs)), new_packs,
                    fmt_mb(new_total)))
    lines.append("time: scan %.1f s (%d files hashed), packs %.1f s, total %.1f s"
                 % ((t_scan - t0).total_seconds(), hashed, (t_zip - t_scan).total_seconds(),
                    (t_end - t0).total_seconds()))
    report = "\n".join(lines)
    with open(os.path.join(out, "packs.txt"), "w", encoding="utf-8") as f:
        f.write(report + "\n")
    print(report)
    print("version %s, channel %s -> %s" % (version, a.channel, out))


def cmd_upload(a):
    """Creates the GitHub release with the new packs and the channel's JSON files, then asks the
    publish-pages workflow to rebuild the site."""
    out = os.path.abspath(a.out)
    state = load_json(os.path.join(out, "publish-state.json"), None)
    if state is None:
        die("no publish-state.json in " + out)
    if state.get("baseUrl"):
        die("this pack was made with --base-url (local test urls); pack again without it")
    repo = a.repo or state["repo"]
    if state.get("kind") == "mods":
        # Mod archives: one release per catalogue run; catalog.json goes to the site with the next Pages run.
        tag = state["tag"]
        assets = [os.path.join(out, "assets", n) for n in state["newArchives"]]
        assets.append(os.path.join(out, "site", "downloads", "mods", "catalog.json"))
        cmds = [["gh", "release", "create", tag, "--repo", repo, "--title", "FOE Reborn mods " + tag, "--latest=false",
                 "--notes", "Mod catalogue %s: %d new archives." % (tag, len(state["newArchives"]))] + assets,
                ["gh", "workflow", "run", "publish-pages.yml", "--repo", repo, "--ref", "main"]]
        for c in cmds:
            print(" ".join('"%s"' % x if " " in x else x for x in c))
            if not a.dry_run:
                subprocess.run(c, check=True)
        return
    tag = state["version"]
    chan_dir = os.path.join(out, "site", "downloads", "test" if state["channel"] == "test" else "")
    assets = [os.path.join(out, "assets", n) for n in state["newPacks"]]
    assets += [os.path.join(chan_dir, n) for n in ("manifest.json", "version.json", "notes.json")]
    cmds = [["gh", "release", "create", tag, "--repo", repo, "--title", "FOE Reborn " + tag,
             "--notes", "Game build %s. Packs: %d new." % (tag, len(state["newPacks"]))]
            + (["--prerelease"] if state["channel"] == "test" else []) + assets,
            ["gh", "workflow", "run", "publish-pages.yml", "--repo", repo, "--ref", "main"]]
    for c in cmds:
        print(" ".join('"%s"' % x if " " in x else x for x in c))
        if not a.dry_run:
            subprocess.run(c, check=True)


def mod_folders(spec):
    """Mod folders from a comma list of mod folders or folders of mod folders (one group level)."""
    res = []
    for item in [x.strip() for x in spec.split(",") if x.strip()]:
        item = os.path.abspath(item)
        if os.path.isfile(os.path.join(item, "package.json")):
            res.append(item)
            continue
        if not os.path.isdir(item):
            die("not a folder: " + item)
        for name in sorted(os.listdir(item)):
            sub = os.path.join(item, name)
            if name.startswith(".") or not os.path.isdir(sub):
                continue
            if os.path.isfile(os.path.join(sub, "package.json")):
                res.append(sub)
                continue
            for name2 in sorted(os.listdir(sub)):
                sub2 = os.path.join(sub, name2)
                if not name2.startswith(".") and os.path.isfile(os.path.join(sub2, "package.json")):
                    res.append(sub2)
    return res


def cmd_mods(a):
    out = os.path.abspath(a.out)
    if os.path.exists(out) and os.listdir(out):
        die("--out must be a new or empty folder: " + out)
    base_url = a.base_url.rstrip("/") + "/" if a.base_url else None
    repo = a.repo or DEFAULT_REPO
    texts = load_json(a.texts, {}) if a.texts else {}
    prev_state = load_json(os.path.join(a.prev, "publish-state.json"), None) if a.prev else None
    if a.prev and (prev_state is None or prev_state.get("kind") != "mods"):
        die("--prev is not a mods output: " + a.prev)
    prev_cat = load_json(os.path.join(a.prev, "site", "downloads", "mods", "catalog.json"), {"mods": []}) \
        if a.prev else {"mods": []}
    prev_by_sha = {e["sha256"]: e for e in prev_cat.get("mods", [])}
    tag = "mods-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S")
    os.makedirs(os.path.join(out, "assets"), exist_ok=True)
    entries = {}
    new_archives = []
    lines = []
    for folder in mod_folders(a.mods):
        man = load_json(os.path.join(folder, "package.json"), None)
        if not isinstance(man, dict) or not man.get("id") or not man.get("version"):
            die("broken package.json in " + folder)
        mid, ver = man["id"], str(man["version"])
        if not MOD_ID.match(mid):
            die("bad mod id: " + mid)
        if mid in entries:
            die("two folders with id " + mid)
        rel_files = []
        for dirpath, dirnames, filenames in os.walk(folder):
            dirnames[:] = sorted(d for d in dirnames if not d.startswith("."))
            for fn in sorted(filenames):
                rel = os.path.relpath(os.path.join(dirpath, fn), folder).replace("\\", "/")
                if rel not in MOD_SKIP:
                    rel_files.append((rel, os.path.getsize(os.path.join(dirpath, fn)), None))
        rel_files.sort()
        tmp = os.path.join(out, "assets", mid + ".zip.tmp")
        write_zip(folder, rel_files, tmp)
        sha = sha256_file(tmp)
        name = "%s-%s-%s.zip" % (mid, ver, sha[:16])
        size = os.path.getsize(tmp)
        old = prev_by_sha.get(sha)
        if old:
            os.remove(tmp)
            url = old["url"]
            status = "reused"
        else:
            os.replace(tmp, os.path.join(out, "assets", name))
            url = (base_url + "assets/" + name) if base_url else \
                "https://github.com/%s/releases/download/%s/%s" % (repo, tag, name)
            new_archives.append(name)
            status = "new"
        ru = load_json(os.path.join(folder, "catalog.ru.json"), {})
        ru.update(texts.get(mid, {}))
        entry = {
            "id": mid,
            "name": {"ru": ru.get("name") or man.get("name") or mid, "en": man.get("name") or mid},
            "description": {"ru": ru.get("description") or man.get("description") or "",
                            "en": man.get("description") or ""},
            "author": man.get("author") or "",
            "version": ver,
            "kind": "code" if man.get("assemblies") else "data",
            "game": man.get("game") or "",
            "requires": [{"id": r["id"], "version": r.get("version") or ""} for r in (man.get("requires") or [])
                         if isinstance(r, dict) and r.get("id")],
            "size": size,
            "sha256": sha,
            "url": url,
        }
        homepage = man.get("homepage") or man.get("url")
        if homepage:
            entry["homepage"] = homepage
        entries[mid] = entry
        lines.append("%-24s %-8s %-5s %9d  %s" % (mid, ver, entry["kind"], size, status))
    for e in prev_cat.get("mods", []):
        if e["id"] not in entries:
            entries[e["id"]] = e
            lines.append("%-24s %-8s %-5s %9d  kept from --prev" % (e["id"], e["version"], e["kind"], e["size"]))
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    catalog = {"format": CATALOG_FORMAT, "builtAt": now, "mods": [entries[k] for k in sorted(entries)]}
    dump_json(os.path.join(out, "site", "downloads", "mods", "catalog.json"), catalog)
    dump_json(os.path.join(out, "publish-state.json"),
              {"kind": "mods", "tag": tag, "repo": repo, "baseUrl": base_url, "newArchives": new_archives})
    report = "\n".join(lines + ["catalogue: %d mods, %d new archives -> %s" % (len(entries), len(new_archives), out)])
    with open(os.path.join(out, "mods.txt"), "w", encoding="utf-8") as f:
        f.write(report + "\n")
    print(report)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pack")
    p.add_argument("--build", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--version", required=True, help="v0.1.N or 'auto'")
    p.add_argument("--channel", required=True, choices=["test", "prod"])
    p.add_argument("--notes-ru")
    p.add_argument("--notes-en")
    p.add_argument("--base-url")
    p.add_argument("--prev")
    p.add_argument("--repo")
    p.add_argument("--launcher", help="launcher exe to put on the site")
    p.add_argument("--game-repo", default=DEFAULT_GAME_REPO)
    p.add_argument("--catalogue-mods", default=DEFAULT_CATALOGUE_MODS,
                   help="comma list of mod folder globs left out of the packs (default %(default)s; '' keeps all)")
    md = sub.add_parser("mods")
    md.add_argument("--mods", required=True)
    md.add_argument("--out", required=True)
    md.add_argument("--base-url")
    md.add_argument("--prev")
    md.add_argument("--texts")
    md.add_argument("--repo")
    u = sub.add_parser("upload")
    u.add_argument("--out", required=True)
    u.add_argument("--repo")
    u.add_argument("--dry-run", action="store_true")
    v = sub.add_parser("version")
    v.add_argument("--game-repo", default=DEFAULT_GAME_REPO)
    a = ap.parse_args()
    if a.cmd == "pack":
        cmd_pack(a)
    elif a.cmd == "mods":
        cmd_mods(a)
    elif a.cmd == "upload":
        cmd_upload(a)
    else:
        print(game_version(a.game_repo))


if __name__ == "__main__":
    main()
