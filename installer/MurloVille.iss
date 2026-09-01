; Установщик лаунчера MurloVille.
;
; Ставим в профиль пользователя, а не в Program Files, и это осознанный выбор.
; Программа пока без подписи: Windows и так покажет предупреждение SmartScreen.
; Установка в системную папку потребовала бы прав администратора и добавила бы
; к нему запрос UAC. Два страшных окна подряд — верный способ потерять половину
; желающих поиграть. Побочная выгода: удаление тоже не требует прав.
;
; Папка та же, куда лаунчер ставит себя сам (%LOCALAPPDATA%\Programs\MurloVille).
; Это важно: лаунчер по этому пути понимает, что уже установлен, и не предлагает
; установку второй раз.
;
; Версия приходит снаружи: ISCC /DAppVersion=1.1.0

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{8F3C2A94-6D51-4B7E-9A0C-2E5D7B1F4A63}
AppName=Лаунчер MurloVille
AppVersion={#AppVersion}
AppVerName=Лаунчер MurloVille {#AppVersion}
AppPublisher=MurloVille
AppPublisherURL=https://play.murloville.ru
AppSupportURL=https://play.murloville.ru
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик лаунчера MurloVille

DefaultDirName={localappdata}\Programs\MurloVille
DefaultGroupName=MurloVille
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Без прав администратора: см. пояснение выше.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=MurloVille-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Если лаунчер запущен, он держит свой же файл. Даём Windows закрыть его
; штатно, иначе установка упадёт на замене занятого файла.
CloseApplications=yes
RestartApplications=no

UninstallDisplayName=Лаунчер MurloVille
UninstallDisplayIcon={app}\MurloVille.exe

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"
Name: "autostart";   Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительно:"

[Files]
Source: "..\out\MurloVille.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MurloVille"; Filename: "{app}\MurloVille.exe"
Name: "{autodesktop}\MurloVille";  Filename: "{app}\MurloVille.exe"; Tasks: desktopicon

[Registry]
; Запись, которую мог оставить лаунчер, ставивший себя сам. Без этого в
; «Установленных приложениях» оказалось бы две строки об одной программе.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\MurloVille"; \
    Flags: deletekey uninsdeletekey

; Автозапуск свёрнутым: лаунчер догоняет обновления и не лезет на глаза.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "MurloVille"; ValueType: none; Flags: deletevalue; Tasks: not autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "MurloVille"; ValueType: string; \
    ValueData: """{app}\MurloVille.exe"" --autostart"; \
    Flags: uninsdeletevalue; Tasks: autostart

[UninstallDelete]
; Настройки лаунчера: путь к найденной игре и отметка об отказе от установки.
; Саму игру не трогаем никогда — она лежит отдельно и весит шестнадцать гигабайт.
Type: filesandordirs; Name: "{localappdata}\MurloVille"

[Run]
Filename: "{app}\MurloVille.exe"; Description: "Запустить лаунчер"; \
    Flags: nowait postinstall skipifsilent
