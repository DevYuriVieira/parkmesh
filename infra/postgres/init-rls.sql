-- Provision non-superuser application role for PostgreSQL Row-Level Security enforcement.
-- The API connects as the schema owner for DDL initialization (InitializeAsync), then
-- scopes data-plane audit transactions via SET LOCAL ROLE parkmesh_app when Postgres:AppRole is set.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'parkmesh_app') THEN
        CREATE ROLE parkmesh_app
            NOLOGIN
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOINHERIT
            NOREPLICATION
            NOBYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO parkmesh_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO parkmesh_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO parkmesh_app;
