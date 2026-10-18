# Contributing

This is an alpha project with Windows release packages under the [MIT License](LICENSE). By submitting a contribution, you agree to license it under MIT.

## Useful bug reports

Include Windows version and architecture, which widget is affected, a minimal reproduction, expected/actual behavior and the exact source revision. For providers, include the CLI version and a **redacted structure**, not the full authenticated response. Distinguish fresh, stale and missing data. Hardware reports should name the sensor source and whether optional monitoring software was installed.

Never attach credentials, tokens, account files, `state.json`, raw logs, network journals or private executable paths without inspecting and redacting them first. Do not paste provider secrets into an issue. Report a suspected secret exposure privately to the repository owner through an available private channel.

## Local workflow

Use the build commands in the README. The build scripts do not stop or launch an installed widget. Keep development output separate from a running installation. Do not change autostart, notification-area settings or credentials while testing without an explicit user action.

Keep provider calls opt-in. Prefer synthetic parser fixtures for automated work; never query live accounts or launch authenticated provider CLIs merely to verify a pull request. The existing `--fixtures` mechanism can fall back to live sources when a fixture is missing: it is **not an offline sandbox**. Pair any fixture work with an explicit provider allowlist and complete fixtures.

For a change, verify the smallest relevant behavior, compile the affected widget, parse example JSON and document untested runtime or hardware behavior. Do not claim platform support from compilation alone. Sensor dependencies retain their upstream licences. Preserve the installer payload notices, verify official checksums and signatures, and keep startup and elevation opt-in. Do not claim a clean-machine installation was tested when only extraction or a dry run was performed.

The `docs/index.html` site is standalone HTML/CSS with no dependencies. Check narrow and wide layouts, keyboard focus, link targets and readable contrast after editing. Use actual application exports. Label controlled sample data as demo data and distinguish it from real sensor readings. Ensure the site works under the GitHub Pages project subpath. Validate release asset names and image targets; publication must be verified separately from local checks.
