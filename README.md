# KidsLocker

Домашний родительский контроль времени для одного ПК на Windows 11. Один файл
`KidsLocker.exe`: запустил → окно с кнопками → настроил Telegram и расписание → нажал
«Установить». В запрещённые часы компьютер принудительно выключается; управление — с
телефона через Telegram. Сын предупреждений не получает.

> Инструмент для ПК, которым владеет и управляет родитель. Работает прозрачно (служба
> видна в системе) — стойкость строится на правах SYSTEM, избыточности и оповещениях, а
> не на сокрытии. Никаких скрытых процессов и драйверов.

## Как это работает

- Один exe в двух ролях: двойной клик → **окно-пульт** для родителя; тот же файл ставится
  как **служба Windows от SYSTEM** — движок, который каждые ~10с проверяет расписание и в
  запрещённое время делает `shutdown /s /f`.
- Вход в пульт — под мастер-паролем (чтобы сын, тоже админ, им не воспользовался).
- Дублирующие задачи планировщика (вход/старт/каждые 10 мин) выключают ПК, даже если
  службу остановили; задача-guardian раз в минуту её восстанавливает и шлёт алерт.
- Правка файла расписания в обход приложения детектится по хешу, откатывается и алертит.
- Telegram-бот: расписание, `/extra`, `/pause`, `/shutdown`, статус — и алерты родителю.
- Загрузочный слой (BIOS setup-пароль + запрет USB-boot + BitLocker) закрывает обход в
  обход Windows. Ставится руками — см. [docs/hardening.md](docs/hardening.md).

## Быстрый старт

1. Собрать: `powershell -ExecutionPolicy Bypass -File install\Publish.ps1` → `publish\KidsLocker.exe`
   (или скачать готовый — см. [Релиз](#релиз)).
2. Скопировать `KidsLocker.exe` на ПК сына, запустить, задать мастер-пароль.
3. Вкладка Telegram: токен → «Определить автоматически» → «Проверить связь» → «Сохранить».
4. Вкладка Расписание: задать часы → «Сохранить расписание».
5. Вкладка Служба: «Установить (тест)» → проверить → «Установить (боевой)».
6. Загрузочный слой по [docs/hardening.md](docs/hardening.md).

Подробно — [docs/operations.md](docs/operations.md).

## Документация

- [docs/SPEC.md](docs/SPEC.md) — архитектура и честные границы.
- [docs/operations.md](docs/operations.md) — настройка и управление.
- [docs/hardening.md](docs/hardening.md) — BIOS и BitLocker, чек-лист.

## Структура

```
src/KidsLocker.Service/   единый exe: GUI (Gui/), служба (движок), установщик (Setup/), Telegram
tests/KidsLocker.Tests/   юнит-тесты (расписание, парсинг, пароль)
install/Publish.ps1       сборка одного самодостаточного exe
docs/                     SPEC, operations, hardening
.github/workflows/        CI (тесты) и авторелиз по тегу
```

## Разработка

```powershell
dotnet build src\KidsLocker.Service
dotnet test  tests\KidsLocker.Tests
```

Режимы exe: без аргументов (интерактивно) → GUI; как служба → движок; служебные флаги
`--enforce-once [--live]`, `--guard`, `--test-telegram`, `--telegram-updates`,
`--install-eventsource`, `--console`. Установку/удаление делает GUI (в C#, класс `Setup/Installer`).

## Релиз

Готовый `KidsLocker.exe` — на странице [релизов](https://github.com/Ozimnadius/kids-locker/releases):
у каждой версии есть сам exe и `KidsLocker.exe.sha256` с его контрольной суммой.

Выпустить версию `X.Y.Z` — на актуальном `main` поставить тег и отправить его на GitHub:

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

GitHub Actions ([release.yml](.github/workflows/release.yml)) прогонит тесты, соберёт exe с
версией из тега и создаст релиз с автоматическими заметками. Тег с суффиксом
(`vX.Y.Z-rc.1`) станет пре-релизом. Тесты на каждый PR и push в `main` гоняет
[ci.yml](.github/workflows/ci.yml).

Проверить скачанный exe (должно вывести `True`):

```powershell
(Get-FileHash KidsLocker.exe).Hash -eq (Get-Content KidsLocker.exe.sha256).Split(' ')[0]
```
