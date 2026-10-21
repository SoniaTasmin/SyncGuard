#!/bin/bash
# Creates both databases and applies the idempotent schema scripts. Run by the db-init compose service.
set -euo pipefail
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" # keeps the password off the command line
sql() { /opt/mssql-tools18/bin/sqlcmd -C -b -S sqlserver -U sa "$@"; }

for db in SyncGuardSource SyncGuardDestination; do
  sql -Q "IF DB_ID('$db') IS NULL CREATE DATABASE [$db]"
done
sql -d SyncGuardSource -i /db/source.sql
sql -d SyncGuardDestination -i /db/destination.sql
echo "SyncGuard schema applied."
