#!/usr/bin/env python3
"""Create missing Unity .meta files for the com.xrat.airy package.

Packages installed from a git URL are read-only, so Unity cannot generate .meta files for them and
ignores any asset without one. Run this after adding files to the package (Unity also writes them
when the package is embedded and the editor is open; either way, commit the .meta files).

GUIDs are derived from the package-relative path, so re-running is stable and never rewrites existing files.
"""
import hashlib
import pathlib
import sys

PACKAGE = pathlib.Path(__file__).resolve().parent.parent / "AiryUnity" / "Packages" / "com.xrat.airy"

TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
           "  executionOrder: 0\n  icon: {instanceID: 0}\n" + TAIL,
    ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n" + TAIL,
    ".shader": "ShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n" + TAIL,
    ".json": "PackageManifestImporter:\n  externalObjects: {}\n" + TAIL,
    ".md": "TextScriptImporter:\n  externalObjects: {}\n" + TAIL,
    ".csv": "TextScriptImporter:\n  externalObjects: {}\n" + TAIL,
}
FOLDER = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n" + TAIL


def hidden(path: pathlib.Path) -> bool:
    # Unity skips names starting with '.' and anything ending in '~' (e.g. Documentation~).
    rel = path.relative_to(PACKAGE)
    return any(part.startswith(".") or part.endswith("~") for part in rel.parts)


def main() -> int:
    created = 0
    for path in sorted(PACKAGE.rglob("*")):
        if path.suffix == ".meta" or hidden(path):
            continue
        meta = path.with_name(path.name + ".meta")
        if meta.exists():
            continue
        if path.is_dir():
            body = FOLDER
        else:
            body = IMPORTERS.get(path.suffix)
            if body is None:
                print(f"skipping {path.relative_to(PACKAGE)}: no importer template", file=sys.stderr)
                continue
        guid = hashlib.md5(("com.xrat.airy/" + path.relative_to(PACKAGE).as_posix()).encode()).hexdigest()
        meta.write_text(f"fileFormatVersion: 2\nguid: {guid}\n{body}", newline="\n")
        created += 1
        print(f"created {meta.relative_to(PACKAGE)}")
    print(f"{created} meta file(s) created")
    return 0


if __name__ == "__main__":
    sys.exit(main())
