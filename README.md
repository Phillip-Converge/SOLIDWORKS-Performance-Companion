# Companion for SOLIDWORKS

**See why SOLIDWORKS is slow, in plain English, while it's happening.**

Companion for SOLIDWORKS is a free Windows app that sits beside SOLIDWORKS and tells you what's going on: what
SOLIDWORKS is doing right now, what's holding it up, and what you can do about it. It's built for the people who use
SOLIDWORKS every day, not for IT experts. Every computer word it uses is explained.

When SOLIDWORKS freezes, shows "Not Responding", or takes ages to open an assembly or switch a drawing sheet, the
Companion shows you why: the file waiting on a slow network folder, the antivirus checking every part, memory running
out, the graphics driver resetting, files saved in a SOLIDWORKS version from 2010, or a Windows setting holding the
whole PC back. It catches the moment, keeps it, and explains it step by step.

It only watches. It never changes SOLIDWORKS, its settings or your files.

Created by **Phillip Lewis**, Phillip.Lewis@converge.design. Questions, ideas and problems are welcome by e-mail.

---

## What it does

- **What's happening, live.** A running log, in everyday words, of what SOLIDWORKS is doing (opening, rebuilding,
  switching sheets, saving) and what the rest of the computer is doing to it.
- **"Hung up" panel.** Anything that holds SOLIDWORKS up slides out of the log into its own list: freezes, slow opens,
  slow sheet switches, windows that take longer than usual to appear after you click, files saved in older SOLIDWORKS
  versions, and anything Windows recorded at the same moment (a program crashing, the graphics driver resetting, disk
  errors, low memory, network drops, Windows Defender blocking or quarantining a file). Click any of them for:
  what happened, why it matters, what to try, how to look into it further, and the computer words explained.
- **"Is it stuck?"** During a freeze it tells you whether SOLIDWORKS is still working or has gone quiet, and when it's
  reasonable to give up on it.
- **Fixes.** A check of the things that most often slow SOLIDWORKS down (power settings, the graphics card it uses,
  Windows' memory and object limits, antivirus, folder permissions, VPN, SOLIDWORKS file locations, PDM client
  settings), each with plain step-by-step instructions.
- **Crashes.** Reads SOLIDWORKS's own crash records and explains them.
- **The basics at a glance.** Small live charts of the processor, each core, memory, graphics card, disk and network.
- **Ask your own AI (optional).** Ask Claude, ChatGPT or an AI running on your own PC about what's happening, using
  your own account. Off unless you set it up.
- Light and dark look, and a slim always-on-top strip for a second monitor.

## What it never does

- It never changes SOLIDWORKS, its settings or your files.
- It never stops, restarts or ends SOLIDWORKS, its licensing, or PDM.
- It never sends anything anywhere, unless you set up "Ask AI". Even then, names of your files, folders, servers, PC
  and you are hidden first, and it shows you exactly what will be sent before you send it.
- Everything it records stays on your PC.

---

## Set it up

### What you need

- A Windows 10 or Windows 11 PC (64-bit) with **SOLIDWORKS installed**. It was built and tested on SOLIDWORKS 2026;
  it reads SOLIDWORKS from the outside, so recent versions should work too.
- About 250 MB of disk space (a 70 MB download that unzips to about 165 MB). Nothing else to install: everything it needs, including Microsoft's .NET, is in the download.

### 1. Download

1. Go to this project's **Releases** page on GitHub (on the right-hand side of the main page).
2. Under the newest release, click **Companion-for-SOLIDWORKS-<version>-win-x64.zip** to download it.

### 2. Unzip it somewhere permanent

1. Open your **Downloads** folder, right-click the zip file and choose **Extract All...**
2. Choose a folder you'll keep, for example your **Documents** folder, and click **Extract**.
   You'll get a folder called **Companion-for-SOLIDWORKS-<version>-win-x64**.

(Running it straight from inside the zip file won't work properly; extract it first.)

### 3. Start it

1. Open the folder and double-click **SWCompanion.exe**.
2. The first time, Windows may show **"Windows protected your PC"**. That's because the Companion is free and isn't
   signed with a paid certificate that Windows recognises. Click **More info**, then **Run anyway**. Windows
   remembers your choice, so this only happens once.
3. Optional: right-click **SWCompanion.exe**, choose **Show more options > Send to > Desktop (create shortcut)** for a
   desktop icon, or **Pin to Start**.

### 4. Use it

1. Start SOLIDWORKS as usual (before or after the Companion; it finds SOLIDWORKS by itself within a few seconds).
2. Work normally. Keep the Companion open on a second monitor, or switch to the **Slim strip** (bottom of the left
   bar), which stays on top and widens with an explanation when SOLIDWORKS slows down.
3. When something holds SOLIDWORKS up, it appears in **hung up**. Click it for the explanation and what to try.
4. Look at **Fixes** once: it lists the settings on your PC most likely to slow SOLIDWORKS down, one at a time, with
   the steps. After you change something, press **I did it, check it**.

### Optional extras

- **Run as administrator** (right-click SWCompanion.exe > Run as administrator) for two extra things: seeing exactly
  which files SOLIDWORKS reads and whether Windows Defender held each one up, and reading Defender's list of
  exclusions. Everything else works without it.
- **Ask AI** (Settings > Ask AI): choose Claude, OpenAI (ChatGPT) or "a model on this PC". For Claude or ChatGPT you
  need an **API key** from the provider's developer website (console.anthropic.com or platform.openai.com); a Claude
  Pro or ChatGPT Plus subscription can't be used by other apps. The provider bills your account directly, usually a few
  cents a question. The key is stored encrypted, for your Windows account on this PC only. A model on this PC (for
  example Ollama) is free and nothing leaves your PC.
- **The add-in** isn't needed. The Companion talks to SOLIDWORKS from outside. (Developers: see
  `docs\addin-setup.md`.)

### Updating

Download the new zip from **Releases**, extract it, and use the new folder. Your settings, history and logs are kept
(they live in `%LOCALAPPDATA%\SWCompanion`, not in the program folder). Delete the old folder when you're happy.

### Removing it

1. Close the Companion.
2. Delete its folder (and any shortcuts you made).
3. To remove its settings and logs too, delete the folder `%LOCALAPPDATA%\SWCompanion` (paste that into File
   Explorer's address bar to find it).

Nothing else is installed, so there's nothing in Windows' "Installed apps" to remove.

### If something doesn't work

- **It says SOLIDWORKS isn't open, but it is.** Give it about 10 seconds after SOLIDWORKS finishes starting. If
  SOLIDWORKS runs as administrator, run the Companion as administrator too.
- **"Windows protected your PC" with no "Run anyway".** Your company may block unsigned programs; ask your IT team.
- **Your antivirus quarantines it.** Some antivirus products distrust new, unsigned programs. Restore it from the
  antivirus's quarantine, or ask your IT team to allow it.
- Anything else: e-mail Phillip.Lewis@converge.design, and include the activity log from
  `%LOCALAPPDATA%\SWCompanion\logs` if you can.

---

## Good to know

- It watches from outside SOLIDWORKS, so it sees the effects (processor, memory, disk, network, Windows' own records)
  but not SOLIDWORKS's internal steps. Explanations say when something is a likely cause rather than a certainty.
- PDM features have had limited testing against real vaults so far.

## For developers

Needs the .NET 10 SDK and SOLIDWORKS installed (the build uses SOLIDWORKS's API files from the installation; they are
not part of this repository or the download).

```powershell
dotnet build SWCompanion.slnx
dotnet test tests\SWCompanion.Core.Tests
.\scripts\Publish-Release.ps1      # builds the download zip into dist\
```

## Trademarks

SOLIDWORKS is a registered trademark of Dassault Systèmes SolidWorks Corporation. Windows and Windows Defender are
trademarks of Microsoft. Claude is a trademark of Anthropic; ChatGPT is a trademark of OpenAI. This project is
independent and is not affiliated with, endorsed by, or sponsored by any of them.

## Licence

MIT. Free to use, change and share, as long as the copyright notice stays. See [LICENSE](LICENSE).
