# Agent skills for shapez 2 modding

One skill, `shapez2-modding`, that teaches a coding agent to build, debug and publish
shapez 2 mods with the ShapezShifter API.

```text
shapez2-modding/
├── SKILL.md                  router — task table, five cross-cutting rules, script index
├── references/
│   ├── setup.md              project, csproj, manifest, publicizer, build/install loop
│   ├── add-content.md        buildings, islands, toolbar, research, translations
│   ├── hooking.md            detours, interceptors, lane hooks, staying compatible
│   ├── debugging.md          the diagnostic ladder, in the order that pays off
│   └── traps.md              failures that compile cleanly and cost a session
└── scripts/
    ├── check-env.sh          env vars, is the game running, what is actually installed
    ├── scan-log.sh           Player.log triage — finds the first failure, not the loudest
    └── new-mod.sh            scaffold a buildable project
```

**Why one skill and not several.** The references and scripts are shared: a task that adds
an island needs the toolbar page, the trap list and the log scanner, and splitting on
add / hook / debug means either duplicating those or writing cross-skill paths that break
the moment someone copies a single directory. `SKILL.md` stays short and routes; the agent
reads only the reference the task needs.

## Installing

**Claude Code, as a plugin** — gets updates over git:

```
/plugin marketplace add <owner>/<repo>
/plugin install shapez2-modding
```

Requires a `.claude-plugin/marketplace.json` at the repo root; not yet added.

**Any agent, by copying** — no update path, but works everywhere:

```bash
cp -r skills/shapez2-modding ~/.claude/skills/          # user-wide
cp -r skills/shapez2-modding <your-mod-repo>/.claude/skills/   # one project
```

**Copilot, Cursor, Codex** do not read `SKILL.md`. Point them at the same files from an
`AGENTS.md` (or `.github/copilot-instructions.md`) in your mod repo:

```markdown
For shapez 2 modding, read `.claude/skills/shapez2-modding/SKILL.md` and the reference
it routes you to before making changes.
```

## Scripts

Bash — Git Bash on Windows, which the toolchain already assumes. They read `SPZ2_PATH`,
`SPZ2_PERSISTENT` and `SPZ2_SHIFTER`; on Windows the game sets those with
`"shapez 2.exe" --set-modding-env-vars`. Each takes `--help`.

They are the part a documentation page cannot be. `check-env.sh` answers "is the thing I
am about to test the thing I just built", which is the most common wasted hour in this
ecosystem, and `scan-log.sh` encodes the fact that the visible exception in a shapez 2
crash is usually a second-order effect of a failure far above it.

## Keeping it true

The references are distilled from the long-form pages in `docs/` and `docs/howto/` of this
repo, and they name the page behind each section. When game behaviour changes, the doc page
is the source of truth — update it first, then the reference.

Everything in `references/traps.md` is a real failure that reached a working session, with
the class or method that proves it. Add to it on the same terms: name the mechanism, not
just the symptom.
