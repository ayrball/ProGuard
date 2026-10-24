# strings.json -> Strings.resx -> Strings.cs, then build the solution.
$s = $PSScriptRoot
python "$s\build_resx.py" "$s\strings.json" E:\projects\proguard-wt\i18n\src\ProGuard.UI\Resources\Strings.resx
& E:\projects\proguard-wt\i18n\tools\i18n\Update-Strings.ps1
dotnet build E:\projects\proguard-wt\i18n\ProGuard.sln -nologo -v q 2>&1 | Select-String 'error|warning CS|Warning\(s\)|Error\(s\)' | Select-Object -First 15
