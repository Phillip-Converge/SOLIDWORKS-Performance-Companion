# The Companion for SOLIDWORKS add-in (optional)

**You don't need the add-in.** Just run the Companion next to SOLIDWORKS: a few seconds after
SOLIDWORKS starts, the Companion connects to it from outside and section **08 inside solidworks**
says "connected: from outside". It hears the same things (file opens, sheet switches, which views
are likely slow) and has a **time each view** button.

**Why you might still want the add-in:** Windows won't let a program running as administrator
connect from outside to a SOLIDWORKS that isn't. File tracing (section 06) needs the Companion to
run as administrator, so without the add-in you choose one or the other. With the add-in
installed, both work together, and "time each view" is in SOLIDWORKS's Tools menu instead.

When the add-in is installed and switched on, the Companion uses it and doesn't connect from
outside, so nothing is reported twice.

## What the add-in does and doesn't do

- It only listens to SOLIDWORKS events. It never changes models, drawings, settings or files.
- It never waits for the Companion. If the Companion isn't running, the notes are simply dropped.
- It talks to the Companion over a private line on this PC (a "named pipe") that only your
  Windows account can open. Nothing goes over the network.
- It keeps its own diary in `%LOCALAPPDATA%\SWCompanion\logs\addin-<date>.log`.

**Right now it reports:** file opens and how long each took, which document is active, when
files close, and drawing sheet switches (step 3b). Rebuild times, assembly component checks
and save times come in the next steps.

## Sheet switches: which view is holding it up

Every sheet switch is timed. When one takes 2 seconds or more, the add-in also reads each view
on the new sheet: what it shows, how many parts, its display style and quality, whether it's
out of date or lightweight, and when its model finished loading. Section 08 then ranks the views
most likely to blame and says why.

That ranking is **evidence, not a stopwatch per view**: SOLIDWORKS doesn't report how long each
view takes. To measure each view:

1. Open the drawing on the slow sheet.
2. In SOLIDWORKS choose **Tools > Companion: Time Each View on This Sheet**.
3. Read the warning and click **OK**. The add-in loads (if needed) and redraws each view one at a
   time and times it. It changes and saves nothing. SOLIDWORKS may afterwards show the drawing as
   changed (an asterisk in the title); closing without saving, or saving, are both fine.
4. Section 08 shows the views slowest first, with seconds for each.

The test only runs when you choose it. Timing views one by one can give a different total from
a real sheet switch, because models already in memory load faster the second time.

## Install (once)

You need administrator rights, because SOLIDWORKS looks for add-ins in a part of the registry
that only administrators can write to.

1. Close SOLIDWORKS.
2. Click Start, type **PowerShell**, right-click **Windows PowerShell** and choose
   **Run as administrator**. Click **Yes** on the Windows prompt.
3. Paste this and press Enter (change the folder if your project is somewhere else):

   ```powershell
   powershell -ExecutionPolicy Bypass -File "C:\Dev\SWCompanion\scripts\Install-AddIn.ps1"
   ```

   `-ExecutionPolicy Bypass` lets this one script run without changing Windows' script settings.
4. It builds the add-in, registers it, and writes a line to the activity log. It should end with
   a green "Done".

   You will see a yellow warning: *"Registering an unsigned assembly with /codebase..."*. That's
   expected and harmless here. It's Windows' registration tool saying the add-in doesn't carry a
   publisher signature, which only matters for add-ins shared between many programs. It is
   still registered.

## Check it works

1. Start the Companion, then SOLIDWORKS.
2. In SOLIDWORKS, open **Tools > Add-Ins**. "Companion for SOLIDWORKS" should be in the list, with
   the right-hand **Start Up** box ticked.
3. In the Companion, section 08 should say **add-in: connected** within a few seconds.
4. Open a part, an assembly and a drawing. Each open should appear in section 08 with its time,
   for example `Opened Bracket.SLDASM in 4.2 s`. Switching between open documents should update
   "Active document".
5. Compare one open time with a stopwatch. They should roughly agree.

If section 08 stays at "not connected":

- Check the add-in is ticked under **Tools > Add-Ins** (tick the left box to load it now).
- Look in `%LOCALAPPDATA%\SWCompanion\logs\addin-<date>.log`. "Loaded" means SOLIDWORKS started
  it; an error there tells us what went wrong.
- If SOLIDWORKS is running **as administrator** and the Companion isn't, Windows keeps them
  apart. Run both the same way.

## Switching it off or removing it

- **Off for now:** untick it under **Tools > Add-Ins**. Nothing else changes.
- **Remove completely:** close SOLIDWORKS, open PowerShell as administrator, and run

  ```powershell
  powershell -ExecutionPolicy Bypass -File "C:\Dev\SWCompanion\scripts\Remove-AddIn.ps1"
  ```

## Updating after a new version

Close SOLIDWORKS and run the install script again. It rebuilds and re-registers in place.
