CREATE EXTENSION IF NOT EXISTS pgcrypto;

DO $$ BEGIN
  CREATE ROLE course_owner NOLOGIN;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE ROLE course_publication LOGIN PASSWORD 'course_publication';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE ROLE course_runtime LOGIN PASSWORD 'course_runtime';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE ROLE workflow_worker LOGIN PASSWORD 'workflow_worker';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

GRANT course_publication TO postgres;
GRANT course_runtime TO postgres;
GRANT workflow_worker TO postgres;
