"""
Selective .unitypackage extractor (GUIDs preserved).

usage:
  python extract_unitypackage.py <project_root> <package.unitypackage> <regex> [<regex> ...] [--dry-run] [--keep-code] [--no-deps]
  python extract_unitypackage.py <project_root> <package.unitypackage> <regex> [<regex> ...] --remove [--dry-run]

Extracts every asset whose pathname matches one of the regexes (case-insensitive, searched anywhere in the path),
plus - recursively - every asset in the SAME package that those assets reference by GUID:
  - text assets (.prefab .mat .asset .controller .anim .overrideController .mask) are scanned for `guid:` references
  - every .meta is scanned too (model importers reference external materials / avatars by GUID)
References to GUIDs outside the package (built-in resources, other packages) are ignored.

--dry-run lists what would be written, and first, when several assets match, each one with its own dependency count
and size, so variants with similar names can be compared before any of them is extracted.

By default scripts, assemblies, shaders and scenes are never extracted (.cs .asmdef .asmref .dll .shader .shadergraph
.shadersubgraph .hlsl .cginc .unity .lighting) - convert materials to your own shader instead. --keep-code disables that.
Files that already exist are skipped, so it is safe to re-run with more patterns. Folder .meta files from the package
are written for every folder created, so folder GUIDs match the package too.

--remove takes a trial extraction back out: the same patterns select the same assets and dependencies, and each one
that is on disk with the package's GUID is deleted with its .meta, unless a file outside that set still references
it (another extracted prefab sharing a material or atlas, a scene, a project setting): those are kept and listed with
one user. Folders left empty are removed too. Add --dry-run to list both without deleting. Afterwards refresh the
Editor (`unity command cliext.wait idle --refresh true`).

A .unitypackage is a gzipped tar: <guid>/pathname, <guid>/asset, <guid>/asset.meta (folders have no asset).
"""
import os
import re
import sys
import tarfile

args = [a for a in sys.argv[1:] if not a.startswith('--')]
flags = {a for a in sys.argv[1:] if a.startswith('--')}
KNOWN = {'--dry-run', '--keep-code', '--no-deps', '--remove'}
if len(args) < 3 or flags - KNOWN:
    if flags - KNOWN:
        print(f"unknown option(s): {' '.join(sorted(flags - KNOWN))}")
    print(__doc__)
    sys.exit(1)

root, pkg, patterns = args[0], args[1], [re.compile(p, re.I) for p in args[2:]]
dry_run = '--dry-run' in flags
remove = '--remove' in flags
follow_deps = '--no-deps' not in flags
SKIP = None if '--keep-code' in flags else re.compile(
    r'\.(cs|asmdef|asmref|dll|shader|shadergraph|shadersubgraph|hlsl|cginc|unity|lighting)$', re.I)
SCAN_EXT = ('.prefab', '.mat', '.asset', '.controller', '.anim', '.overridecontroller', '.mask')
GUID_RE = re.compile(rb'guid: ([0-9a-f]{32})')
META_GUID_RE = re.compile(rb'^guid: ([0-9a-f]{32})', re.M)


def skipped(path):
    return SKIP is not None and SKIP.search(path)


entries = {}  # guid -> {'pathname', 'asset', 'meta'}
with tarfile.open(pkg, 'r:gz') as tf:
    for m in tf:
        parts = m.name.lstrip('./').split('/')
        if len(parts) != 2 or not m.isfile():
            continue
        guid, kind = parts
        e = entries.setdefault(guid, {})
        if kind == 'pathname':
            e['pathname'] = tf.extractfile(m).read().decode('utf8').splitlines()[0].strip()
        elif kind == 'asset':
            e['asset'] = tf.extractfile(m).read()
        elif kind == 'asset.meta':
            e['meta'] = tf.extractfile(m).read()


def closure(start):
    """The start GUIDs plus every package asset they reference, recursively."""
    queue, wanted = list(start), set()
    while queue:
        g = queue.pop()
        if g in wanted:
            continue
        e = entries.get(g)
        if not e or 'asset' not in e or skipped(e.get('pathname', '')):
            continue
        wanted.add(g)
        if not follow_deps:
            continue
        blobs = []
        if e['pathname'].lower().endswith(SCAN_EXT):
            blobs.append(e['asset'])
        if 'meta' in e:
            blobs.append(e['meta'])
        for b in blobs:
            for ref in GUID_RE.findall(b):
                r = ref.decode()
                if r in entries and r not in wanted:
                    queue.append(r)
    return wanted


def mb(guids):
    return sum(len(entries[g]['asset']) for g in guids) / 1048576


matches = [g for g, e in entries.items()
           if 'asset' in e and not skipped(e.get('pathname', '')) and any(r.search(e.get('pathname', '')) for r in patterns)]
wanted = closure(matches)
print(f"{os.path.basename(pkg)}: {len(matches)} matched, {len(wanted)} with dependencies, {mb(wanted):.1f} MB")


# ------------------------------------------------------------------ --remove

# Binary files can't hold a `guid:` reference; everything else under Assets/ and ProjectSettings/ is scanned.
BINARY_EXT = ('.png', '.jpg', '.jpeg', '.tga', '.psd', '.tif', '.tiff', '.exr', '.hdr', '.bmp', '.gif', '.fbx', '.obj',
              '.blend', '.wav', '.ogg', '.mp3', '.aif', '.aiff', '.flac', '.mp4', '.mov', '.dll', '.so', '.a', '.ttf',
              '.otf', '.zip', '.unitypackage', '.bytes', '.bank')


def on_disk(g):
    """The asset's path when it is on disk with the package's GUID in its .meta, else None."""
    p = os.path.join(root, entries[g]['pathname'])
    try:
        with open(p + '.meta', 'rb') as f:
            m = META_GUID_RE.search(f.read())
    except OSError:
        return None
    return p if os.path.isfile(p) and m and m.group(1).decode() == g else None


def norm(p):
    return os.path.normcase(os.path.abspath(p))


if remove:
    present = {}
    for g in wanted:
        p = on_disk(g)
        if p:
            present[g] = p
    mine = {norm(p) for p in present.values()}
    mine |= {x + '.meta' for x in mine}
    users = {}  # GUID in the set -> one file outside the set that references it
    for top in ('Assets', 'ProjectSettings'):
        for d, _, files in os.walk(os.path.join(root, top)):
            for name in files:
                path = os.path.join(d, name)
                if norm(path) in mine or name.lower().endswith(BINARY_EXT):
                    continue
                try:
                    if os.path.getsize(path) > 64 * 1048576:
                        continue
                    with open(path, 'rb') as f:
                        data = f.read()
                except OSError:
                    continue
                refs = {r.decode() for r in GUID_RE.findall(data)}
                if name.endswith('.meta'):  # a .meta names its own GUID too
                    own = META_GUID_RE.search(data)
                    if own:
                        refs.discard(own.group(1).decode())
                for g in refs & present.keys():
                    users.setdefault(g, os.path.relpath(path, root).replace('\\', '/'))
    # Whatever a kept asset references stays too.
    todo = list(users)
    while todo:
        g = todo.pop()
        for r in closure([g]):
            if r in present and r not in users:
                users[r] = entries[g]['pathname']
                todo.append(r)
    gone = sorted((g for g in present if g not in users), key=lambda g: entries[g]['pathname'])
    print(f"  {'would remove' if dry_run else 'removing'} {len(gone)} ({mb(gone):.1f} MB); keeping {len(users)} still used; "
          f"{len(wanted) - len(present)} not on disk with the package's GUID")
    for g in gone:
        print(f"  - {entries[g]['pathname']}")
    for g in sorted(users, key=lambda g: entries[g]['pathname']):
        print(f"  = {entries[g]['pathname']}  (used by {users[g]})")
    if dry_run:
        sys.exit(0)
    dirs = set()
    for g in gone:
        p = present[g]
        for f in (p, p + '.meta'):
            if os.path.exists(f):
                os.remove(f)
        dirs.add(os.path.dirname(p))
    assets_dir = norm(os.path.join(root, 'Assets'))
    removed_dirs = 0
    for d in sorted(dirs, key=len, reverse=True):
        while norm(d) != assets_dir and os.path.isdir(d) and not os.listdir(d):
            os.rmdir(d)
            if os.path.exists(d + '.meta'):
                os.remove(d + '.meta')
            removed_dirs += 1
            d = os.path.dirname(d)
    print(f"  removed {len(gone)} assets and {removed_dirs} empty folders; refresh the Editor next")
    sys.exit(0)


# ------------------------------------------------------------------ extract

if dry_run:
    if len(matches) > 1:
        print("  matches, each with its own dependencies:")
        for g in sorted(matches, key=lambda g: entries[g]['pathname']):
            own = closure([g])
            print(f"    {entries[g]['pathname']}: {len(own)} assets, {mb(own):.1f} MB")
    for g in sorted(wanted, key=lambda g: entries[g]['pathname']):
        print(f"  {entries[g]['pathname']}")
    sys.exit(0)

written = existed = 0
for g in wanted:
    e = entries[g]
    dst = os.path.join(root, e['pathname'])
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if os.path.exists(dst):
        existed += 1
        continue
    with open(dst, 'wb') as f:
        f.write(e['asset'])
    if 'meta' in e:
        with open(dst + '.meta', 'wb') as f:
            f.write(e['meta'])
    written += 1

# Folder metas for created directories (stable folder GUIDs, no Unity warnings).
for g, e in entries.items():
    if 'asset' not in e and 'meta' in e and 'pathname' in e:
        d = os.path.join(root, e['pathname'])
        if os.path.isdir(d) and not os.path.exists(d + '.meta'):
            with open(d + '.meta', 'wb') as f:
                f.write(e['meta'])

print(f"  {written} written, {existed} already existed")
