-- Space Hunter's public totals. One row per install that said yes, and one row per day for "freed in the last N days".
CREATE TABLE IF NOT EXISTS installs (
  id TEXT PRIMARY KEY,          -- a random number the app made up; not tied to the person or the computer
  os TEXT NOT NULL,             -- windows, mac or linux
  v TEXT NOT NULL,              -- app version
  freed INTEGER NOT NULL,       -- total bytes freed so far, as the app reports it
  cleans INTEGER NOT NULL,      -- number of cleans so far
  first_seen INTEGER NOT NULL,  -- ms since 1970
  last_seen INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS installs_last_seen ON installs (last_seen);
CREATE TABLE IF NOT EXISTS days (
  day TEXT PRIMARY KEY,         -- YYYY-MM-DD (UTC)
  freed INTEGER NOT NULL
);
