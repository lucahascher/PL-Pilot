# Security

PL/Pilot lets a language model act inside PL/SQL Developer with your database privileges. Please read
the safety model in the [README](README.md#safety-model) before you use it on databases that matter.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private
[*Report a vulnerability*](https://github.com/xZeenos/PL-Agent/security/advisories/new) form instead.
Include the PL/Pilot version (*Settings → About*), the PL/SQL Developer and Oracle versions and the steps
to reproduce. You will get an answer as soon as possible; fixes are released as a new version.

Especially welcome: ways to make the agent change data, code or files **without** an approval card,
to get around the production guard or the read-only modes, to run script in the chat window, or to leak
data or credentials.

## Security model in short

- Every change (editor, files, compile, DML/DDL) needs the user's approval. Destructive and hard-to-review
  statements (DROP/TRUNCATE/DELETE also inside PL/SQL, dynamic SQL, privileges, jobs, UPDATE without
  WHERE, PL/SQL in queries, logon triggers) and writes to git/IDE/agent configuration always ask, even
  with the auto-approve settings.
- Connections matching configurable patterns (connection text or the database's own names) are read-only
  for the agent; Plan mode and the read-only setting remove all changing tools.
- The agent uses a separate database session; uncommitted changes are shown with Commit / Rollback.
- The connection password is never read. The API key is stored encrypted with Windows DPAPI.
- The chat page runs only its own script (Content-Security-Policy with a per-load nonce); everything the
  model or the database returns is escaped.
- PL/Pilot sends data only to the LLM endpoint you configure and has no telemetry.

## Known limitations

- **Read-only is not a hard guarantee.** It means the agent issues no changing statements. A query can
  still call existing functions that have side effects (autonomous transactions, network packages). On
  production use a database account with read-only privileges.
- **Prompt injection.** Text from the database, files or the editor can contain instructions for the
  model. The approvals are the safety net: read what you approve, and keep the auto-approve settings off
  on important databases.
- Statements that already run in the database cannot be interrupted by a plug-in; *Stop* only stops
  waiting for them.
