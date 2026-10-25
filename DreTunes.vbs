' DreTunes - silent launcher wrapper.
' Runs DreTunes.ps1 with no console window so the shortcut behaves like a normal program.
' Any fatal error is reported by the script itself (message box) and written to
' %LOCALAPPDATA%\DreTunes\launch.log.
Dim sh, here, cmd
Set sh = CreateObject("WScript.Shell")
here = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & here & "DreTunes.ps1"""
sh.Run cmd, 0, False
