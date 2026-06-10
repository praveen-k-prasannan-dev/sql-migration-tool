<div align="center">

<img src="image1.png" width="110" alt="SQL Migration Tool Logo" />

# SQL Migration Tool

**A portable, version-agnostic SQL Server database backup and restore utility**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?style=flat-square&logo=windows&logoColor=white)](https://github.com)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2008%20→%202022-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Language](https://img.shields.io/badge/Language-C%23%2012-239120?style=flat-square&logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![License](https://img.shields.io/badge/License-MIT-22C55E?style=flat-square)](LICENSE)
[![Version](https://img.shields.io/badge/Version-1.0.0-F59E0B?style=flat-square)](https://github.com)

*Completely developed by [Claude AI](https://claude.ai) — Anthropic*

</div>

---

## 📋 Table of Contents

- [Overview](#-overview)
- [Why This Tool?](#-why-this-tool)
- [Key Features](#-key-features)
- [Application Screens](#-application-screens)
- [System Requirements](#-system-requirements)
- [Quick Start — Running the App](#-quick-start--running-the-app)
- [Usage Guide](#-usage-guide)
  - [Step 1 — Connect](#step-1--connect-to-sql-server)
  - [Step 2 — Backup](#step-2--backup-a-database)
  - [Step 3 — Restore / Install](#step-3--restore--install-to-target)
- [What Gets Exported](#-what-gets-exported)
- [Backup Folder Structure](#-backup-folder-structure)
- [Building from Source](#-building-from-source)
- [Project Structure](#-project-structure)
- [Creating a Release ZIP](#-creating-a-release-zip)
- [Credits](#-credits)
- [License](#-license)

---

## 🌐 Overview

**SQL Migration Tool** is a Windows desktop application that solves one of the most common database administration challenges: migrating a SQL Server database from one server version to another without compatibility issues.

SQL Server's native `.bak` backup files are **version-locked** — a backup created on SQL Server 2019 cannot be restored to SQL Server 2016. This tool eliminates that constraint by exporting the entire database as **portable, human-readable SQL scripts** — plain text files that can be applied to any SQL Server version from 2008 all the way up to 2022.

> **No .bak files. No version conflicts. No SSMS required.**

---

## 💡 Why This Tool?

| Problem | Traditional .bak | SQL Migration Tool |
|---|---|---|
| Restore to older SQL Server | ❌ Not supported | ✅ Works on any version |
| Requires SQL Server Agent | ❌ Required | ✅ Not needed |
| Human-readable output | ❌ Binary format | ✅ Plain SQL scripts + JSON |
| Transfer across networks | ❌ Large binary files | ✅ Compressed-friendly text |
| Partial object selection | ❌ All or nothing | ✅ Pick tables, views, procs |
| Track what was backed up | ❌ No log | ✅ Manifest + progress log |

---

## ✨ Key Features

### 🔌 Connection Management
- Supports **Windows Authentication** and **SQL Server Authentication**
- **Connection Profiles** — save and recall server/database combinations with one click
- Built-in **Test Connection** with instant feedback
- Passwords stored as Base64 in `%AppData%\SqlMigrationTool\profiles.json`

### 📦 Backup (Export)
- Selectively export any combination of database objects
- Data exported in **configurable chunk sizes** (prevents memory issues on large tables)
- Live progress log with **✔ / ✘** status per item
- **Summary table** showing totals, successes, and failures by object type
- Graceful error handling — one failed object doesn't abort the whole backup

### ▶ Install (Restore)
- Reads the backup manifest and replays objects in the correct dependency order:
  `Tables → Data → Views → Stored Procs → Functions → Triggers → Foreign Keys → Indexes`
- Foreign keys applied **last** to avoid circular-dependency insert failures
- Live progress list + summary table with real-time counts
- Abort button to cancel mid-install safely

### 🎨 User Interface
- Visual Studio 2022–style animated **splash screen** with progress bar, skip link
- **About** link on every screen (re-opens splash)
- Clean blue/green themed headers per screen
- Resizable backup and install windows (1000 × 750)
- All forms display the application icon in title bar and taskbar

---

## 🖥 Application Screens

| Screen | Description |
|---|---|
| **Splash Screen** | Animated startup screen with app logo, description, author, and progress bar. Skip link available. |
| **Connection** | Enter SQL Server details. Supports Windows & SQL Auth. Save/recall profiles. |
| **Operation** | Choose between BACKUP or INSTALL mode. |
| **Backup** | Select objects, configure chunk size, choose output folder, run backup with live log. |
| **Install** | Load a backup folder, review manifest, run install with dependency-ordered progress. |

---

## 💻 System Requirements

### To Run the Application
| Requirement | Minimum |
|---|---|
| Operating System | Windows 10 (x64) or Windows 11 |
| .NET Runtime | **Included** — self-contained build, no separate install needed |
| SQL Server | Any version from 2008 R2 to 2022 (source and/or target) |
| RAM | 256 MB |
| Disk | 50 MB for the app + space for your backup files |

### To Build from Source
| Requirement | Version |
|---|---|
| .NET SDK | 8.0 or later |
| Visual Studio | 2022 (recommended) or VS Code with C# extension |
| Windows | Required (WinForms, win-x64 target) |

---

## 🚀 Quick Start — Running the App

1. Download `SqlMigrationTool.zip` from the [Releases](../../releases) page
2. Extract the ZIP to any folder (e.g. `C:\Tools\SqlMigrationTool\`)
3. Double-click **`SqlMigrationTool.exe`**
4. No installation, no admin rights, no .NET install needed

---

## 📖 Usage Guide

### Step 1 — Connect to SQL Server

1. The splash screen plays on startup (click **Skip** to dismiss immediately)
2. On the **Connection** screen:
   - Select a saved **Profile** from the dropdown, or enter details manually
   - Enter **Server Name / IP** (e.g. `localhost`, `192.168.1.10`, `(localdb)\MSSQLLocalDB`)
   - Enter **Port** (default `1433`, leave as-is for most setups)
   - Choose **Windows Authentication** (uses your Windows login) or **SQL Server Authentication**
   - Enter the **Database Name**
3. Click **Test Connection** — a green status means you're good to go
4. The connection is automatically saved as a Profile for next time
5. Click **Next ▶** to continue

### Step 2 — Backup a Database

1. On the **Operation** screen, click **📦 BACKUP**
2. On the **Backup** screen:
   - Click **Browse** to choose an output folder
   - Tick the object types you want to export (Tables, Views, Stored Procs, Functions, Triggers)
   - The object list will populate — tick/untick individual items as needed
   - Set the **Chunk Size** (number of rows per data file, default 1000)
   - Click **▶ Start Backup**
3. Watch the **progress log** and **summary table** update in real time
4. When complete, the output folder contains everything needed to restore

### Step 3 — Restore / Install to Target

1. Connect to the **target** SQL Server (repeat Step 1 with different server details)
2. On the **Operation** screen, click **▶ INSTALL**
3. On the **Install** screen:
   - Click **Browse** and select the backup folder created in Step 2
   - Click **Load** to read the manifest — it shows all objects found
   - Review the list, then click **▶ Start Install**
4. Objects are installed in dependency order automatically
5. The summary table tracks Tables, Data, Views, Procs, Functions, Triggers, FKs, and Indexes
6. Any errors are shown inline — other objects continue to install

---

## 📁 What Gets Exported

| Object Type | What is captured |
|---|---|
| **Tables** | `CREATE TABLE` with all columns, data types, NULLability, defaults, PRIMARY KEY, UNIQUE constraints, computed columns with `AS` expressions |
| **Data** | All rows exported to JSON chunk files, with proper type handling for `datetime`, `image`, `varbinary`, `datetimeoffset`, `time` |
| **Views** | Full `CREATE VIEW` script including schema binding |
| **Stored Procedures** | Full `CREATE PROCEDURE` with all parameters |
| **Functions** | Scalar, inline table-valued, and multi-statement TVFs |
| **Triggers** | DML triggers with `ON` table binding |
| **Foreign Keys** | `ALTER TABLE … ADD CONSTRAINT … FOREIGN KEY` with `ON DELETE / ON UPDATE` actions |
| **Indexes** | `CREATE [UNIQUE] [CLUSTERED] INDEX` including included columns |

---

## 🗂 Backup Folder Structure

After a backup, the output folder contains:

```
MyBackup\
├── manifest.json              ← Master index of all backed-up objects
├── schema\
│   ├── Tables\
│   │   ├── dbo.Customers.sql
│   │   ├── dbo.Orders.sql
│   │   └── ...
│   ├── Views\
│   ├── StoredProcedures\
│   ├── Functions\
│   └── Triggers\
├── data\
│   ├── dbo.Customers\
│   │   ├── chunk_001.json
│   │   ├── chunk_002.json
│   │   └── ...
│   └── dbo.Orders\
├── relations\
│   └── ForeignKeys.sql
└── indexes\
    └── Indexes.sql
```

All files are **plain text** — you can open, inspect, and even manually edit them before restoring.

---

## 🔨 Building from Source

```powershell
# 1. Clone the repository
git clone https://github.com/YOUR_USERNAME/DBBackUpAndRestore.git
cd DBBackUpAndRestore

# 2. Restore NuGet packages and build
cd SqlMigrationTool
dotnet build -c Release

# 3. Run directly
dotnet run --project SqlMigrationTool\SqlMigrationTool -c Release

# 4. Publish a self-contained executable
dotnet publish SqlMigrationTool\SqlMigrationTool -c Release -r win-x64 --self-contained true -o .\publish
```

---

## 🗃 Project Structure

```
DBBackUpAndRestore\
├── image1.png                          ← Application logo source
├── image2.png                          ← Alternate logo
├── README.md                           ← This file
├── LICENSE
└── SqlMigrationTool\
    ├── SqlMigrationTool.sln
    ├── SqlMigrationTool\               ← Main WinForms project
    │   ├── Program.cs                  ← Entry point & navigation loop
    │   ├── AppIcon.cs                  ← Embedded .ico loader
    │   ├── AppImage.cs                 ← Embedded PNG loader (splash logo)
    │   ├── SqlMigrationTool.csproj
    │   ├── Forms\
    │   │   ├── frmSplash.cs            ← Animated splash screen (owner-drawn)
    │   │   ├── frmConnection.cs        ← Server connection + profiles
    │   │   ├── frmOperation.cs         ← Backup / Install selector
    │   │   ├── frmBackup.cs            ← Backup wizard + live progress
    │   │   └── frmInstall.cs           ← Restore wizard + live progress
    │   ├── Models\
    │   │   ├── ConnectionConfig.cs     ← Connection parameters
    │   │   ├── ConnectionProfile.cs    ← Saved profile model
    │   │   ├── DatabaseObject.cs       ← Object type + schema + name
    │   │   ├── BackupProgress.cs       ← Structured progress record
    │   │   ├── InstallManifest.cs      ← Backup folder manifest
    │   │   ├── InstallProgress.cs      ← Install progress record
    │   │   └── DataChunk.cs            ← Row data chunk model
    │   ├── Services\
    │   │   ├── SqlConnectionService.cs ← Connection test helper
    │   │   ├── SchemaExporter.cs       ← DDL script generator
    │   │   ├── DataExporter.cs         ← Row data → JSON exporter
    │   │   ├── BackupService.cs        ← Orchestrates full backup
    │   │   ├── InstallService.cs       ← Orchestrates full install
    │   │   ├── ProfileService.cs       ← Save/load connection profiles
    │   │   └── ErrorLogger.cs          ← Error log writer
    │   └── Resources\
    │       ├── app.ico                 ← Application icon (7 sizes)
    │       └── image1.png              ← Logo embedded resource
    └── TestConsole\                    ← Developer test harness (console)
        ├── TestConsole.csproj
        └── Program.cs
```

---

## 📦 Creating a Release ZIP

Run these commands from the repository root to produce a distributable ZIP:

```powershell
# Publish a self-contained, folder-based release
dotnet publish SqlMigrationTool\SqlMigrationTool `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o .\Release\SqlMigrationTool

# Zip the output folder (PowerShell 5+)
Compress-Archive `
    -Path   .\Release\SqlMigrationTool\* `
    -DestinationPath .\Release\SqlMigrationTool-v1.0.0-win-x64.zip `
    -Force

Write-Host "ZIP created: .\Release\SqlMigrationTool-v1.0.0-win-x64.zip"
```

The resulting ZIP is **self-contained** — recipients just extract and run `SqlMigrationTool.exe`. No .NET installation required.

---

## 👤 Credits

| Role | Name |
|---|---|
| **Created by** | Praveen Kuttathara Prasannan |
| **Developed by** | [Claude AI](https://claude.ai) — Anthropic |
| **Framework** | .NET 8 / Windows Forms |
| **Database Driver** | Microsoft.Data.SqlClient 5.2.1 |

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.

```
MIT License  ©  2025  Praveen Kuttathara Prasannan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
```

---

<div align="center">

Made with ❤️ for the SQL Server community

*If this tool saved you time, consider giving it a ⭐ on GitHub!*

</div>
