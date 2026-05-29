#!/usr/bin/env python3
"""Generate .so.meta files for libplayscope_crash and folder .meta files
for the ABI directories under Plugins/Android/libs/.

Called from build-native.yml after cmake produces the three .so files.
Stable GUIDs per ABI so Unity does not see the binaries as new assets
on every CI rebuild.
"""
import os

OUT = "Plugins/Android/libs"

ABI_META = [
    ("arm64-v8a",   "ARM64",  "1e2afb1a292a4fc689fb83e583166d65"),
    ("armeabi-v7a", "ARMv7",  "32d0b032ac374543ba6a71873491f14d"),
    ("x86_64",      "X86_64", "44ad282d668a437d9fcac7765524bbaa"),
]

FOLDER_META = [
    ("arm64-v8a",   "cd75a66bf8f64e86b8f685bb7e6476f0"),
    ("armeabi-v7a", "7b0a3a6693fb449da38205551119b2eb"),
    ("x86_64",      "a05afe15452545bb881fb2998cfb1d03"),
]

SO_META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: {cpu}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

FOLDER_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_lf(path: str, content: str) -> None:
    with open(path, "w", newline="\n") as f:
        f.write(content)
    print("wrote", path)


def main() -> None:
    for abi, cpu, guid in ABI_META:
        path = f"{OUT}/{abi}/libplayscope_crash.so.meta"
        write_lf(path, SO_META_TEMPLATE.format(guid=guid, cpu=cpu))

    for abi, guid in FOLDER_META:
        path = f"{OUT}/{abi}.meta"
        if os.path.exists(path):
            print("keep existing", path)
            continue
        write_lf(path, FOLDER_TEMPLATE.format(guid=guid))


if __name__ == "__main__":
    main()
