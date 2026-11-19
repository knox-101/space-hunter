# Security

Space Hunter deletes files, so a bug that makes it delete the wrong thing counts as a security problem.

## Reporting a problem

Please **don't open a public issue** for a security problem. Instead, report it privately on this repository's
**Security** tab with **Report a vulnerability**. Include:

- the Space Hunter version (on Windows, the exe's Properties → Details; on Mac, Space Hunter → About),
- what was deleted or exposed that shouldn't have been,
- the folder paths involved, with any personal names replaced.

You'll get a reply within a week. Fixes go out as a new release, credited to you unless you'd rather not be named.

## What Space Hunter promises

- It works offline. It sends nothing anywhere, and has no account and no telemetry.
- It only deletes inside the folders listed for each item, or folders you added yourself.
- It never cleans personal folders, protected folders or AI agents' memory folders, and it never follows links or
  junctions.
- Official downloads come only from this repository's **Releases** page.
