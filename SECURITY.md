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

- It has no account and no telemetry. Its only network request is one check for a newer release on GitHub when it
  opens; nothing about you or your files is sent, and offline it simply skips the check.
- Updates are installed only if their signature matches the project's release key, which is built into the app.
- It only deletes inside the folders listed for each item, or folders you added yourself.
- It never cleans personal folders, protected folders or AI agents' memory folders, and it never follows links or
  junctions.
- Official downloads come only from this repository's **Releases** page.
