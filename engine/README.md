# Обновление VirtualDub2 и модуля ввода

В `upstream.json` записаны адреса репозиториев и полные идентификаторы проверенных коммитов. `patches/host.patch` содержит встраивание окна, управление с клавиатуры, оформление и приём файлов. `patches/decoder.patch` содержит изменения avlib для AVC/HEVC, M2TS, MPLS, CLPI, образов Blu-ray ISO (UDF) и метаданных. Мост SDK собирается из `bridge/`.

## Попробовать другую ревизию

Из корня проекта выполните команды ниже. Для возврата к проверенной версии опустите `--revision`. Параметр `--mirror` позволяет взять коммит из локальной копии Git.

```powershell
python engine/prepare.py host C:/Build/vd-trial --revision <полный-SHA-коммита>
./engine/build-host.ps1 -Checkout C:/Build/vd-trial
```

Скрипт сначала проверяет весь патч и только после успешного применения переносит подготовленный каталог на место. При конфликте остаётся доступен вывод Git с именем файла и контекстом. Рабочие копии и предыдущие сборки сохраняются. Для декодера используйте компонент `decoder` и `build-decoder.ps1 -Checkout ... -RuntimeDirectory ...`.

Для быстрого прогона скопируйте готовый выпуск в тестовую папку и закройте запущенную из неё программу. Из `_out/Release_x64/` перенесите в тестовую `Engine/` оба файла — `VirtualDub2.exe` и `VirtualDub2.vdi`. При изменениях декодера замените `Engine/plugins64/avlib-1.vdplugin` файлом из `_bin/Release_x64/`. Затем выполните `python tests/file_drop.py <тестовая-папка>`. Для изменений в декодировании дополнительно проверьте на своём диске выбор MPLS и точный переход на знакомый кадр после перемотки.

При конфликте подготовьте отдельный полный клон upstream, переключитесь на новый SHA и выполните `git apply --3way --index <путь-к-патчу>`. Разрешите конфликты и добавьте изменённые файлы в индекс. Для сохранения исправлений выполните в этом клоне команду ниже, подставив путь к патчу в проекте. Вывод Git в файл сохраняет байты патча также в Windows PowerShell.

```powershell
git diff --cached --binary --full-index --output=C:/Build/project/engine/patches/host.patch HEAD
```

После проверки укажите новый SHA в `upstream.json`, проверьте повторное применение в пустую папку и зафиксируйте патч и SHA вместе. При смене ABI FFmpeg также обновите зависимости, их контрольные суммы и настройку импортных библиотек в `build-decoder.ps1`.

## Собрать проверяемый выпуск

Нужны Git, Visual Studio 2022 Community с C++ и Windows SDK, Yasm в PATH или параметр `-YasmDirectory`, Python 3.11+ с pywin32, а также ffmpeg и ffprobe в PATH для создания тестовых роликов и образа UDF. Параметр `-RuntimeDirectory` задаёт каталог готовых сторонних библиотек. Для локальной сборки положите туда файлы с путями и SHA-256 из `engine/runtime-files.json`. Подойдёт и каталог `Engine` из совместимой сборки.

```powershell
./build-release.ps1 -WorkDirectory ./.local/build-work -RuntimeDirectory ./.local/runtime -OutputDirectory ./out
```

Скрипт использует отдельные каталоги для каждой комбинации патча и ревизии. Повторные сборки используют уже собранные объекты. Для публикации берутся зафиксированные исходники из чистого локального Git-репозитория, выборочные файлы движка и начальный INI. Тесты открывают синтетические ролики и два плейлиста из одного ISO в копии программы на отдельном рабочем столе Windows. Для разработки из архива готовые пропатченные деревья находятся в `upstream/host` и `upstream/decoder`.

В публичном репозитории принят автор коммитов `SyncSyncAV contributors` с адресом `project@localhost.invalid`. Настройте его локально перед первым коммитом в новом клоне.

```powershell
git config --local user.name "SyncSyncAV contributors"
git config --local user.email project@localhost.invalid
git config --local core.hooksPath .githooks
```

Локальные хуки проверяют имя и почту автора и коммитера, подготовленные файлы перед коммитом и историю перед push. Упаковщик отдельно проверяет достижимую историю Git и содержимое обоих ZIP, включая вложенные архивы, по данным локального профиля и глобального Git. Дополнительные личные адреса и имена можно передать локально через `SYNCSYNCAV_PRIVATE_TERMS`, по одному на строку. В отчёте проверки выводится имя файла. Публичная сборка не включает снимки экрана, журналы, настройки сеанса или использованные для ручной проверки фильмы.

## Библиотеки Blu-ray

Исходный архив libbluray 1.5.0, порт libudfread 1.2.0, его исправление MSVC и уведомления об авторстве находятся в `dependencies/`. Для libbluray используются Meson, shared release, `--vsenv`, отключённые BD-J, fontconfig, freetype, libxml2, tools и examples. Для libudfread примените `libudfread-msvc.diff` к исходнику версии из порта, выполните `meson setup build --vsenv --buildtype=release -Ddefault_library=shared -Denable_examples=false` и `meson compile -C build`. Сборка в нейтральном каталоге с параметром линкера `/PDBALTPATH:%_PDB%` исключает путь к личному профилю из записи PDB.
