# Microsoft Store listing: SQL Migration Tool 2.0

Text for Partner Center, field by field (**Store listings → English (United States)**). Character
limits are Partner Center's.

---

## Product name

SQL Migration Tool

## Description *(max 10,000 characters)*

Move a SQL Server database to any SQL Server version, older or newer, without .bak files.

SQL Server backups (.bak) only restore to the same or a newer version. SQL Migration Tool exports
your database as portable SQL scripts and data files instead, then rebuilds it on any SQL Server
from 2008 to 2025. That includes going back to an older version, moving between editions, or
copying a database into a different server environment.

WHAT IT COPIES
Tables with their columns, keys, defaults, CHECK and UNIQUE constraints, collations and
partitioning. Views, stored procedures, functions and triggers, created in dependency order.
Schemas, user-defined data types and table types, sequences (continuing from the current value),
synonyms, XML schema collections, indexes (filtered, included columns, columnstore, indexed views),
foreign keys, extended properties and database triggers. All table data, chunked so large tables
don't exhaust memory. Optionally, database users, roles and permissions.

INSTALL THE WAY YOU NEED
Choose how existing objects on the target are treated:
• Drop and Create: replace them
• Create Only: never touch what already exists
• Create or Alter: update in place, keeping permissions, and add missing columns to existing tables
Choose how data is loaded into tables that already have rows: skip them, truncate and reload,
append, or upsert by key. Foreign keys, constraints and triggers are handled safely during the
load and restored afterwards.

SAFE BY DESIGN
• Preview: see every script exactly as it would run, without changing anything
• Warnings when the target's collation or compatibility level differs from the source
• An optional cleanup script that is inert until you edit it and refuses to run against the source
• A detailed log of every warning and error

PRIVATE
No telemetry, no accounts, no cloud. The app talks only to the SQL Servers you connect to. Saved
connection passwords are encrypted with Windows Data Protection.

Backups are plain text (SQL and JSON), so you can read them, keep them in source control, or run
the scripts yourself in SQL Server Management Studio.

## What's new in this version *(max 1,500 characters)*

Version 2.0, a major update:
• Exports user-defined table types, data types, schemas, sequences, synonyms, XML schema
  collections, partitioning, extended properties and database triggers
• Complete constraints and indexes, with collation, precision and disabled states preserved
• Views, functions and procedures created in dependency order
• Script modes: Drop and Create, Create Only, Create or Alter
• Data options for existing tables: skip, truncate and load, append, upsert
• Install preview, optional cleanup script, optional users/roles/permissions export
• Much faster data loading (bulk copy) and exact date/time and decimal precision
• Saved passwords encrypted with Windows Data Protection

## Short description *(max 1,000 characters)*

Back up a SQL Server database as portable scripts and data, and restore it to any SQL Server version
from 2008 to 2025, including older versions that cannot restore a .bak file.

## Product features *(up to 20; max 200 characters each)*

1. Restore to any SQL Server version from 2008 to 2025, including older versions than the source
2. Portable, human-readable backups: SQL scripts plus JSON data files and a manifest
3. Copies tables, views, procedures, functions, triggers, types, sequences, synonyms and more
4. User-defined table types and data types, schemas, XML schema collections and partitioning
5. Keys, CHECK/UNIQUE constraints, collations, indexes and extended properties preserved
6. Objects created in dependency order for clean installs
7. Script modes: Drop and Create, Create Only, or Create or Alter
8. Data options for existing tables: skip, truncate and load, append, or upsert by key
9. Install preview shows every script before anything changes
10. Optional export of database users, roles and permissions
11. Fast bulk data loading with exact date/time and decimal precision
12. Collation and compatibility level checks against the target
13. Windows and SQL Server authentication; saved connections with encrypted passwords
14. No telemetry, accounts or cloud: it connects only to your servers

## Search terms *(up to 7; max 30 characters each)*

1. SQL Server backup
2. SQL Server restore
3. database migration
4. SQL Server downgrade
5. SQL scripts export
6. database copy
7. SQL Server tools

## Other fields

| Field | Value |
|---|---|
| Category | Developer tools |
| Privacy policy URL | `https://github.com/praveen-k-prasannan-dev/sql-migration-tool/blob/main/docs/privacy-policy.md` |
| Website | `https://github.com/praveen-k-prasannan-dev/sql-migration-tool` |
| Support contact info | `https://github.com/praveen-k-prasannan-dev/sql-migration-tool/issues` |
| Copyright and trademark info | © 2026 Praveen Kuttathara Prasannan |
| Additional system requirements | Access to a SQL Server instance (2008 or later) |
| Pricing | Your choice (Free fits the MIT licence) |

## Age rating questionnaire

The app has no user interaction with other people, no user-generated content shared online,
no purchases, no location, and no personal data collection. Answer **No** to every content and
interaction question; the expected rating is **3+ / Everyone**.

## Screenshots *(min 1, up to 10; desktop at least 1366×768)*

The current repository screenshots are smaller than 1366×768 and show the 1.0 screens. Take new
screenshots of the 2.0 app (Connect, Backup with the script-mode and cleanup options, Install with
script mode / data / preview, a finished install), then frame them:

```powershell
SqlMigrationTool\Packaging\Frame-Screenshots.ps1 -Source <folder with screenshots>
```

This centres each one on a 1920×1080 navy canvas in `SqlMigrationTool\Packaging\out\store-screenshots`.
Suggested captions: "Connect with Windows or SQL authentication", "Choose what to back up and how
it installs", "Install with a script mode and data option", "Preview every script before it runs".
