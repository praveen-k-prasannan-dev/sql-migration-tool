# Privacy Policy: SQL Migration Tool

**Effective date:** 16 October 2026

SQL Migration Tool is a Windows desktop application for backing up and restoring SQL Server
databases. This policy explains what the application does with information.

## Summary

SQL Migration Tool **does not collect, transmit or share any personal data**. It contains no
analytics, telemetry, advertising or crash reporting, and it never contacts the developer or any
third-party service.

## Network connections

The application connects only to the SQL Server instances that you enter on the Connect screen.
It sends and receives the queries and data needed to back up or restore the database you
choose. Nothing is sent anywhere else.

## Information stored on your computer

- **Connection profiles (optional).** If you choose to save a connection, the server name, port,
  database name, authentication type, user name and password are stored in your Windows user
  profile. The password is encrypted with Windows Data Protection (DPAPI) for your Windows
  account, so other user accounts and other computers cannot read it. You can remove saved
  profiles by deleting the profile file (`profiles.json` in the application's data folder) or by
  uninstalling the app.
- **Backup files.** Backups (SQL scripts, data files and a manifest), install logs
  (`error.log`), previews and optional cleanup scripts are written only to the folders you
  choose. They contain your database's structure and data, so store and share them with the same
  care as the database itself.

The developer has no access to any of this information.

## Children's privacy

The application does not collect information from anyone, including children.

## Changes to this policy

Changes will be published on this page with a new effective date.

## Contact

Questions about this policy: open an issue at
<https://github.com/praveen-k-prasannan-dev/sql-migration-tool/issues>.
