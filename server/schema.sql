CREATE TABLE IF NOT EXISTS presets (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  author TEXT NOT NULL,
  ini TEXT NOT NULL,
  hash TEXT NOT NULL UNIQUE,
  who TEXT NOT NULL,
  created INTEGER NOT NULL,
  likes INTEGER NOT NULL DEFAULT 0,
  downloads INTEGER NOT NULL DEFAULT 0,
  reports INTEGER NOT NULL DEFAULT 0,
  hidden INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS presets_new ON presets (hidden, created);
CREATE INDEX IF NOT EXISTS presets_top ON presets (hidden, likes);
CREATE INDEX IF NOT EXISTS presets_who ON presets (who, created);
CREATE TABLE IF NOT EXISTS likes (preset TEXT NOT NULL, client TEXT NOT NULL, PRIMARY KEY (preset, client));
CREATE TABLE IF NOT EXISTS reports (preset TEXT NOT NULL, client TEXT NOT NULL, PRIMARY KEY (preset, client));
