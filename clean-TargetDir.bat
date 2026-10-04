@rem Deletes the files (not folders) in the Release output folder before a build.
@rem Stops if the folder doesn't exist yet: the loop would otherwise empty the current folder.
if not exist %1 exit /b 0
cd /d %1
for %%i in (*.*) do if not "%%i"==".gitignore" if not "%%i"==".git" del /q "%%i"