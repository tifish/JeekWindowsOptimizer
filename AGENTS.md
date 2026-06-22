# AGENTS.md

- JeekWindowsOptimizer is a Windows system optimization tool.

## Rules

- After finishing a feature or fixing a bug, automatically build and launch the program for me to test. If the program is already running, kill the process and run it again.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.
- When changing the format of `Data\*.tab` files, there is no need to keep backward compatibility with the old format; just keep the code and data in sync.
