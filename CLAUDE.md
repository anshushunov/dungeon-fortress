@AGENTS.md

# Claude Code

`AGENTS.md` is the canonical shared contract for every coding agent in this
repository. Do not duplicate or override it here.

- Start implementation from a GitHub Issue with explicit acceptance criteria.
- Read `docs/engineering/MULTI_AGENT_WORKFLOW.md` before creating branches,
  worktrees, subagents, or agent teams.
- Use an isolated worktree for every parallel task that can write files.
- Treat Claude auto memory as a local convenience, never as project truth.
  Promote durable findings to project documentation, an ADR, an Issue, or a PR.
- Project-scoped MCP configuration will live in `.mcp.json` after the Agent
  Bridge exists. Never commit tokens or machine-specific absolute paths.

