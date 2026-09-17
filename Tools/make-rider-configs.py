#!/usr/bin/env python3
"""Generates Rider run configurations for every mod in the workspace.

    python Tools/make-rider-configs.py

Three per mod, matching the three things you ever do with one:

    install   dotnet build                  -> <persistent>/mods/<Mod>       game CLOSED
    stage     dotnet build -p:Dev=true      -> <persistent>/mods-dev/<Mod>   safe while running
    publish   dotnet build -t:SteamPublish  -> the workshop item in Steam/base.vdf

`publish` is only emitted for projects that actually declare the `SteamPublish` target, rather
than for all of them: a configuration that fails with "target does not exist" the first time
somebody tries it is worse than no configuration.

**Why `.NET Executable` and not `Shell Script`.** A shell configuration needs an interpreter,
and which one Rider picks on Windows depends on what is installed - a config that runs for the
author and not for anyone else is the failure mode. `.NET Executable` runs a named binary with
arguments and a working directory, and `dotnet.exe` is a named binary. Nothing else is involved.

Rider reads these from `.idea/.idea.<Solution>/.idea/runConfigurations/`, one file per
configuration. They are grouped into folders so the dropdown stays legible at thirty entries.
"""

import os
import re
import xml.sax.saxutils as sax

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOLUTION = "Shapez2-Mods"
OUT = os.path.join(ROOT, ".idea", ".idea." + SOLUTION, ".idea", "runConfigurations")

# Resolved once rather than left as bare "dotnet": Rider launches the binary itself rather than
# through a shell, so it does not get the PATH lookup a terminal would.
DOTNET = os.environ.get("RIDER_DOTNET", r"C:\Program Files\dotnet\dotnet.exe")

ACTIONS = [
    ("install", "1 Install to mods", "build",
     "Builds and installs into <persistent>/mods. The game must be CLOSED - the installed dll "
     "is memory-mapped while it runs, and the copy fails with MSB3027."),
    ("stage", "2 Stage to mods-dev", "build -p:Dev=true",
     "Stages into <persistent>/mods-dev, which is safe while the game is running. Nothing "
     "loads from there on its own: use mrl.reload <modname> with the Mod Reloader."),
    ("publish", "3 Publish to Steam", "build -t:SteamPublish",
     "Uploads whatever is already at OutputPath to the workshop item named in Steam/base.vdf. "
     "Build first - this target publishes, it does not compile."),
]

TEMPLATE = """<component name="ProjectRunConfigurationManager">
  <configuration default="false" name="{name}" type="DotNetExe" factoryName=".NET Executable" folderName="{folder}">
    <option name="EXE_PATH" value="{dotnet}" />
    <option name="PROGRAM_PARAMETERS" value="{args}" />
    <option name="WORKING_DIRECTORY" value="$PROJECT_DIR$/{workdir}" />
    <option name="PASS_PARENT_ENVS" value="1" />
    <option name="USE_EXTERNAL_CONSOLE" value="0" />
    <option name="USE_MONO" value="0" />
    <option name="RUNTIME_ARGUMENTS" value="" />
    <option name="PROJECT_PATH" value="" />
    <option name="PROJECT_EXE_PATH_TRACKING" value="0" />
    <option name="PROJECT_ARGUMENTS_TRACKING" value="0" />
    <option name="PROJECT_WORKING_DIRECTORY_TRACKING" value="0" />
    <option name="PROJECT_KIND" value="None" />
    <option name="PROJECT_TFM" value="" />
    <method v="2" />
  </configuration>
</component>
"""


def mods():
    """Every mod repo with a buildable project, as (repo folder, project name, has publish)."""
    found = []

    for repo in sorted(os.listdir(ROOT)):
        if not repo.startswith("Shapez2-"):
            continue

        path = os.path.join(ROOT, repo)
        if not os.path.isdir(path):
            continue

        for entry in sorted(os.listdir(path)):
            project = os.path.join(path, entry, entry + ".csproj")
            if not os.path.isfile(project):
                continue

            text = open(project, encoding="utf-8").read()
            found.append((repo, entry, 'Target Name="SteamPublish"' in text))
            break

    return found


def main():
    os.makedirs(OUT, exist_ok=True)

    # Clear only what this script wrote, so a hand-made configuration beside them survives.
    for stale in os.listdir(OUT):
        if re.match(r"^_mod_.*\.xml$", stale):
            os.remove(os.path.join(OUT, stale))

    written = 0

    for repo, project, publishes in mods():
        for action, folder, args, _ in ACTIONS:
            if action == "publish" and not publishes:
                continue

            name = "%s (%s)" % (project, action)
            body = TEMPLATE.format(
                name=sax.quoteattr(name)[1:-1],
                folder=sax.quoteattr(folder)[1:-1],
                dotnet=sax.quoteattr(DOTNET)[1:-1],
                args=sax.quoteattr(args)[1:-1],
                workdir=sax.quoteattr(repo)[1:-1],
            )

            path = os.path.join(OUT, "_mod_%s_%s.xml" % (project, action))
            open(path, "w", encoding="utf-8", newline="\n").write(body)
            written += 1

        print("  %-28s %s" % (project, "install, stage, publish" if publishes
                              else "install, stage"))

    print("\n%d configuration(s) in %s" % (written, os.path.relpath(OUT, ROOT)))
    print("Rider picks them up on the next reload of the solution.")


if __name__ == "__main__":
    main()
