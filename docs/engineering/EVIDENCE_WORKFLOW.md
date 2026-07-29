# Воспроизводимые evidence bundle

Статус: действует
Дата проверки: 2026-07-30

Этот workflow собирает визуальное доказательство, но не превращает картинку в
источник истины. PNG нужен человеку; manifest связывает его с явными параметрами
кадра, структурированным результатом движка, каноническим checksum и SHA-256.

## Снять bundle

Из корня репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-evidence.ps1 `
  -SpecPath .\evidence\baseline.example.json `
  -OutputRoot evidence\baseline
```

`OutputRoot` всегда относителен к `.artifacts/`. Абсолютный путь и `..`
отвергаются до запуска Godot. Для каждого элемента `captures` скрипт:

1. запускает `scripts/run-game.ps1` с явными параметрами кадра;
2. повторяет тот же capture в отдельный PNG;
3. требует одинаковые fixture, seed, tick и canonical checksum;
4. сравнивает PNG побайтово и по SHA-256;
5. пишет `manifest.json` и `manifest.md`.

Пример создаст:

```text
.artifacts/evidence/baseline/
├── baseline-t1.png
├── baseline-t1.repeat.png
├── manifest.json
└── manifest.md
```

В Git эти файлы не попадают: весь `.artifacts/` игнорируется. `manifest.json`
содержит точную команду для основного и повторного кадра, все параметры spec,
commit, признак dirty worktree, fixture/seed/tick, canonical checksum, оба
SHA-256 и структурированный `view`. `manifest.md` — сокращённый handoff для PR.
Публикуемый финальный bundle снимается после commit и должен иметь
`sourceDirty: false`.

По умолчанию dirty worktree отвергается до запуска Godot. Для локальной
диагностики можно явно добавить `-AllowDirtySource`; такой manifest получит
`reproducible: false` и `publishable: false` и не прикладывается к PR.

Проверить spec без сборки и запуска движка:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-evidence.ps1 `
  -SpecPath .\evidence\baseline.example.json `
  -OutputRoot evidence\baseline `
  -ValidateOnly
```

Имя capture уникально и состоит из lower-case ASCII, цифр, `.`, `_` и `-`.
Pixel-affecting параметры обязательны: `tileSize`, `cameraZoom`,
`cameraPosition`, `uiScale` и `frameSize`. Неизвестное поле считается ошибкой,
чтобы опечатка не превратилась в незаметный default.

## Приложить PNG к PR

GitHub загружает attachment через web Markdown editor: PNG можно перетащить в
поле комментария или выбрать через кнопку attachment. При выборе файл
загружается сразу, а редактор получает anonymized URL. Это официальный
[workflow GitHub для attachments](https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/attaching-files).

REST endpoint комментария принимает только строковое поле
[`body`](https://docs.github.com/en/rest/issues/comments#create-an-issue-comment);
отдельного binary attachment parameter у него нет. Поэтому автоматизация не
придумывает несуществующий API и не коммитит PNG:

1. открыть draft PR в авторизованном браузере;
2. вставить содержимое `manifest.md` в новый комментарий;
3. загрузить основной `*.png` через attachment control;
4. дождаться появления anonymized URL и только после этого отправить комментарий;
5. открыть preview/ссылку и сверить SHA-256 с manifest.

Repeat PNG остаётся локальным доказательством воспроизводимости; прикладывать
обе одинаковые картинки не нужно. Для public repository GitHub предупреждает,
что attachment доступен публично, поэтому кадр не должен содержать секреты или
приватные данные.

## Диагностика GitHub authentication

Проверка не печатает raw output `gh`/Git, URL с userinfo, токены или значения
credential helper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\github-auth.ps1
```

Structured result различает:

- отсутствующий, просроченный или рабочий `gh auth`;
- наличие `origin` и credential helper без публикации их значений;
- rejected/missing credentials, network failure;
- `sandbox_credential_unavailable`, когда Windows credential manager настроен,
  но не смонтирован в Codex sandbox.

Write-доступ проверяется `git push --dry-run` в уникальную probe-ветку,
образованную из текущего commit. Dry-run проходит GitHub receive/auth path, но
не создаёт ref. Обычный `git ls-remote` намеренно не используется как
доказательство: public repository читается анонимно.

Нормальная первичная настройка выполняется в обычном PowerShell владельца:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\github-auth.ps1 `
  -Action Setup
```

Она вызывает официальный `gh auth login` и затем `gh auth setup-git`. Внутри
Codex sandbox `-Action Setup` намеренно отвергается: копирование PAT в аргумент,
файл репозитория или лог не является исправлением. Если внешний PowerShell
успешен, а sandbox сообщает `sandbox_credential_unavailable`, API-операции
выполняются GitHub connector'ом, а `git push` — разрешённой/elevated командой.
Это граница изоляции, а не сломанный credential helper.

## Точный расход токенов

Оценка до запуска и фактическое измерение — разные поля. Если клиент даёт goal
usage API, основной исполнитель и каждый review-субагент создают собственный
goal в начале задачи и завершают его только после всех обязательных проверок.
Финальный handoff пишет:

```yaml
token_usage:
  status: exact
  tokens: <целое число из завершённого goal>
  estimated: 180000-300000
```

Если конкретная среда не предоставляет счётчик, используется не число «на глаз»,
а явное:

```yaml
token_usage:
  status: unavailable
  reason: <какой API/ответ отсутствует>
  estimated: 180000-300000
```

Расход субагента не складывается с основным мысленно: каждый возвращает свою
точную метрику, а ведущий отдельно показывает main, review и сумму. Значение
снимается после завершения goal; промежуточное значение допустимо только в
статусе блока и помечается как `running`.
