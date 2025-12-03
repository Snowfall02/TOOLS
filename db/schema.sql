-- DB schema for deep dive tool
CREATE TABLE IF NOT EXISTS equities (
  id SERIAL PRIMARY KEY,
  symbol TEXT NOT NULL UNIQUE,
  name TEXT
);

CREATE TABLE IF NOT EXISTS deep_dives (
  id SERIAL PRIMARY KEY,
  equity_id INTEGER REFERENCES equities(id) ON DELETE CASCADE,
  requested_at TIMESTAMP WITH TIME ZONE DEFAULT now(),
  interval TEXT, -- e.g., 1d, 1h, 15m
  start_time TIMESTAMP,
  end_time TIMESTAMP,
  image_path TEXT,
  image_data BYTEA,        -- image stored in Postgres
  job_id TEXT,             -- hangfire job id
  status TEXT DEFAULT 'queued', -- queued / processing / done / failed
  metadata JSONB
);

CREATE TABLE IF NOT EXISTS candles (
  id SERIAL PRIMARY KEY,
  deep_dive_id INTEGER REFERENCES deep_dives(id) ON DELETE CASCADE,
  ts TIMESTAMP WITH TIME ZONE,
  open NUMERIC,
  high NUMERIC,
  low NUMERIC,
  close NUMERIC,
  volume NUMERIC,
  pct_move NUMERIC -- (close-open)/open * 100
);
