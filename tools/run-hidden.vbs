' Starts the given command without a console window and returns its exit code.
' Usage: wscript.exe run-hidden.vbs <program> [arguments...]
Option Explicit
Dim shell, command, i, argument
Set shell = CreateObject("WScript.Shell")
command = ""
For i = 0 To WScript.Arguments.Count - 1
    argument = WScript.Arguments(i)
    If InStr(argument, " ") > 0 Then argument = """" & argument & """"
    If i > 0 Then command = command & " "
    command = command & argument
Next
WScript.Quit shell.Run(command, 0, True)
