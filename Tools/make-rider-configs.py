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

**`ShConfigurationType`, because its id is knowable.** Rider rejects a wrong `type` with
"Unknown run configuration type", and Rider's own .NET types are a bad thing to guess at: their
classes are obfuscated, so the string constants in them are fragmented and the plausible-looking
ones are display names. `DotNetExe` and `DotNetExecutable` were both tried and both rejected.

`ShConfigurationType` is IntelliJ platform rather than Rider, its id is public and long stable,
and the jar scan confirms Rider bundles it (`com.intellij.sh.run.ShConfigurationType`). It runs
`dotnet` through bash, which is exactly what a terminal does, so `dotnet` has to be on PATH -
and it already is, or none of the command lines in CLAUDE.md would work either.

`--dotnet-exe` emits the Rider-native `.NET Executable` form instead, for anyone who finds the
right id. It is the nicer configuration if it ever works: no shell, no PATH.

**The working directory is the project folder, not the mod repo.** Two of the twelve repos -
Decoration Blocks and Extra Shape Parts - have no solution file at their root, and a bare
`dotnet build` there fails with `MSB1003: Specify a project or solution file`. The project
folder always holds exactly one `.csproj`, so running from there works for every mod whatever
the repo layout, and builds the project rather than whatever a solution happens to contain.

Rider reads these from `.idea/.idea.<Solution>/.idea/runConfigurations/`, one file per
configuration. They are grouped into folders so the dropdown stays legible at thirty entries.
"""

import os
import re
import sys
import xml.sax.saxutils as sax

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOLUTION = "Shapez2-Mods"
OUT = os.path.join(ROOT, ".idea", ".idea." + SOLUTION, ".idea", "runConfigurations")

# Resolved once rather than left as bare "dotnet": Rider launches the binary itself rather than
# through a shell, so it does not get the PATH lookup a terminal would.
DOTNET = os.environ.get("RIDER_DOTNET", r"C:\Program Files\dotnet\dotnet.exe")
BASH = os.environ.get("RIDER_BASH", r"C:\Program Files\Git\bin\bash.exe")

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

EXE_TEMPLATE = """<component name="ProjectRunConfigurationManager">
  <configuration default="false" name="{name}" type="DotNetExecutable" factoryName=".NET Executable" folderName="{folder}">
    <option name="EXE_PATH" value="{dotnet}" />
    <option name="PROGRAM_PARAMETERS" value="{args}" />
    <option name="WORKING_DIRECTORY" value="$PROJECT_DIR$/{workdir}/{project}" />
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

SH_TEMPLATE = """<component name="ProjectRunConfigurationManager">
  <configuration default="false" name="{name}" type="ShConfigurationType" folderName="{folder}">
    <option name="SCRIPT_TEXT" value="dotnet {args}" />
    <option name="INDEPENDENT_SCRIPT_PATH" value="true" />
    <option name="SCRIPT_PATH" value="" />
    <option name="SCRIPT_OPTIONS" value="" />
    <option name="INDEPENDENT_SCRIPT_WORKING_DIRECTORY" value="false" />
    <option name="SCRIPT_WORKING_DIRECTORY" value="$PROJECT_DIR$/{workdir}/{project}" />
    <option name="INDEPENDENT_INTERPRETER_PATH" value="false" />
    <option name="INTERPRETER_PATH" value="{bash}" />
    <option name="INTERPRETER_OPTIONS" value="" />
    <option name="EXECUTE_IN_TERMINAL" value="true" />
    <option name="EXECUTE_SCRIPT_FILE" value="false" />
    <envs />
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
    shell = "--dotnet-exe" not in sys.argv
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
            body = (SH_TEMPLATE if shell else EXE_TEMPLATE).format(
                name=sax.quoteattr(name)[1:-1],
                folder=sax.quoteattr(folder)[1:-1],
                dotnet=sax.quoteattr(DOTNET)[1:-1],
                bash=sax.quoteattr(BASH)[1:-1],
                args=sax.quoteattr(args)[1:-1],
                workdir=sax.quoteattr(repo)[1:-1],
                project=sax.quoteattr(project)[1:-1],
            )

            path = os.path.join(OUT, "_mod_%s_%s.xml" % (project, action))
            open(path, "w", encoding="utf-8", newline="\n").write(body)
            written += 1

        print("  %-28s %s" % (project, "install, stage, publish" if publishes
                              else "install, stage"))

    print("\n%d %s configuration(s) in %s"
          % (written, "Shell Script" if shell else ".NET Executable",
             os.path.relpath(OUT, ROOT)))
    print("Rider picks them up on the next reload of the solution.")


if __name__ == "__main__":
    main()
