-- Roles and schemas
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

CREATE SCHEMA IF NOT EXISTS course AUTHORIZATION course_owner;
CREATE SCHEMA IF NOT EXISTS api AUTHORIZATION course_owner;
CREATE SCHEMA IF NOT EXISTS payment AUTHORIZATION course_owner;
CREATE SCHEMA IF NOT EXISTS operation AUTHORIZATION course_owner;
CREATE SCHEMA IF NOT EXISTS autocheck AUTHORIZATION course_owner;

GRANT USAGE ON SCHEMA course, api, payment, operation, autocheck TO course_publication, course_runtime;
ALTER ROLE course_publication SET search_path = course, public;
ALTER ROLE course_runtime SET search_path = course, api, public;
