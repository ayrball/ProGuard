ProGuard
========

A Windows process manager in the spirit of Process Lasso. It keeps your PC responsive by lowering
the priority of background programs that hog the CPU (ProBalance), and lets you set rules for how
individual programs run.

There are two ways to run it. Both need 64-bit Windows 10 or 11 and nothing else: no .NET install.

  - The installer, ProGuard-<version>-x64.msi (recommended). The engine runs as a Windows service, so
    ProGuard works from startup, for every user, whether or not its window is open.
  - The portable zip, ProGuard-<version>-portable-win-x64.zip (this folder). Nothing is installed; the
    engine runs only while you have it started.

Use one or the other, not both at once.


INSTALL (MSI)
  1. Double-click ProGuard-<version>-x64.msi and approve the administrator prompt.
  2. Choose whether ProGuard's tray icon starts when you sign in (on by default). This is for your
     account only; anyone can change it later in ProGuard's System page.
  3. Open ProGuard from the Start menu.
  Windows may warn about an unknown publisher until releases are code-signed: click "More info" >
  "Run anyway".

  For a silent install, from an administrator command prompt:
      msiexec /i ProGuard-<version>-x64.msi /qn
      msiexec /i ProGuard-<version>-x64.msi /qn START_AT_SIGNIN=0     (no tray icon at sign-in)

  What gets installed:
      C:\Program Files\ProGuard          the program
      ProGuard Engine (ProGuardEngine)   the service; it restarts by itself if it ever crashes
      Start menu > ProGuard              the window
      C:\ProgramData\ProGuard            your settings, rules and logs (created when it first runs)

UPGRADE (MSI)
  Run the newer MSI the same way. It replaces the old version in one step: the service is stopped,
  the files are replaced and the service starts again. Your settings, rules and logs are kept, and so
  is your start-at-sign-in choice. ProGuard's window closes during the upgrade; open it again
  afterwards (the installer offers to). An older MSI won't install over a newer version.

UNINSTALL (MSI)
  Settings > Apps > Installed apps > ProGuard > Uninstall
  (or Control Panel > Programs and Features).
  This stops and removes the service, closes ProGuard everywhere it's open and deletes
  C:\Program Files\ProGuard. Your settings, rules and logs in C:\ProgramData\ProGuard are kept, so
  installing again picks up where you left off.

  To delete them too, either run the MSI again, choose Remove and tick "Also delete ProGuard's
  settings, rules and logs", or from an administrator command prompt:
      msiexec /x ProGuard-<version>-x64.msi /qn REMOVE_DATA=1
  (or delete C:\ProgramData\ProGuard yourself after uninstalling).


PORTABLE: START
  1. Unzip this folder anywhere, e.g. your Desktop. Keep the files together.
  2. Double-click "Start ProGuard.cmd".
     Run it as administrator (right-click > Run as administrator) to let ProGuard manage every
     process, including services. Without it, it only manages your own programs.

PORTABLE: STOP
  Double-click "Stop ProGuard.cmd". Closing the window only hides it to the tray.

PORTABLE: UPGRADE
  Stop it, replace the files in this folder with the new ones, start it again. Settings are kept.

PORTABLE: REMOVE
  Stop it, then delete this folder and C:\ProgramData\ProGuard (its settings, rules and logs).


GOOD TO KNOW
  - Don't run it alongside Process Lasso: both adjust the same processes and will fight. The
    installer warns if it finds Process Lasso but never changes or removes it.
  - Settings and rules are saved in C:\ProgramData\ProGuard, by both the installed and the portable
    version.
  - If something goes wrong, please send:
      %LOCALAPPDATA%\ProGuard\ui-errors.log
      and the files in C:\ProgramData\ProGuard\logs (if any),
    plus a short description of what you were doing. For an installer problem, also send a log:
      msiexec /i ProGuard-<version>-x64.msi /l*v install.log
  - Licensed under the PolyForm Noncommercial License 1.0.0 (LICENSE.txt, NOTICE.txt).
